using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WardogsRadio.Core;
using WardogsRadio.Playback;
using WardogsRadio.Voicemeeter;

namespace WardogsRadio.App;

public partial class MainWindow
{
    sealed class SignalBar : INotifyPropertyChanged
    {
        double _height = 3;
        public event PropertyChangedEventHandler? PropertyChanged;
        public double Height { get => _height; set { if (Math.Abs(_height - value) < .1) return; _height = value; PropertyChanged?.Invoke(this, new(nameof(Height))); } }
    }

    readonly ObservableCollection<SignalBar> _nowPlayingBars = new(Enumerable.Range(0, 14).Select(_ => new SignalBar()));
    bool _syncingNowPlayingStationSelector;
    bool _nowPlayingTimelineInteraction;
    double? _nowPlayingDurationSeconds;
    double _nowPlayingLevelTarget;
    List<Station> _dockStationChoices = [];
    readonly NowPlayingSurfaceStateMachine _nowPlayingSurfaceState = new();
    bool _nowPlayingTransitionTargetExpanded;
    bool _youtubeOccludedByNowPlaying;
    // Scalar level display only (not an FFT/spectrum).  Keep it deliberately
    // cheap while restoring reliable playback.
    readonly DispatcherTimer _nowPlayingVisualizerTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    void InitializeNowPlayingSurface()
    {
        DockVisualizerBars.ItemsSource = _nowPlayingBars;
        ReducedMotionCheck.IsChecked = _config.ReduceMotion;
        _nowPlayingVisualizerTimer.Tick += (_, _) => RenderNowPlayingVisualizer();
        _nowPlayingVisualizerTimer.Start();
        Dispatcher.BeginInvoke(ApplyCompactNowPlayingSurface, DispatcherPriority.Loaded);
        RefreshNowPlayingPresentation();
    }

    void StopNowPlayingSurface() => _nowPlayingVisualizerTimer.Stop();

    void RefreshNowPlayingPresentation()
    {
        var presentation = GetPlaybackPresentation();
        var station = presentation.Station;
        _syncingNowPlayingStationSelector = true;
        try
        {
            var choices = _config.Profile.Stations.Where(candidate => candidate.Enabled).OrderBy(candidate => candidate.Order).ToList();
            // Do not recreate the ComboBox items during normal playback telemetry;
            // that would close its station picker while an operator is selecting.
            if (!SameStationChoices(_dockStationChoices, choices))
            {
                _dockStationChoices = choices;
                DockStationSelector.ItemsSource = _dockStationChoices;
            }
            DockStationSelector.SelectedItem = station;
        }
        finally { _syncingNowPlayingStationSelector = false; }

        if (station is null)
        {
            DockTrackText.Text = "SELECT A STATION";
            DockArtistText.Text = "Your active radio channel will appear here.";
            SurfaceStationState.Text = "AWAITING SIGNAL";
            SurfaceUpNextText.Text = "No queued track";
            SurfaceRouteText.Text = "ROUTE IDLE";
            SetNowPlayingTimeline(new NowPlayingTimeline(0, null));
            SetNowPlayingTransport(false);
            return;
        }

        MusicLibraryService.MaterializeStationPlaylist(_config, station);
        var index = station.ProviderId == "mpv" ? _mpvProvider?.CurrentPlaylistIndex ?? station.Runtime.SequenceIndex : station.Runtime.SequenceIndex;
        index = Math.Clamp(index, 0, Math.Max(0, station.PlaylistSongs.Count - 1));
        var song = presentation.Song ?? station.PlaylistSongs.ElementAtOrDefault(index);
        var providerSnapshot = _mpvProvider?.Snapshot ?? _externalProvider?.Snapshot;
        var title = song?.Name;
        if (string.IsNullOrWhiteSpace(title)) title = providerSnapshot?.Track?.Title;
        if (string.IsNullOrWhiteSpace(title)) title = station.Name;
        var artist = song is null ? null : _config.MusicLibrary.Songs.FirstOrDefault(candidate => candidate.Id == song.Id)?.Artist;
        if (string.IsNullOrWhiteSpace(artist)) artist = providerSnapshot?.Track?.Artist;
        DockTrackText.Text = title;
        DockArtistText.Text = CompactNowPlayingMetadata.Format(station, artist);
        SurfaceStationState.Text = NowPlayingState(presentation);
        SurfaceRouteText.Text = _outputTelemetry.Confidence == OutputTelemetryConfidence.Unavailable ? "ROUTE UNAVAILABLE" :
            _outputTelemetry.Confidence == OutputTelemetryConfidence.Conflicting ? "ROUTE NEEDS CHECK" : "ROUTE MONITORED";
        SurfaceUpNextText.Text = NextSongText(station, index);
        SetNowPlayingTimeline(presentation.LogicalSongPosition is { } position
            ? new NowPlayingTimeline(position, presentation.LogicalSongDuration) : new NowPlayingTimeline(0, null));
        SetNowPlayingTransport(presentation.IsPlaying);
    }

