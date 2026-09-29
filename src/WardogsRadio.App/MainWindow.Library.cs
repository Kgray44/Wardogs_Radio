using System.Windows;
using System.Windows.Controls;
using WardogsRadio.Core;

namespace WardogsRadio.App;

public partial class MainWindow
{
    sealed record LibrarySourceCard(MediaSource SourceRecord, string Name, string ProviderLabel, string Source,
        string CueCountLabel);
    sealed record LibrarySongCard(LibrarySong SongRecord, string Name, string Detail, string StationCountLabel);
    string _librarySourceSort = "name";
    string _librarySongSort = "name";

    static string LibraryProviderLabel(string providerId) => providerId switch
    {
        "youtube" => "YOUTUBE VIDEO",
        "soundcloud" => "SOUNDCLOUD",
        "applemusic" => "APPLE MUSIC",
        "external-audio" => "MUSIC APP",
        "mpv" => "LOCAL / MPV",
        _ => providerId.ToUpperInvariant()
    };

    void RefreshLibrary()
    {
        var library = _config.MusicLibrary;
        library.Sources ??= [];
        library.Songs ??= [];
        LibrarySummaryText.Text = $"{library.Songs.Count} {Plural(library.Songs.Count, "song cue", "song cues")} · " +
            $"{library.Sources.Count} {Plural(library.Sources.Count, "source", "sources")} · no media files are copied";
        LibrarySourceCountText.Text = $"{library.Sources.Count} sources";
        var sourceCards = library.Sources
            .Select(source => new LibrarySourceCard(source, source.Name,
                LibraryProviderLabel(source.ProviderId),
                source.Source,
                $"{library.Songs.Count(song => song.SourceId == source.Id)} {Plural(library.Songs.Count(song => song.SourceId == source.Id), "cue", "cues")}"));
        LibrarySourceList.ItemsSource = _librarySourceSort switch
        {
            "name-desc" => sourceCards.OrderByDescending(source => source.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "cues" => sourceCards.OrderByDescending(source => library.Songs.Count(song => song.SourceId == source.SourceRecord.Id))
                .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => sourceCards.OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase).ToList()
        };
        ShowLibrarySongs(null);
        RefreshLibraryActions();
    }

    void ShowLibrarySongs(Guid? sourceId)
    {
        var library = _config.MusicLibrary;
        var usage = _config.Profile.Stations.SelectMany(station => station.PlaylistEntries)
            .GroupBy(entry => entry.SongId).ToDictionary(group => group.Key, group => group.Count());
        var search = LibrarySearchBox?.Text?.Trim() ?? "";
        var unusedOnly = (LibraryFilterBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "unused";
        var songs = library.Songs.Where(song => sourceId is null || song.SourceId == sourceId)
            .Where(song => !unusedOnly || usage.GetValueOrDefault(song.Id) == 0)
            .Where(song => string.IsNullOrWhiteSpace(search) || song.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (song.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                library.Sources.FirstOrDefault(source => source.Id == song.SourceId)?.Name.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
            .Select(song => new LibrarySongCard(song, song.Name,
                SongDetail(song, library.Sources.FirstOrDefault(source => source.Id == song.SourceId)),
                $"Used by {usage.GetValueOrDefault(song.Id)} {Plural(usage.GetValueOrDefault(song.Id), "station", "stations")}"));
        LibrarySongList.ItemsSource = _librarySongSort switch
        {
            "name-desc" => songs.OrderByDescending(song => song.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            "stations" => songs.OrderByDescending(song => usage.GetValueOrDefault(song.SongRecord.Id))
                .ThenBy(song => song.Name, StringComparer.OrdinalIgnoreCase).ToList(),
            _ => songs.OrderBy(song => song.Name, StringComparer.OrdinalIgnoreCase).ToList()
        };
        LibrarySongCountText.Text = $"{library.Songs.Count} {Plural(library.Songs.Count, "song", "songs")}";
        LibrarySongHintText.Text = library.Sources.Count == 0
            ? "Add any station source. Local files and single YouTube videos can create reusable timeline cues."
            : "Editing a cue updates every station that references it.";
        RefreshLibraryActions();
    }

    static string Plural(int value, string singular, string plural) => value == 1 ? singular : plural;

    static string SongDetail(LibrarySong song, MediaSource? source)
    {
        var timing = song.EndSeconds is { } end ? $"{DisplayTime(song.StartSeconds)}–{DisplayTime(end)}" :
            song.StartSeconds > 0 ? $"from {DisplayTime(song.StartSeconds)}" : "full source";
        return $"{timing} · {source?.Name ?? "missing source"}";
    }

    void LibrarySourceList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibrarySourceList.SelectedItem is LibrarySourceCard card)
        {
            ShowLibrarySongs(card.SourceRecord.Id);
            LibrarySongHintText.Text = $"{card.Name} · select a cue to edit its shared title and boundaries.";
            ShowLibraryListening(sourceId: card.SourceRecord.Id);
        }
        RefreshLibraryActions();
    }

    void LibrarySongList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RefreshLibraryActions();
        if (LibrarySongList.SelectedItem is LibrarySongCard card) ShowLibraryListening(songId: card.SongRecord.Id);
    }

    void LibrarySort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _librarySourceSort = (LibrarySourceSortBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "name";
        _librarySongSort = (LibrarySongSortBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "name";
        RefreshLibrary();
    }

    IReadOnlyList<LibrarySourceCard> SelectedLibrarySources() => LibrarySourceList?.SelectedItems.OfType<LibrarySourceCard>().ToList() ?? [];
    IReadOnlyList<LibrarySongCard> SelectedLibrarySongs() => LibrarySongList?.SelectedItems.OfType<LibrarySongCard>().ToList() ?? [];

    void RefreshLibraryActions()
    {
        if (LibrarySourceSelectionText is null || LibrarySongSelectionText is null) return;
        var sources = SelectedLibrarySources();
        var songs = SelectedLibrarySongs();
        LibrarySourceSelectionText.Text = $"{sources.Count} selected";
        LibrarySongSelectionText.Text = $"{songs.Count} selected";
        LibraryOpenSelectedSourceButton.IsEnabled = sources.Count > 0;
        LibraryDeleteSelectedSourcesButton.IsEnabled = sources.Count > 0;
        LibrarySongStationsButton.IsEnabled = songs.Count > 0;
        LibraryDuplicateSelectedSongsButton.IsEnabled = songs.Count > 0;
        LibraryEditSelectedSongButton.IsEnabled = songs.Count > 0;
        LibraryDeleteSelectedSongsButton.IsEnabled = songs.Count > 0;
    }

    void LibraryFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || LibrarySongList is null) return;
        ShowLibrarySongs((LibrarySourceList?.SelectedItem as LibrarySourceCard)?.SourceRecord.Id);
    }

