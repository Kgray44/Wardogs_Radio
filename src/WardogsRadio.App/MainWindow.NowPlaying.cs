using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    readonly DispatcherTimer _nowPlayingVisualizerTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };

    void InitializeNowPlayingSurface()
    {
        DockVisualizerBars.ItemsSource = _nowPlayingBars;
        DrawerVisualizerBars.ItemsSource = _nowPlayingBars;
        ReducedMotionCheck.IsChecked = _config.ReduceMotion;
        _nowPlayingVisualizerTimer.Tick += (_, _) => RenderNowPlayingVisualizer();
        _nowPlayingVisualizerTimer.Start();
        RefreshNowPlayingPresentation();
    }

    void StopNowPlayingSurface() => _nowPlayingVisualizerTimer.Stop();

    void RefreshNowPlayingPresentation()
    {
        var station = _active;
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
            DockTrackText.Text = DrawerTrackText.Text = "SELECT A STATION";
            DockArtistText.Text = DrawerArtistText.Text = "Your active radio channel will appear here.";
            DrawerStationText.Text = "NO STATION";
            DrawerStateText.Text = "AWAITING SIGNAL";
            DrawerUpNextText.Text = "No queued track";
            DrawerRouteText.Text = "ROUTE IDLE";
            SetNowPlayingTimeline(new NowPlayingTimeline(0, null));
            SetNowPlayingTransport(false);
            return;
        }

        MusicLibraryService.MaterializeStationPlaylist(_config, station);
        var index = station.ProviderId == "mpv" ? _mpvProvider?.CurrentPlaylistIndex ?? station.Runtime.SequenceIndex : station.Runtime.SequenceIndex;
        index = Math.Clamp(index, 0, Math.Max(0, station.PlaylistSongs.Count - 1));
        var song = station.PlaylistSongs.ElementAtOrDefault(index);
        var providerSnapshot = _mpvProvider?.Snapshot ?? _externalProvider?.Snapshot;
        var title = song?.Name;
        if (string.IsNullOrWhiteSpace(title)) title = providerSnapshot?.Track?.Title;
        if (string.IsNullOrWhiteSpace(title)) title = station.Name;
        var artist = song is null ? null : _config.MusicLibrary.Songs.FirstOrDefault(candidate => candidate.Id == song.Id)?.Artist;
        if (string.IsNullOrWhiteSpace(artist)) artist = providerSnapshot?.Track?.Artist;
        if (string.IsNullOrWhiteSpace(artist)) artist = string.IsNullOrWhiteSpace(station.Source) ? station.ProviderId : station.Source;
        DockTrackText.Text = DrawerTrackText.Text = title;
        DockArtistText.Text = DrawerArtistText.Text = artist;
        DrawerStationText.Text = $"CH-{station.Order + 1:00} // {station.Name.ToUpperInvariant()}";
        DrawerStationIcon.Data = IconCatalog.Get(station.IconId).Shape;
        try { DrawerStationIcon.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(station.AccentColor)!; }
        catch { DrawerStationIcon.Fill = (System.Windows.Media.Brush)FindResource("OliveBrush"); }
        DrawerStateText.Text = NowPlayingState(station, providerSnapshot);
        DrawerRouteText.Text = _outputTelemetry.Confidence == OutputTelemetryConfidence.Unavailable ? "ROUTE UNAVAILABLE" :
            _outputTelemetry.Confidence == OutputTelemetryConfidence.Conflicting ? "ROUTE NEEDS CHECK" : "ROUTE MONITORED";
        DrawerUpNextText.Text = NextSongText(station, index);
        var absolutePosition = station.ProviderId == "mpv" ? _mpvProvider?.Snapshot.PositionSeconds ?? station.Runtime.PositionSeconds : station.Runtime.PositionSeconds;
        var sourceDuration = station.ProviderId == "mpv" ? _mpvProvider?.Snapshot.DurationSeconds ?? station.Runtime.DurationSeconds : station.Runtime.DurationSeconds;
        SetNowPlayingTimeline(NowPlayingTimeline.FromSource(absolutePosition, sourceDuration, song?.StartSeconds ?? 0, song?.EndSeconds));
        SetNowPlayingTransport(station.Runtime.WasPlaying);
    }

    static bool SameStationChoices(IReadOnlyList<Station> left, IReadOnlyList<Station> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => pair.First.Id == pair.Second.Id &&
            pair.First.Name == pair.Second.Name && pair.First.IconId == pair.Second.IconId &&
            pair.First.AccentColor == pair.Second.AccentColor && pair.First.Order == pair.Second.Order);

    string NowPlayingState(Station station, PlaybackSnapshot? snapshot)
    {
        if (station.Runtime.WasPlaying) return "● ON AIR";
        if (snapshot?.Health is ProviderHealth.Unavailable or ProviderHealth.Failed) return "! SOURCE UNAVAILABLE";
        // YouTube's first ready/progress event is the authoritative handoff from the
        // embedded player. Keep this explicit rather than misrepresenting load as pause.
        if (station.ProviderId == "youtube" && !_youtubePlayerReady) return "◌ ACQUIRING SIGNAL";
        if (snapshot?.Health == ProviderHealth.Unknown) return "◌ ACQUIRING SIGNAL";
        return "Ⅱ PAUSED";
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
        var label = playing ? "Ⅱ  PAUSE" : "▶  PLAY";
        DockPlayButton.Content = playing ? "Ⅱ" : "▶";
        DrawerPlayButton.Content = label;
    }

    void SetNowPlayingTimeline(NowPlayingTimeline timeline)
    {
        _nowPlayingDurationSeconds = timeline.DurationSeconds;
        var elapsed = DisplayTime(timeline.PositionSeconds);
        var duration = timeline.DurationSeconds is { } known ? DisplayTime(known) : "--:--";
        DockElapsedText.Text = DrawerElapsedText.Text = elapsed;
        DockDurationText.Text = DrawerDurationText.Text = duration;
        if (!_nowPlayingTimelineInteraction)
        {
            DockTimeline.Value = timeline.ProgressPercent;
            DrawerTimeline.Value = timeline.ProgressPercent;
        }
        DockTimeline.IsEnabled = DrawerTimeline.IsEnabled = timeline.CanSeek;
    }

    void UpdateNowPlayingVisualizer(SignalLevel music)
    {
        // This method is invoked by the existing 10 Hz Voicemeeter polling loop.
        // Rendering is deliberately decoupled below at 30 FPS, so the mixer is never
        // polled at UI-frame rate.
        _nowPlayingLevelTarget = music.Available ? VoicemeeterSignalMonitor.BarValue(music) / 100d : 0;
    }

    void RenderNowPlayingVisualizer()
    {
        for (var index = 0; index < _nowPlayingBars.Count; index++)
        {
            // A shared source-level signal is rendered as a restrained, deterministic meter pattern.
            var shape = .44 + ((index * 5 + 3) % 7) * .075;
            var desired = 3 + _nowPlayingLevelTarget * 38 * shape;
            var previous = _nowPlayingBars[index].Height;
            _nowPlayingBars[index].Height = _config.ReduceMotion ? desired : desired >= previous
                ? previous + (desired - previous) * .58 // quick attack
                : Math.Max(3, previous - 1.15); // slower signal decay
        }
    }

    async void DockStationSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingNowPlayingStationSelector || !IsLoaded || DockStationSelector.SelectedItem is not Station station || station.Id == _active?.Id) return;
        await ActivateAsync(station);
        RefreshNowPlayingPresentation();
    }

    void NowPlayingExpand_Click(object sender, RoutedEventArgs e) => OpenNowPlayingDrawer();
    void NowPlayingDockTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => OpenNowPlayingDrawer();

    void OpenNowPlayingDrawer()
    {
        NowPlayingDrawer.Visibility = Visibility.Visible;
        NowPlayingDrawer.BeginAnimation(OpacityProperty, null);
        if (_config.ReduceMotion) NowPlayingDrawer.Opacity = 1;
        else NowPlayingDrawer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        RefreshNowPlayingPresentation();
    }

    void NowPlayingCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (_config.ReduceMotion) { NowPlayingDrawer.Visibility = Visibility.Collapsed; return; }
        var close = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(140));
        close.Completed += (_, _) => { NowPlayingDrawer.Visibility = Visibility.Collapsed; NowPlayingDrawer.Opacity = 1; };
        NowPlayingDrawer.BeginAnimation(OpacityProperty, close);
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