    PlaybackPresentationState GetPlaybackPresentation()
    {
        if (_active is not { } station) return PlaybackPresentationState.Idle;
        var index = station.ProviderId == "mpv" ? _mpvProvider?.CurrentPlaylistIndex ?? station.Runtime.SequenceIndex : station.Runtime.SequenceIndex;
        var song = station.PlaylistSongs.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(0, station.PlaylistSongs.Count - 1)));
        if (station.ProviderId == "youtube")
        {
            var kind = _youtubeRouteRecoveryBlocked ? PlaybackPresentationKind.RouteRepairRequired :
                _youtubePlayerErrorDetail is not null ? PlaybackPresentationKind.Error :
                !_youtubePlayerReady ? PlaybackPresentationKind.Loading :
                station.Runtime.WasPlaying ? PlaybackPresentationKind.Playing : PlaybackPresentationKind.Paused;
            // A persisted resume point is not player telemetry. The embedded page must
            // send one current progress message before a YouTube timeline is exposed.
            var hasLiveTimeline = _youtubePlayerReady && _youtubeHasLiveTimeline;
            return PlaybackPresentationState.Create(station, song,
                _youtubePlayerErrorDetail is null ? ProviderHealth.Ready : ProviderHealth.Failed,
                kind, _youtubePlayerReady, station.Runtime.WasPlaying,
                hasLiveTimeline ? station.Runtime.PositionSeconds : null,
                hasLiveTimeline ? station.Runtime.DurationSeconds : null);
        }

        var snapshot = station.ProviderId == "mpv" ? _mpvProvider?.Snapshot : _externalProvider?.Snapshot;
        var ready = snapshot?.Health == ProviderHealth.Ready;
        var nativeKind = snapshot?.Health is ProviderHealth.Failed or ProviderHealth.Unavailable ? PlaybackPresentationKind.Unavailable :
            !ready ? PlaybackPresentationKind.Loading : snapshot!.IsPlaying ? PlaybackPresentationKind.Playing : PlaybackPresentationKind.Paused;
        return PlaybackPresentationState.Create(station, song, snapshot?.Health ?? ProviderHealth.Unknown, nativeKind,
            ready, snapshot?.IsPlaying ?? false, snapshot?.PositionSeconds, snapshot?.DurationSeconds);
    }

    void RefreshSharedPlaybackPresentation()
    {
        var presentation = GetPlaybackPresentation();
        if (presentation.LogicalSongPosition is { } position && presentation.LogicalSongDuration is { } duration)
        {
            TimeText.Text = $"{DisplayTime(position)} / {DisplayTime(duration)}";
            if (!_timelineDragging) Progress.Value = duration > 0 ? Math.Clamp(100 * position / duration, 0, 100) : 0;
        }
        else
        {
            TimeText.Text = "--:-- / --:--";
            if (!_timelineDragging) Progress.Value = 0;
        }
        PlayButton.Content = presentation.IsPlaying ? "Ⅱ  PAUSE" : "▶  PLAY";
        RefreshNowPlayingPresentation();
    }

    static bool SameStationChoices(IReadOnlyList<Station> left, IReadOnlyList<Station> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Id == pair.Second.Id &&
            pair.First.Name == pair.Second.Name && pair.First.IconId == pair.Second.IconId &&
            pair.First.AccentColor == pair.Second.AccentColor && pair.First.Order == pair.Second.Order);

    static string NowPlayingState(PlaybackPresentationState presentation)
    {
        return presentation.Kind switch
        {
            PlaybackPresentationKind.Playing => "● ON AIR",
            PlaybackPresentationKind.Loading => "◌ ACQUIRING SIGNAL",
            PlaybackPresentationKind.Ready => "◌ AWAITING LIVE TIMELINE",
            PlaybackPresentationKind.RouteRepairRequired => "! ROUTE REPAIR REQUIRED",
            PlaybackPresentationKind.Error => "! PLAYER ERROR",
            PlaybackPresentationKind.Unavailable => "! SOURCE UNAVAILABLE",
            _ => "Ⅱ PAUSED"
        };
    }

    static string NextSongText(Station station, int index)
    {
        if (station.PlaylistSongs.Count == 0) return "No queued track";
        var next = index + 1;
        if (next >= station.PlaylistSongs.Count)
            return station.EffectiveRepeatMode == StationRepeatMode.Off ? "End of station queue" : station.PlaylistSongs[0].Name;
        return station.PlaylistSongs[next].Name;
    }

    void SetNowPlayingTransport(bool playing)
    {
        SurfacePlayIcon.Text = playing ? "Ⅱ" : "▶";
        SurfacePlayLabel.Text = playing ? "  PAUSE" : "  PLAY";
    }

    void SetNowPlayingTimeline(NowPlayingTimeline timeline)
    {
        _nowPlayingDurationSeconds = timeline.DurationSeconds;
        var elapsed = DisplayTime(timeline.PositionSeconds);
        var duration = timeline.DurationSeconds is { } known ? DisplayTime(known) : "--:--";
        DockElapsedText.Text = elapsed;
        DockDurationText.Text = duration;
        if (!_nowPlayingTimelineInteraction)
            DockTimeline.Value = timeline.ProgressPercent;
        DockTimeline.IsEnabled = timeline.CanSeek;
    }

    void UpdateNowPlayingVisualizer(SignalLevel music, SignalLevel monitor)
    {
        // A configured Voicemeeter strip can be available but silent when local
        // MPV plays directly to the selected headphones. Fall back on a silent
        // strip as well as an unavailable one, so the dock follows actual audio.
        var source = music.Available && music.Peak > VoicemeeterSignalMonitor.VisualSignalFloor ? music : monitor;
        var loadingOrSwitching = _activationGate.CurrentCount == 0 ||
            _active?.ProviderId == "youtube" && (!_youtubePlayerReady || _youtubePlayerErrorDetail is not null) ||
            _youtubeRouteRecoveryBlocked;
        _nowPlayingLevelTarget = loadingOrSwitching ? 0 : source.Available
            ? VoicemeeterSignalMonitor.BarValue(source) / 100d : 0;
    }

    void RenderNowPlayingVisualizer()
    {
        for (var index = 0; index < _nowPlayingBars.Count; index++)
        {
            // A shared source-level signal is rendered as a restrained, deterministic meter pattern.
            var shape = .44 + ((index * 5 + 3) % 7) * .075;
            // Keep the compact dock geometry unchanged, but use more of its
            // existing height range so live audio is easier to read at a glance.
            var desired = 3 + _nowPlayingLevelTarget * 52 * shape;
            var previous = _nowPlayingBars[index].Height;
            _nowPlayingBars[index].Height = _config.ReduceMotion ? desired : desired >= previous
                ? previous + (desired - previous) * .58 // quick attack
                : Math.Max(3, previous - 1.55); // slower signal decay
        }
    }

    async void DockStationSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingNowPlayingStationSelector || !IsLoaded || DockStationSelector.SelectedItem is not Station station || station.Id == _active?.Id) return;
        await ActivateAsync(station);
        RefreshNowPlayingPresentation();
    }

    void NowPlayingExpand_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ToggleNowPlayingSurface();
    }

    static bool IsNowPlayingInteractiveTarget(object source) => source is DependencyObject node &&
        (FindAncestor<ButtonBase>(node) is not null || FindAncestor<ComboBox>(node) is not null ||
         FindAncestor<Slider>(node) is not null || FindAncestor<Thumb>(node) is not null);

    static T? FindAncestor<T>(DependencyObject source) where T : DependencyObject
    {
        for (var node = source; node is not null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
            if (node is T match) return match;
        return null;
    }

    void NowPlayingSurface_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsNowPlayingInteractiveTarget(e.OriginalSource)) ToggleNowPlayingSurface();
    }

    void NowPlayingSurface_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource != NowPlayingSurface || e.Key is not (Key.Enter or Key.Space)) return;
        e.Handled = true;
        ToggleNowPlayingSurface();
    }

    void NowPlayingBackdrop_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        RequestNowPlayingSurface(false);
    }

    void NowPlayingOverlay_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_nowPlayingSurfaceState.State is NowPlayingSurfaceState.Compact or NowPlayingSurfaceState.Expanded)
            ApplyNowPlayingSurfaceLayout(_nowPlayingSurfaceState.State == NowPlayingSurfaceState.Expanded, animate: false, TimeSpan.Zero);
    }

    bool IsNowPlayingSurfaceExpanded => _nowPlayingSurfaceState.State is not NowPlayingSurfaceState.Compact;

    void ToggleNowPlayingSurface() => RequestNowPlayingSurface(_nowPlayingSurfaceState.State is NowPlayingSurfaceState.Compact or NowPlayingSurfaceState.Collapsing);

    void RequestNowPlayingSurface(bool expanded)
    {
        var before = _nowPlayingSurfaceState.State;
        var after = _nowPlayingSurfaceState.Request(expanded);
        if (before == after) return;

        if (_config.ReduceMotion)
        {
            StopNowPlayingSurfaceAnimations();
            _nowPlayingSurfaceState.SetImmediately(expanded);
            SurfaceToggleButton.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            SetYouTubeNowPlayingOcclusion(expanded);
            ApplyNowPlayingSurfaceLayout(expanded, animate: false, TimeSpan.Zero);
            SetNowPlayingBackdrop(expanded, animate: false, TimeSpan.Zero);
            RefreshNowPlayingPresentation();
            return;
        }

        BeginNowPlayingSurfaceTransition(expanded);
    }

    void BeginNowPlayingSurfaceTransition(bool expanded)
    {
        _nowPlayingTransitionTargetExpanded = expanded;
        if (expanded) SurfaceToggleButton.Visibility = Visibility.Visible;
        if (expanded) SetYouTubeNowPlayingOcclusion(true);
        var duration = TimeSpan.FromMilliseconds(expanded ? 300 : 230);
        if (expanded) NowPlayingBackdrop.Visibility = Visibility.Visible;
        NowPlayingSurface.IsHitTestVisible = true;
        ApplyNowPlayingSurfaceLayout(expanded, animate: true, duration);
        SetNowPlayingBackdrop(expanded, animate: true, duration);

        NowPlayingSurface.BeginAnimation(FrameworkElement.HeightProperty, null);
        var resize = new DoubleAnimation(NowPlayingSurface.ActualHeight, SurfaceHeight(expanded), duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        resize.Completed += (_, _) => CompleteNowPlayingSurfaceTransition();
        NowPlayingSurface.BeginAnimation(FrameworkElement.HeightProperty, resize);
    }

    void CompleteNowPlayingSurfaceTransition()
    {
        var completedExpanded = _nowPlayingTransitionTargetExpanded;
        ApplyNowPlayingSurfaceLayout(completedExpanded, animate: false, TimeSpan.Zero);
        SetNowPlayingBackdrop(completedExpanded, animate: false, TimeSpan.Zero);
        if (!completedExpanded) SetYouTubeNowPlayingOcclusion(false);
        var next = _nowPlayingSurfaceState.CompleteTransition();
        if (next is NowPlayingSurfaceState.Expanding or NowPlayingSurfaceState.Collapsing)
            BeginNowPlayingSurfaceTransition(_nowPlayingSurfaceState.RequestedExpanded);
        else
        {
            if (next == NowPlayingSurfaceState.Compact) SurfaceToggleButton.Visibility = Visibility.Collapsed;
            NowPlayingSurface.IsHitTestVisible = true;
        }
    }

    void ApplyCompactNowPlayingSurface() => ApplyNowPlayingSurfaceLayout(expanded: false, animate: false, TimeSpan.Zero);

    double SurfaceHeight(bool expanded) => expanded
        ? Math.Clamp(NowPlayingOverlay.ActualHeight * .52, 330, 420)
        : 64;

    void ApplyNowPlayingSurfaceLayout(bool expanded, bool animate, TimeSpan duration)
    {
        var width = NowPlayingSurface.ActualWidth;
        if (width < 1) return;

        var height = SurfaceHeight(expanded);
        if (!animate) NowPlayingSurface.Height = height;
        NowPlayingSurface.CornerRadius = new CornerRadius(expanded ? 12 : 8);

        var compact = CompactNowPlayingLayout.Create(width);
        var trackWidth = expanded ? Math.Min(580, width - 260) : compact.TrackWidth;
        var timelineWidth = expanded ? Math.Max(280, width - 108) : compact.TimelineWidth;
        SetSurfaceElementLayout(DockStationSelector, expanded ? 18 : compact.StationLeft, expanded ? 17 : 12, expanded ? 274 : compact.StationWidth, animate, duration);
        SetSurfaceElementLayout(SurfaceStationState, 20, 59, 250, animate, duration);
        SetSurfaceElementLayout(SurfaceTrackPanel, expanded ? (width - trackWidth) / 2 : compact.TrackLeft, expanded ? 91 : 14, trackWidth, animate, duration);
        SetSurfaceElementLayout(DockVisualizerBars, expanded ? (width - 98) / 2 : compact.VisualizerLeft, expanded ? 159 : 11, expanded ? 98 : compact.VisualizerWidth, animate, duration);
        SetSurfaceElementLayout(SurfaceTimeline, expanded ? 54 : compact.TimelineLeft, expanded ? 237 : 20, timelineWidth, animate, duration);
        SetSurfaceElementLayout(SurfaceTransport, expanded ? (width - 340) / 2 : compact.TransportLeft, expanded ? 287 : 13, expanded ? 340 : compact.TransportWidth, animate, duration);
        SetSurfaceElementLayout(SurfaceToggleButton, width - 132, 14, expanded ? 116 : 36, animate, duration);
        SetSurfaceElementLayout(SurfaceSecondaryInfo, 18, Math.Min(height - 55, 348), Math.Max(300, width - 36), animate, duration);

        SetSurfaceScale(DockVisualizerBars, expanded ? 1.45 : 1, expanded ? 1.32 : 1, animate, duration);
        AnimateSurfaceValue(DockTrackText, TextBlock.FontSizeProperty, expanded ? 26 : 16, animate, duration);
        AnimateSurfaceValue(DockArtistText, TextBlock.FontSizeProperty, expanded ? 13 : 10, animate, duration);
        DockTrackText.TextAlignment = expanded ? TextAlignment.Center : TextAlignment.Left;
        DockArtistText.TextAlignment = expanded ? TextAlignment.Center : TextAlignment.Left;
        DockArtistText.Visibility = expanded || compact.ShowSecondaryMetadata ? Visibility.Visible : Visibility.Collapsed;
        SurfaceToggleIcon.Text = expanded ? "⌄" : "⌃";
        AnimateSurfaceValue(SurfaceStationState, UIElement.OpacityProperty, expanded ? 1 : 0, animate, duration);
        AnimateSurfaceValue(SurfaceSecondaryInfo, UIElement.OpacityProperty, expanded ? 1 : 0, animate, duration, expanded ? TimeSpan.FromMilliseconds(150) : TimeSpan.Zero);
        AnimateSurfaceValue(SurfacePreviousLabel, UIElement.OpacityProperty, expanded ? 1 : 0, animate, duration);
        AnimateSurfaceValue(SurfacePlayLabel, UIElement.OpacityProperty, expanded ? 1 : 0, animate, duration);
        AnimateSurfaceValue(SurfaceNextLabel, UIElement.OpacityProperty, expanded ? 1 : 0, animate, duration);
        AnimateSurfaceValue(SurfaceToggleLabel, UIElement.OpacityProperty, expanded ? 1 : 0, animate, duration);
        AnimateSurfaceValue(SurfacePreviousLabel, FrameworkElement.WidthProperty, expanded ? 80 : 0, animate, duration);
        AnimateSurfaceValue(SurfacePlayLabel, FrameworkElement.WidthProperty, expanded ? 58 : 0, animate, duration);
        AnimateSurfaceValue(SurfaceNextLabel, FrameworkElement.WidthProperty, expanded ? 64 : 0, animate, duration);
        AnimateSurfaceValue(SurfaceToggleLabel, FrameworkElement.WidthProperty, expanded ? 74 : 0, animate, duration);
        SurfaceSecondaryInfo.IsHitTestVisible = expanded;
    }

    void SetYouTubeNowPlayingOcclusion(bool occlude)
    {
        // Composition-hosted WebView content can ignore WPF sibling z-order on
        // some GPU paths. While the drawer is expanded, hide only its visual
        // surface; the iframe and its audio continue running underneath.
        if (occlude)
        {
            if (_active?.ProviderId == "youtube" && YouTubeView.Visibility == Visibility.Visible)
            {
                _youtubeOccludedByNowPlaying = true;
                YouTubeView.Visibility = Visibility.Hidden;
            }
            return;
        }

        if (!_youtubeOccludedByNowPlaying) return;
        _youtubeOccludedByNowPlaying = false;
        if (_active?.ProviderId == "youtube" && !_youtubeRouteRecoveryBlocked && _youtubePlayerErrorDetail is null)
            YouTubeView.Visibility = Visibility.Visible;
    }

    void SetNowPlayingBackdrop(bool expanded, bool animate, TimeSpan duration)
    {
        var wasHidden = NowPlayingBackdrop.Visibility != Visibility.Visible;
        NowPlayingBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
        if (expanded)
        {
            NowPlayingBackdrop.Visibility = Visibility.Visible;
            if (animate && wasHidden) NowPlayingBackdrop.Opacity = 0;
            AnimateSurfaceValue(NowPlayingBackdrop, UIElement.OpacityProperty, 1, animate, duration);
        }
        else if (!animate)
        {
            NowPlayingBackdrop.Opacity = 1;
            NowPlayingBackdrop.Visibility = Visibility.Collapsed;
        }
        else
        {
            var fade = new DoubleAnimation(NowPlayingBackdrop.Opacity, 0, duration);
            fade.Completed += (_, _) =>
            {
                if (_nowPlayingSurfaceState.State is NowPlayingSurfaceState.Compact or NowPlayingSurfaceState.Collapsing)
                {
                    NowPlayingBackdrop.Opacity = 1;
                    NowPlayingBackdrop.Visibility = Visibility.Collapsed;
                }
            };
            NowPlayingBackdrop.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }

    static void SetSurfaceElementLayout(FrameworkElement element, double left, double top, double width, bool animate, TimeSpan duration)
    {
        var previousLeft = double.IsNaN(Canvas.GetLeft(element)) ? 0 : Canvas.GetLeft(element);
        var previousTop = double.IsNaN(Canvas.GetTop(element)) ? 0 : Canvas.GetTop(element);
        var previousWidth = double.IsNaN(element.Width) ? element.ActualWidth : element.Width;
        Canvas.SetLeft(element, left);
        Canvas.SetTop(element, top);
        element.Width = width;
        var (translate, _) = SurfaceTransforms(element);
        if (!animate)
        {
            element.BeginAnimation(FrameworkElement.WidthProperty, null);
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.X = 0;
            translate.Y = 0;
            return;
        }
        translate.BeginAnimation(TranslateTransform.XProperty, null);
        translate.BeginAnimation(TranslateTransform.YProperty, null);
        translate.X = previousLeft - left;
        translate.Y = previousTop - top;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
        translate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, duration) { EasingFunction = easing });
        element.BeginAnimation(FrameworkElement.WidthProperty, new DoubleAnimation(previousWidth, width, duration) { EasingFunction = easing });
    }

    static void SetSurfaceScale(FrameworkElement element, double x, double y, bool animate, TimeSpan duration)
    {
        var (_, scale) = SurfaceTransforms(element);
        if (!animate)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = x;
            scale.ScaleY = y;
            return;
        }
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(scale.ScaleX, x, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(scale.ScaleY, y, duration) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    static (TranslateTransform Translate, ScaleTransform Scale) SurfaceTransforms(FrameworkElement element)
    {
        if (element.RenderTransform is TransformGroup group && group.Children.OfType<TranslateTransform>().FirstOrDefault() is { } existingTranslate && group.Children.OfType<ScaleTransform>().FirstOrDefault() is { } existingScale)
            return (existingTranslate, existingScale);
        var scale = new ScaleTransform();
        var translate = new TranslateTransform();
        element.RenderTransform = new TransformGroup { Children = [scale, translate] };
        element.RenderTransformOrigin = new Point(.5, .5);
        return (translate, scale);
    }

    static void AnimateSurfaceValue(DependencyObject target, DependencyProperty property, double value, bool animate, TimeSpan duration, TimeSpan? beginTime = null)
    {
        var animatable = (IAnimatable)target;
        if (!animate) { animatable.BeginAnimation(property, null); target.SetValue(property, value); return; }
        var current = target.GetValue(property) is double existing ? existing : 0;
        animatable.BeginAnimation(property, new DoubleAnimation(current, value, duration) { BeginTime = beginTime, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    void StopNowPlayingSurfaceAnimations()
    {
        NowPlayingSurface.BeginAnimation(FrameworkElement.HeightProperty, null);
        NowPlayingBackdrop.BeginAnimation(UIElement.OpacityProperty, null);
    }

    async void NowPlayingTimeline_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _nowPlayingTimelineInteraction = false;
        if (sender is Slider slider) await SeekNowPlayingLogicalAsync(_nowPlayingDurationSeconds is { } duration ? duration * slider.Value / 100d : 0);
    }

    void NowPlayingTimeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _nowPlayingTimelineInteraction = true;

    async void NowPlayingTimeline_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End) || sender is not Slider slider) return;
        await SeekNowPlayingLogicalAsync(_nowPlayingDurationSeconds is { } duration ? duration * slider.Value / 100d : 0);
    }

    async Task SeekNowPlayingLogicalAsync(double logicalSeconds)
    {
        if (_active is not { } station || _nowPlayingDurationSeconds is not > 0) return;
        var index = station.ProviderId == "mpv" ? _mpvProvider?.CurrentPlaylistIndex ?? station.Runtime.SequenceIndex : station.Runtime.SequenceIndex;
        var song = station.PlaylistSongs.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(0, station.PlaylistSongs.Count - 1)));
        var sourceSeconds = NowPlayingTimeline.ToSourcePosition(logicalSeconds, song?.StartSeconds ?? 0, song?.EndSeconds, station.Runtime.DurationSeconds);
        try { await SeekTimelineAsync(sourceSeconds); }
        catch (Exception error) { Footer.Text = "SEEK FAILED · " + error.Message; }
        finally { RefreshNowPlayingPresentation(); }
    }

    async void ReducedMotion_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        _config.ReduceMotion = ReducedMotionCheck.IsChecked == true;
        try { await _store.SaveAsync(_config); }
        catch (Exception error) { Footer.Text = "MOTION SETTING NOT SAVED · " + error.Message; }
    }
}