    async void LibraryAddSource_Click(object sender, RoutedEventArgs e)
    {
        var type = new LibrarySourceTypeWindow { Owner = this };
        if (type.ShowDialog() != true || type.ProviderId is not { } providerId) return;
        await AddLibrarySourceAsync(providerId);
    }

    void LibraryOpenSelectedSource_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLibrarySources().FirstOrDefault() is not { } source) return;
        OpenLibrarySource(source.SourceRecord);
    }

    void OpenLibrarySource(MediaSource source)
    {
        string listeningDevice;
        try { listeningDevice = RequireListeningPlayerDevice(); }
        catch (InvalidOperationException error) { Footer.Text = error.Message; return; }
        var inspector = new LibrarySourceInspectorWindow(_config.MusicLibrary, source,
            () => _store.SaveAsync(_config), _config.MpvPath, listeningDevice,
            _libraryAllTimeStats?.Sources.FirstOrDefault(item => item.Id == source.Id)) { Owner = this };
        inspector.ShowDialog();
        RefreshLibrary();
        RefreshDashboardPlaylist();
    }

    async Task AddLibrarySourceAsync(string providerId)
    {
        var dialog = new LibrarySourceWindow(providerId, _active?.Runtime.WasPlaying != true, _config.MusicLibrary) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result) return;
        try
        {
            var source = dialog.SearchResult is { } discovered
                ? MediaDiscoveryIngestion.Add(_config.MusicLibrary, discovered).Source
                : MusicLibraryService.EnsureSource(_config.MusicLibrary, result.ProviderId, result.Source, result.Name);
            if (dialog.SearchResult is null && MusicLibraryService.SupportsCueRanges(result.ProviderId, result.Source))
                MusicLibraryService.EnsureWholeSourceSong(_config.MusicLibrary, source, result.Name);
            await _store.SaveAsync(_config);
            RefreshLibrary();
            Footer.Text = "SOURCE READY IN LIBRARY · No media file was copied.";
        }
        catch (Exception error) { Footer.Text = "COULD NOT ADD SOURCE · " + error.Message; }
    }

    void ApplyDiscoveredStationSource(Station station, MediaSearchResult? result, bool sourceChanged)
    {
        if (result is null || !sourceChanged) return;
        station.PlaylistEntries.Clear();
        station.PlaylistSongs.Clear();
        MediaDiscoveryIngestion.Add(_config.MusicLibrary, result,
            result.Type == MediaSearchResultType.Video ? station : null);
        MusicLibraryService.MaterializeStationPlaylist(_config, station);
    }


    async void LibraryEditSong_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLibrarySongs().FirstOrDefault() is not { } selected) return;
        var song = selected.SongRecord;
        var source = _config.MusicLibrary.Sources.FirstOrDefault(candidate => candidate.Id == song.SourceId);
        if (source is null) return;
        var dialog = new EditSongWindow(new StationSong
        {
            Id = song.Id, Source = source.Source, Name = song.Name, StartSeconds = song.StartSeconds, EndSeconds = song.EndSeconds
        }, source.DurationSeconds) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            MusicLibraryService.UpdateSong(_config.MusicLibrary, song.Id, dialog.SongTitle,
                dialog.StartSeconds, dialog.EndSeconds, source.DurationSeconds);
            if (_active is { } active && active.PlaylistEntries.Any(entry => entry.SongId == song.Id))
                MusicLibraryService.MaterializeStationPlaylist(_config, active);
            await _store.SaveAsync(_config);
            RefreshLibrary();
            RefreshDashboardPlaylist();
            Footer.Text = "CUE SAVED EVERYWHERE · Original media was not changed.";
        }
        catch (Exception error) { Footer.Text = "COULD NOT EDIT CUE · " + error.Message; }
    }

    async void LibraryDuplicateSong_Click(object sender, RoutedEventArgs e)
    {
        var songs = SelectedLibrarySongs().Select(card => card.SongRecord).ToList();
        if (songs.Count == 0) return;
        try
        {
            foreach (var song in songs) MusicLibraryService.DuplicateSong(_config.MusicLibrary, song.Id);
            await _store.SaveAsync(_config);
            RefreshLibrary();
            Footer.Text = $"{songs.Count} {Plural(songs.Count, "ALTERNATE CUE CREATED", "ALTERNATE CUES CREATED")} · Each references the same source.";
        }
        catch (Exception error) { Footer.Text = "COULD NOT DUPLICATE CUE · " + error.Message; }
    }

    async void LibrarySongStations_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedLibrarySongs().FirstOrDefault() is not { } selected) return;
        var song = selected.SongRecord;
        var source = _config.MusicLibrary.Sources.FirstOrDefault(candidate => candidate.Id == song.SourceId);
        if (source is null) return;
        var dialog = new LibrarySongMembershipWindow(song, source, _config.Profile.Stations) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            foreach (var choice in dialog.Choices)
            {
                var assigned = choice.Station.PlaylistEntries.Any(entry => entry.SongId == song.Id);
                if (choice.IsAssigned && !assigned) MusicLibraryService.AddSongToStation(choice.Station, song.Id);
                if (!choice.IsAssigned && assigned) MusicLibraryService.RemoveSongFromStation(choice.Station, song.Id);
                MusicLibraryService.MaterializeStationPlaylist(_config, choice.Station);
            }
            await _store.SaveAsync(_config);
            RefreshLibrary();
            RefreshDashboardPlaylist();
            Footer.Text = "STATION ASSIGNMENTS SAVED · The library cue remains shared.";
        }
        catch (Exception error) { Footer.Text = "COULD NOT UPDATE STATIONS · " + error.Message; }
    }

    async void LibraryDeleteSong_Click(object sender, RoutedEventArgs e)
    {
        var songs = SelectedLibrarySongs().Select(card => card.SongRecord).ToList();
        if (songs.Count == 0) return;
        var usedBy = _config.Profile.Stations.Count(station => station.PlaylistEntries.Any(entry => songs.Any(song => song.Id == entry.SongId)));
        if (MessageBox.Show(this, $"Delete {songs.Count} selected {Plural(songs.Count, "library cue", "library cues")}? They will be removed from {usedBy} {Plural(usedBy, "station", "stations")}, but their source media will remain.",
            "Delete library cues", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            foreach (var song in songs) MusicLibraryService.DeleteSong(_config, song.Id);
            if (_active is { } active) MusicLibraryService.MaterializeStationPlaylist(_config, active);
            await _store.SaveAsync(_config);
            RefreshLibrary();
            RefreshDashboardPlaylist();
            Footer.Text = $"{songs.Count} {Plural(songs.Count, "LIBRARY CUE DELETED", "LIBRARY CUES DELETED")} · Source media was kept.";
        }
        catch (Exception error) { Footer.Text = "COULD NOT DELETE CUE · " + error.Message; }
    }

    async void LibraryDeleteSelectedSources_Click(object sender, RoutedEventArgs e)
    {
        var sources = SelectedLibrarySources().Select(card => card.SourceRecord).ToList();
        if (sources.Count == 0) return;
        var sourceIds = sources.Select(source => source.Id).ToHashSet();
        var cueCount = _config.MusicLibrary.Songs.Count(song => sourceIds.Contains(song.SourceId));
        if (MessageBox.Show(this, $"Delete {sources.Count} selected {Plural(sources.Count, "source", "sources")} and their {cueCount} {Plural(cueCount, "cue", "cues")}? This removes their station references but never deletes original media files or videos.",
            "Delete library sources", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            foreach (var source in sources) MusicLibraryService.DeleteSource(_config, source.Id);
            if (_active is { } active) MusicLibraryService.MaterializeStationPlaylist(_config, active);
            await _store.SaveAsync(_config);
            RefreshLibrary();
            RefreshDashboardPlaylist();
            Footer.Text = $"{sources.Count} {Plural(sources.Count, "SOURCE REMOVED", "SOURCES REMOVED")} · Original media was kept.";
        }
        catch (Exception error) { Footer.Text = "COULD NOT DELETE SOURCE · " + error.Message; }
    }
}
