using System.Windows;
using System.Windows.Threading;
using WardogsRadio.Core;

namespace WardogsRadio.App;

public partial class MainWindow
{
    ListeningHistoryRecorder _listeningRecorder = new();
    ListeningHistoryStore? _listeningStore;
    readonly DispatcherTimer _listeningTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly SemaphoreSlim _listeningWriteGate = new(1, 1);
    readonly List<ListeningHistoryEntry> _unsavedListeningEntries = [];
    DateTimeOffset _lastListeningCheckpoint;
    bool _listeningTickBusy;
    bool _listeningSelectionInProgress;
    string? _listeningStoreError;

    void InitializeListening(string configurationRoot)
    {
        _listeningStore = new ListeningHistoryStore(configurationRoot);
        _listeningTimer.Tick += ListeningTimer_Tick;
        InitializeListeningPageRefresh();
    }

    async Task StartListeningAsync()
    {
        if (_listeningStore is null) return;
        try
        {
            await _listeningStore.RecoverCheckpointAsync();
            _lastListeningCheckpoint = DateTimeOffset.UtcNow;
            _listeningTimer.Start();
        }
        catch (Exception error)
        {
            _listeningStoreError = error.GetType().Name;
            Footer.Text = "LISTENING HISTORY UNAVAILABLE · Check local storage permissions.";
        }
    }

    async void ListeningTimer_Tick(object? sender, EventArgs e)
    {
        if (_listeningTickBusy || _listeningSelectionInProgress || _listeningStore is null || !_config.ListeningHistoryEnabled) return;
        _listeningTickBusy = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            _listeningRecorder.Observe(CurrentListeningObservation(), now);
            await PersistListeningAsync(now, checkpoint: now - _lastListeningCheckpoint >= TimeSpan.FromSeconds(30));
        }
        catch (Exception error)
        {
            _listeningStoreError = error.GetType().Name;
            Footer.Text = "LISTENING HISTORY COULD NOT SAVE · Check local storage.";
        }
        finally { _listeningTickBusy = false; }
    }

    ListeningObservation? CurrentListeningObservation()
    {
        if (_active is not { } station) return null;
        var resolved = MusicLibraryService.Resolve(_config, station);
        var index = station.ProviderId == "mpv" ? _mpvProvider?.CurrentPlaylistIndex ?? station.Runtime.SequenceIndex
            : station.Runtime.SequenceIndex;
        var song = resolved.ElementAtOrDefault(Math.Clamp(index, 0, Math.Max(0, resolved.Count - 1)));
        var source = song is null ? _config.MusicLibrary.Sources.FirstOrDefault(candidate =>
            candidate.ProviderId == station.ProviderId && MusicLibraryService.NormalizeSource(candidate.ProviderId, candidate.Source) ==
            MusicLibraryService.NormalizeSource(station.ProviderId, station.Source))
            : _config.MusicLibrary.Sources.FirstOrDefault(candidate => candidate.Id == song.SourceId);
        var playing = station.ProviderId switch
        {
            "mpv" => _mpvProvider?.Snapshot.IsPlaying == true,
            "youtube" => station.Runtime.WasPlaying,
            "external-audio" => _externalProvider?.Snapshot.IsPlaying == true,
            _ => station.Runtime.WasPlaying
        };
        var routeReady = station.ProviderId switch
        {
            "mpv" => _mpvProvider?.Snapshot.Health == ProviderHealth.Ready,
            "youtube" => _youtubePlayerReady && _youtubeListeningRoute.IsActive && !_youtubeRouteRecoveryBlocked,
            "external-audio" => _externalProvider?.Snapshot.Health == ProviderHealth.Ready,
            _ => false
        };
        // The selected listening endpoint is authoritative for this user's radio time.
        if (MissingListeningAlert.Visibility == Visibility.Visible) routeReady = false;
        return new ListeningObservation(station.Id, station.Name, song?.SongId, song?.Name,
            source?.Id, source?.Name, playing, true, ListeningPlayerGain(station),
            HasListeningRoute: routeReady);
    }

    async Task PersistListeningAsync(DateTimeOffset now, bool checkpoint = false)
    {
        if (_listeningStore is null) return;
        await _listeningWriteGate.WaitAsync();
        try
        {
            _unsavedListeningEntries.AddRange(_listeningRecorder.Drain());
            await _listeningStore.AppendAsync(_unsavedListeningEntries);
            _unsavedListeningEntries.Clear();
            if (checkpoint)
            {
                await _listeningStore.CheckpointAsync(_listeningRecorder.Checkpoint());
                _lastListeningCheckpoint = now;
            }
            _listeningStoreError = null;
        }
        finally { _listeningWriteGate.Release(); }
    }

    async Task EndListeningTrackAsync(TrackEndReason reason)
    {
        if (!_config.ListeningHistoryEnabled || _listeningStore is null) return;
        var now = DateTimeOffset.UtcNow;
        _listeningRecorder.EndTrack(reason, now);
        try { await PersistListeningAsync(now, checkpoint: true); }
        catch (Exception error) { _listeningStoreError = error.GetType().Name; }
    }

    async Task RefreshListeningNowAsync()
    {
        if (!_config.ListeningHistoryEnabled || _listeningStore is null) return;
        var now = DateTimeOffset.UtcNow;
        _listeningRecorder.Observe(CurrentListeningObservation(), now);
        try { await PersistListeningAsync(now, checkpoint: true); }
        catch (Exception error) { _listeningStoreError = error.GetType().Name; }
    }

    async Task ChangeYouTubeTrackAsync(string command, TrackEndReason reason)
    {
        _listeningSelectionInProgress = true;
        try
        {
            await YouTubeCommandAsync(command);
            await EndListeningTrackAsync(reason);
        }
        finally { _listeningSelectionInProgress = false; }
    }

    async Task ChangeProviderTrackAsync(Func<Task> command, TrackEndReason reason)
    {
        _listeningSelectionInProgress = true;
        try
        {
            await command();
            await EndListeningTrackAsync(reason);
        }
        finally { _listeningSelectionInProgress = false; }
    }

    async Task StopListeningAsync()
    {
        _listeningTimer.Stop();
        _listeningPageTimer.Stop();
        if (_listeningStore is null || !_config.ListeningHistoryEnabled) return;
        var now = DateTimeOffset.UtcNow;
        _listeningRecorder.Close(now);
        await PersistListeningAsync(now, checkpoint: true);
    }

    void ResumeListeningAfterPackage()
    {
        _listeningRecorder = new ListeningHistoryRecorder();
        _lastListeningCheckpoint = DateTimeOffset.UtcNow;
        _listeningTimer.Start();
    }
}
