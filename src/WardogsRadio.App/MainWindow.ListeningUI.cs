using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WardogsRadio.Core;

namespace WardogsRadio.App;

public partial class MainWindow
{
    sealed record ListeningEntityCard(ListeningEntityStats Stats, string Label, double Share = 0);
    sealed record RecentCard(RecentListening Recent, string Label);
    ListeningStats? _visibleListeningStats;
    ListeningStats? _libraryAllTimeStats;
    bool _updatingListeningControls;
    readonly DispatcherTimer _listeningPageTimer = new() { Interval = VisiblePageRefresh.Interval };
    readonly VisiblePageRefresh _listeningPageRefresh = new();

    void InitializeListeningPageRefresh()
    {
        _listeningPageTimer.Tick += async (_, _) => await RefreshListeningPageAsync(explicitRequest: false);
    }

    async void ListeningView_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsLoaded || _closingInProgress || ListeningView is null) return;
        if (ListeningView.IsVisible)
        {
            _listeningPageTimer.Start();
            await RefreshListeningPageAsync();
        }
        else _listeningPageTimer.Stop();
    }

    void Listening_Click(object sender, RoutedEventArgs e)
    {
        Show(ListeningView, "LISTENING", "Your radio, over time", ListeningNav);
    }

    async Task RefreshDashboardListeningAsync()
    {
        if (_listeningStore is null || DashboardListeningText is null) return;
        try
        {
            var today = DateTime.Today;
            var weekStart = today.AddDays(-((int)today.DayOfWeek + 6) % 7);
            var records = await _listeningStore.ReadAsync();
            var weekly = ListeningStatsService.Aggregate(records, new DateTimeOffset(weekStart));
            var top = weekly.Songs.OrderByDescending(song => song.QualifiedPlays).FirstOrDefault();
            DashboardListeningText.Text = weekly.TotalAudibleSeconds <= 0 ? "No listening history this week." :
                $"This week  {FormatListeningTime(weekly.TotalAudibleSeconds)}\n" +
                $"Favorite  {weekly.Stations.FirstOrDefault()?.Name ?? "—"}\n" +
                $"Top track  {top?.Name ?? "—"}";
        }
        catch (Exception error)
        {
            _listeningStoreError = error.GetType().Name;
            DashboardListeningText.Text = "Listening summary unavailable.";
        }
    }

    async Task RefreshLibraryListeningAsync()
    {
        if (_listeningStore is null) return;
        try
        {
            _libraryAllTimeStats = ListeningStatsService.Aggregate(await _listeningStore.ReadAsync());
            if (LibrarySongList.SelectedItem is LibrarySongCard song) ShowLibraryListening(songId: song.SongRecord.Id);
            else if (LibrarySourceList.SelectedItem is LibrarySourceCard source) ShowLibraryListening(sourceId: source.SourceRecord.Id);
            else LibraryListeningText.Text = "Select a source or song to see its listening history.";
        }
        catch (Exception error)
        {
            _listeningStoreError = error.GetType().Name;
            LibraryListeningText.Text = "Listening summary unavailable.";
        }
    }

    void ShowLibraryListening(Guid? sourceId = null, Guid? songId = null)
    {
        var item = songId is { } selectedSong ? _libraryAllTimeStats?.Songs.FirstOrDefault(entry => entry.Id == selectedSong)
            : sourceId is { } selectedSource ? _libraryAllTimeStats?.Sources.FirstOrDefault(entry => entry.Id == selectedSource)
            : null;
        LibraryListeningText.Text = item is null ? "No listening history for this item yet." :
            $"LISTENING · {FormatListeningTime(item.AudibleSeconds)} heard · {item.QualifiedPlays} plays · {item.Completed} completed · {item.Skipped} skipped";
    }

    async Task RefreshStationListeningAsync(Guid? stationId = null)
    {
        if (_listeningStore is null || StationListeningText is null) return;
        try
        {
            var stats = ListeningStatsService.Aggregate(await _listeningStore.ReadAsync());
            var station = stats.Stations.FirstOrDefault(item => item.Id == (stationId ?? (StationList.SelectedItem as Station)?.Id));
            StationListeningText.Text = station is null ? "No listening history for this station yet." :
                $"LISTENING · {FormatListeningTime(station.AudibleSeconds)} · {station.QualifiedPlays} plays · {station.UniqueSongs} songs";
        }
        catch (Exception error)
        {
            _listeningStoreError = error.GetType().Name;
            StationListeningText.Text = "Listening summary unavailable.";
        }
    }

    async void ListeningRange_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && ListeningView?.Visibility == Visibility.Visible) await RefreshListeningPageAsync();
    }

    Task RefreshListeningPageAsync(bool explicitRequest = true) => _listeningPageRefresh.RunAsync(
        () => ListeningView.IsVisible && !_closingInProgress, RefreshListeningPageCoreAsync, explicitRequest);

    async Task RefreshListeningPageCoreAsync()
    {
        if (_listeningStore is null) return;
        try
        {
            await PersistListeningAsync(DateTimeOffset.UtcNow, checkpoint: true);
            var records = await _listeningStore.ReadAsync();
            var from = ListeningRangeBox.SelectedItem is ComboBoxItem choice ? choice.Tag?.ToString() switch
            {
                "7" => DateTimeOffset.Now.AddDays(-7),
                "30" => DateTimeOffset.Now.AddDays(-30),
                "year" => new DateTimeOffset(DateTime.Now.Year, 1, 1, 0, 0, 0, DateTimeOffset.Now.Offset),
                _ => (DateTimeOffset?)null
            } : null;
            var stats = await Task.Run(() => ListeningStatsService.Aggregate(records, from));
            if (!ListeningView.IsVisible || _closingInProgress) return;
            _visibleListeningStats = stats;
            ListeningTotalText.Text = FormatListeningTime(stats.TotalAudibleSeconds);
            ListeningPlaysText.Text = stats.QualifiedPlays.ToString("N0");
            ListeningSongsText.Text = stats.UniqueSongs.ToString("N0");
            ListeningStationsText.Text = stats.UniqueStations.ToString("N0");
            var favorite = stats.Stations.FirstOrDefault();
            var mostPlayed = stats.Songs.OrderByDescending(song => song.QualifiedPlays).ThenBy(song => song.Name).FirstOrDefault();
            var mostHeard = stats.Songs.FirstOrDefault();
            ListeningHighlightsText.Text = favorite is null ? "No listening history yet. Play a station to begin." :
                $"FAVORITE STATION  {favorite.Name} · {FormatListeningTime(favorite.AudibleSeconds)}\n" +
                $"MOST PLAYED  {mostPlayed?.Name ?? "—"} · {mostPlayed?.QualifiedPlays ?? 0} plays\n" +
                $"MOST LISTENED  {mostHeard?.Name ?? "—"} · {FormatListeningTime(mostHeard?.AudibleSeconds ?? 0)}";
            ListeningSessionsText.Text = stats.Sessions == 0 ? "" :
                $"{stats.Sessions} listening sessions · {FormatListeningTime(stats.AverageSessionSeconds)} average · {FormatListeningTime(stats.LongestSessionSeconds)} longest";
            ListeningStationList.ItemsSource = stats.Stations.Take(30).Select(item => new ListeningEntityCard(item,
                $"{item.Name} · {FormatListeningTime(item.AudibleSeconds)} · {item.QualifiedPlays} plays",
                stats.TotalAudibleSeconds <= 0 ? 0 : 100 * item.AudibleSeconds / stats.TotalAudibleSeconds)).ToList();
            ListeningSongList.ItemsSource = stats.Songs.OrderByDescending(item => item.QualifiedPlays).Take(40)
                .Select(item => new ListeningEntityCard(item,
                    $"{item.Name} · {item.QualifiedPlays} plays · {FormatListeningTime(item.AudibleSeconds)}")).ToList();
            ListeningSourceList.ItemsSource = stats.Sources.Take(30).Select(item => new ListeningEntityCard(item,
                $"{item.Name} · {FormatListeningTime(item.AudibleSeconds)} · {item.QualifiedPlays} plays")).ToList();
            ListeningRecentList.ItemsSource = stats.Recent.Take(30).Select(item => new RecentCard(item,
                $"{item.PlayedAt.ToLocalTime():MMM d, h:mm tt} · {item.SongName} · {item.StationName}")).ToList();
            _updatingListeningControls = true;
            ListeningEnabledCheck.IsChecked = _config.ListeningHistoryEnabled;
            _updatingListeningControls = false;
            ListeningDetailText.Text = "Select a station, song, source, or recent play for details.";
        }
        catch (Exception error)
        {
            _listeningStoreError = error.GetType().Name;
            ListeningDetailText.Text = "Listening history could not be opened. Check local storage permissions.";
        }
    }

    void ListeningStation_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListeningStationList.SelectedItem is not ListeningEntityCard { Stats: var item }) return;
        ListeningDetailText.Text = $"{item.Name.ToUpperInvariant()}\n{FormatListeningTime(item.AudibleSeconds)} audible · " +
            $"{item.QualifiedPlays} qualified plays · {item.UniqueSongs} unique songs · {item.Sessions} sessions\n" +
            $"Last heard: {FormatLast(item.LastPlayed)}";
    }

    void ListeningSong_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListeningSongList.SelectedItem is not ListeningEntityCard { Stats: var item }) return;
        var station = _visibleListeningStats?.Recent.Where(recent => recent.SongId == item.Id)
            .GroupBy(recent => recent.StationName).OrderByDescending(group => group.Count()).FirstOrDefault()?.Key;
        ListeningDetailText.Text = $"{item.Name.ToUpperInvariant()}\n{item.QualifiedPlays} qualified plays · " +
            $"{FormatListeningTime(item.AudibleSeconds)} heard · {item.Completed} completed · {item.Skipped} skipped\n" +
            $"First heard: {FormatLast(item.FirstPlayed)} · Last heard: {FormatLast(item.LastPlayed)}" +
            (station is null ? "" : $"\nRecently heard on: {station}");
    }

    void ListeningSource_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListeningSourceList.SelectedItem is not ListeningEntityCard { Stats: var item }) return;
        ListeningDetailText.Text = $"{item.Name.ToUpperInvariant()}\n{FormatListeningTime(item.AudibleSeconds)} heard · " +
            $"{item.QualifiedPlays} qualified plays · {item.UniqueSongs} songs heard\nLast heard: {FormatLast(item.LastPlayed)}";
    }

    void ListeningRecent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ListeningRecentList.SelectedItem is not RecentCard { Recent: var item }) return;
        ListeningDetailText.Text = $"{item.SongName}\n{item.StationName} · {item.PlayedAt.ToLocalTime():dddd, MMMM d, yyyy h:mm tt}";
    }

    async void ListeningEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingListeningControls || !IsLoaded || ListeningEnabledCheck is null) return;
        var enabled = ListeningEnabledCheck.IsChecked == true;
        if (enabled == _config.ListeningHistoryEnabled) return;
        try
        {
            if (!enabled)
            {
                var now = DateTimeOffset.UtcNow;
                _listeningRecorder.Close(now);
                await PersistListeningAsync(now, checkpoint: true);
            }
            _config.ListeningHistoryEnabled = enabled;
            await _store.SaveAsync(_config);
            Footer.Text = enabled ? "LOCAL LISTENING HISTORY ENABLED." : "LOCAL LISTENING HISTORY PAUSED.";
        }
        catch (Exception error)
        {
            _listeningStoreError = error.GetType().Name;
            _config.ListeningHistoryEnabled = !enabled;
            _updatingListeningControls = true;
            ListeningEnabledCheck.IsChecked = !enabled;
            _updatingListeningControls = false;
            Footer.Text = "COULD NOT CHANGE LISTENING HISTORY SETTING · Check local storage.";
        }
    }

    async void ListeningClear_Click(object sender, RoutedEventArgs e)
    {
        if (_listeningStore is null || !RadioDialogWindow.Confirm(this, "Clear Listening History?",
            "This permanently removes play history, listening times, and station/song/source statistics. Your Library, stations, and settings are unchanged.",
            "CLEAR HISTORY")) return;
        _listeningTimer.Stop();
        await _listeningWriteGate.WaitAsync();
        var cleared = false;
        try
        {
            await _listeningStore.ClearAsync();
            _listeningRecorder = new ListeningHistoryRecorder();
            _unsavedListeningEntries.Clear();
            _lastListeningCheckpoint = DateTimeOffset.UtcNow;
            cleared = true;
        }
        catch (Exception error)
        {
            _listeningStoreError = error.GetType().Name;
            Footer.Text = "COULD NOT CLEAR LISTENING HISTORY · Check local storage.";
        }
        finally { _listeningWriteGate.Release(); _listeningTimer.Start(); }
        if (!cleared) return;
        await RefreshListeningPageAsync();
        Footer.Text = "LISTENING HISTORY CLEARED · Your Library and stations were kept.";
    }

    static string FormatListeningTime(double seconds) => seconds >= 3600
        ? $"{Math.Floor(seconds / 3600):N0}h {Math.Floor(seconds % 3600 / 60):00}m"
        : seconds >= 60 ? $"{Math.Floor(seconds / 60):N0}m {Math.Floor(seconds % 60):00}s"
        : $"{Math.Floor(seconds):N0}s";

    static string FormatLast(DateTimeOffset? time) => time is null ? "Never" : time.Value.ToLocalTime().ToString("MMM d, yyyy h:mm tt");
}
