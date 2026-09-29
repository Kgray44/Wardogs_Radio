using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using WardogsRadio.Core;
using WardogsRadio.Playback;

namespace WardogsRadio.App;

public partial class LibrarySourceInspectorWindow : Window
{
    sealed record SourceSongRow(LibrarySong Song, string Timing, string Name, string Detail);
    readonly MusicLibrary _library;
    readonly MediaSource _source;
    readonly Func<Task> _save;
    readonly string? _mpvPath;
    readonly string? _mpvAudioDevice;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(400) };
    MpvProvider? _mpv;
    bool _youtubeReady;
    double _position;
    double _duration;
    double? _previewEndSeconds;
    string _instance = Guid.NewGuid().ToString("N");

    public LibrarySourceInspectorWindow(MusicLibrary library, MediaSource source, Func<Task> save,
        string? mpvPath, string? mpvAudioDevice, ListeningEntityStats? listening = null)
    {
        InitializeComponent();
        _library = library;
        _source = source;
        _save = save;
        _mpvPath = mpvPath;
        _mpvAudioDevice = mpvAudioDevice;
        SourceNameText.Text = source.Name;
        SourceIdentityText.Text = source.Source;
        SourceListeningText.Text = listening is null ? "No listening history yet." :
            $"LISTENING · {listening.AudibleSeconds / 60:0} minutes · {listening.QualifiedPlays} plays · {listening.UniqueSongs} songs heard";
        _duration = source.DurationSeconds ?? 0;
        _poll.Tick += PollAsync;
        RefreshSongs();
    }

    async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (!MusicLibraryService.SupportsCueRanges(_source.ProviderId, _source.Source))
        {
            SourceStateText.Text = "UNSUPPORTED";
            TimelineHintText.Text = "This provider/source has no reliable single-media timeline, so song segmentation is unavailable.";
            PreviewPlayButton.IsEnabled = false;
            PreviewPauseButton.IsEnabled = false;
            CreateSongButton.IsEnabled = false;
            CreateSongMenuItem.IsEnabled = false;
            TimelineSurface.Cursor = System.Windows.Input.Cursors.Arrow;
            return;
        }
        if (_source.ProviderId == "youtube") await InitializeYouTubeAsync();
        else await InitializeLocalAsync();
    }

    async Task InitializeYouTubeAsync()
    {
        if (!MusicLibraryService.TryGetYouTubeVideoId(_source.Source, out var video))
        {
            SourceStateText.Text = "UNSUPPORTED";
            TimelineHintText.Text = "This source cannot provide a reliable single-video timeline.";
            return;
        }
        try
        {
            YouTubePreview.Visibility = Visibility.Visible;
            LocalPreview.Visibility = Visibility.Collapsed;
            await YouTubePreview.EnsureCoreWebView2Async();
            YouTubePreview.CoreWebView2.WebMessageReceived += YouTubeMessage;
            YouTubePreview.CoreWebView2.SetVirtualHostNameToFolderMapping("wardogs-radio.example", AppContext.BaseDirectory, CoreWebView2HostResourceAccessKind.DenyCors);
            YouTubePreview.CoreWebView2.Navigate($"https://wardogs-radio.example/youtube-player.html?v={video}&instance={_instance}&repeat=off");
            SourceStateText.Text = "LOADING";
        }
        catch (Exception error)
        {
            SourceStateText.Text = "UNAVAILABLE";
            TimelineHintText.Text = "YouTube preview unavailable · " + error.Message;
        }
    }

    async void YouTubeMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = message.RootElement;
            if (!root.TryGetProperty("instance", out var token) || token.GetString() != _instance) return;
            var type = root.GetProperty("type").GetString();
            if (type == "ready") { _youtubeReady = true; SourceStateText.Text = "READY"; }
            if (type == "progress" && root.TryGetProperty("detail", out var detail))
            {
                using var progress = JsonDocument.Parse(detail.GetString() ?? "{}");
                _position = progress.RootElement.GetProperty("position").GetDouble();
                _duration = progress.RootElement.GetProperty("duration").GetDouble();
                await PersistDurationAsync();
                RefreshTimeline();
            }
            if (type == "error") { SourceStateText.Text = "CHECK SOURCE"; TimelineHintText.Text = root.GetProperty("detail").GetString() ?? "YouTube player error."; }
        }
        catch { }
    }

    async Task InitializeLocalAsync()
    {
        try
        {
            _mpv = new MpvProvider(new MpvLocator(), _mpvPath, _mpvAudioDevice);
            await _mpv.LoadAsync(new Station { Name = _source.Name, ProviderId = "mpv", Source = _source.Source });
            var snapshot = await _mpv.RefreshAsync();
            _duration = snapshot.DurationSeconds ?? _duration;
            LocalPreviewText.Text = snapshot.Track?.Title ?? _source.Name;
            SourceStateText.Text = "READY";
            await PersistDurationAsync();
            _poll.Start();
            RefreshTimeline();
        }
        catch (Exception error)
        {
            SourceStateText.Text = "UNAVAILABLE";
            LocalPreviewText.Text = "Preview unavailable · " + error.Message;
        }
    }

    async void PollAsync(object? sender, EventArgs e)
    {
        if (_mpv is null) return;
        try
        {
            var snapshot = await _mpv.RefreshAsync();
            _position = snapshot.PositionSeconds;
            _duration = snapshot.DurationSeconds ?? _duration;
            if (_previewEndSeconds is { } end && _position >= end - .05)
            {
                await _mpv.PauseAsync();
                _position = end;
                _previewEndSeconds = null;
            }
            await PersistDurationAsync();
            RefreshTimeline();
        }
        catch { _poll.Stop(); }
    }

    async Task PersistDurationAsync()
    {
        if (_duration <= 0 || !double.IsFinite(_duration) || _source.DurationSeconds is { } known && Math.Abs(known - _duration) < .1) return;
        _source.DurationSeconds = _duration;
        await _save();
        RefreshSongs();
    }

    void RefreshTimeline()
    {
        PreviewTimeText.Text = $"{DisplayTime(_position)} / {(_duration > 0 ? DisplayTime(_duration) : "--:--")}";
        TimelineProgress.Value = _duration > 0 ? Math.Clamp(_position / _duration * 100, 0, 100) : 0;
        TimelineHintText.Text = _duration > 0
            ? "Preview source time · right-click a point to split the containing cue."
            : "Waiting for a reliable source duration before enabling segmentation.";
    }

    void RefreshSongs()
    {
        var songs = _library.Songs.Where(song => song.SourceId == _source.Id).OrderBy(song => song.StartSeconds).ToList();
        SongsSummaryText.Text = $"{songs.Count} {Plural(songs.Count, "cue", "cues")} · all remain timing references into this source";
        SongList.ItemsSource = songs.Select(song => new SourceSongRow(song,
            song.EndSeconds is { } end ? $"{DisplayTime(song.StartSeconds)}–{DisplayTime(end)}" : $"{DisplayTime(song.StartSeconds)}–end",
            song.Name, song.Artist ?? "Shared library cue")).ToList();
    }

    async void PreviewPlay_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _previewEndSeconds = null;
            if (_source.ProviderId == "youtube")
            {
                await YouTubeCommandAsync("setSegments([],0)");
                await YouTubeCommandAsync("play()");
            }
            else if (_mpv is not null) await _mpv.PlayAsync();
        }
        catch (Exception error) { TimelineHintText.Text = "PREVIEW FAILED · " + error.Message; }
    }

    async void PreviewPause_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_source.ProviderId == "youtube") await YouTubeCommandAsync("pause()");
            else if (_mpv is not null) await _mpv.PauseAsync();
        }
        catch (Exception error) { TimelineHintText.Text = "PAUSE FAILED · " + error.Message; }
    }

    async void PreviewSong_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LibrarySong song) return;
        try
        {
            _position = song.StartSeconds;
            _previewEndSeconds = song.EndSeconds ?? _duration;
            if (_source.ProviderId == "youtube")
            {
                if (!MusicLibraryService.TryGetYouTubeVideoId(_source.Source, out var video))
                    throw new InvalidOperationException("This source is not a single YouTube video.");
                var segment = JsonSerializer.Serialize(new[] { new { id = song.Id.ToString("N"), source = video, start = song.StartSeconds, end = song.EndSeconds } });
                await YouTubeCommandAsync($"setSegments({segment},0)");
                await YouTubeCommandAsync("selectSegment(0)");
            }
            else if (_mpv is not null)
            {
                await _mpv.SeekAsync(song.StartSeconds);
                await _mpv.PlayAsync();
            }
            RefreshTimeline();
        }
        catch (Exception error) { TimelineHintText.Text = "CUE PREVIEW FAILED · " + error.Message; }
    }

    async Task YouTubeCommandAsync(string command)
    {
        if (!_youtubeReady) throw new InvalidOperationException("YouTube preview is still loading.");
        await YouTubePreview.CoreWebView2.ExecuteScriptAsync("window.wardogs?." + command + ";");
    }

    async void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        await SeekFromPointAsync(e.GetPosition(TimelineSurface).X);
        e.Handled = true;
    }

    async void Timeline_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        await SeekFromPointAsync(e.GetPosition(TimelineSurface).X);
        e.Handled = true;
    }

    async Task SeekFromPointAsync(double x)
    {
        if (_duration <= 0 || TimelineSurface.ActualWidth <= 0) return;
        _position = Math.Clamp(x / TimelineSurface.ActualWidth, 0, 1) * _duration;
        _previewEndSeconds = null;
        try
        {
            if (_source.ProviderId == "youtube") await YouTubeCommandAsync($"seek({_position.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})");
            else if (_mpv is not null) await _mpv.SeekAsync(_position);
            RefreshTimeline();
        }
        catch (Exception error) { TimelineHintText.Text = "SEEK FAILED · " + error.Message; }
    }

    async void CreateSong_Click(object sender, RoutedEventArgs e)
    {
        if (_duration <= 0) { TimelineHintText.Text = "WAIT FOR A RELIABLE SOURCE DURATION BEFORE CREATING CUES."; return; }
        var current = _library.Songs.Where(song => song.SourceId == _source.Id)
            .FirstOrDefault(song => _position > song.StartSeconds + .25 && _position < (song.EndSeconds ?? _duration) - .25);
        if (current is null) { TimelineHintText.Text = "PLACE THE CURSOR INSIDE AN EXISTING CUE TO SPLIT IT."; return; }
        var dialog = new CreateSongWindow(current.Name, DisplayTime(_position)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            MusicLibraryService.SplitSong(_library, current.Id, _position, _duration, dialog.SongTitle, dialog.NameAfter);
            await _save();
            RefreshSongs();
            TimelineHintText.Text = "CUE CREATED IN LIBRARY · no media was copied.";
        }
        catch (Exception error) { TimelineHintText.Text = "COULD NOT CREATE CUE · " + error.Message; }
    }

    void Refresh_Click(object sender, RoutedEventArgs e) { RefreshSongs(); RefreshTimeline(); }
    async void Window_Closed(object? sender, EventArgs e)
    {
        _poll.Stop();
        if (_mpv is not null) await _mpv.DisposeAsync();
    }
    static string Plural(int value, string singular, string plural) => value == 1 ? singular : plural;
    static string DisplayTime(double seconds) { var time = TimeSpan.FromSeconds(Math.Max(0, seconds)); return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes}:{time.Seconds:00}"; }
}
