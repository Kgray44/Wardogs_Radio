using System.Windows;
using System.Windows.Controls;
using WardogsRadio.Core;

namespace WardogsRadio.App;

public partial class MainWindow
{
    sealed record LibrarySourceCard(MediaSource SourceRecord, string Name, string ProviderLabel, string Source,
        string CueCountLabel);
    sealed record LibrarySongCard(LibrarySong SongRecord, string Name, string Detail, string StationCountLabel);

    void RefreshLibrary()
    {
        var library = _config.MusicLibrary;
        library.Sources ??= [];
        library.Songs ??= [];
        LibrarySummaryText.Text = $"{library.Songs.Count} {Plural(library.Songs.Count, "song cue", "song cues")} · " +
            $"{library.Sources.Count} {Plural(library.Sources.Count, "source", "sources")} · no media files are copied";
        LibrarySourceCountText.Text = $"{library.Sources.Count} sources";
        LibrarySourceList.ItemsSource = library.Sources.OrderBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .Select(source => new LibrarySourceCard(source, source.Name,
                source.ProviderId == "youtube" ? "YOUTUBE VIDEO" : "LOCAL / MPV",
                source.Source,
                $"{library.Songs.Count(song => song.SourceId == source.Id)} {Plural(library.Songs.Count(song => song.SourceId == source.Id), "cue", "cues")}"))
            .ToList();
        ShowLibrarySongs(null);
    }

    void ShowLibrarySongs(Guid? sourceId)
    {
        var library = _config.MusicLibrary;
        var usage = _config.Profile.Stations.SelectMany(station => station.PlaylistEntries)
            .GroupBy(entry => entry.SongId).ToDictionary(group => group.Key, group => group.Count());
        var search = LibrarySearchBox?.Text?.Trim() ?? "";
        var unusedOnly = (LibraryFilterBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "unused";
        LibrarySongList.ItemsSource = library.Songs.Where(song => sourceId is null || song.SourceId == sourceId)
            .Where(song => !unusedOnly || usage.GetValueOrDefault(song.Id) == 0)
            .Where(song => string.IsNullOrWhiteSpace(search) || song.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (song.Artist?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false) ||
                library.Sources.FirstOrDefault(source => source.Id == song.SourceId)?.Name.Contains(search, StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(song => song.Name, StringComparer.OrdinalIgnoreCase)
            .Select(song => new LibrarySongCard(song, song.Name,
                SongDetail(song, library.Sources.FirstOrDefault(source => source.Id == song.SourceId)),
                $"Used by {usage.GetValueOrDefault(song.Id)} {Plural(usage.GetValueOrDefault(song.Id), "station", "stations")}"))
            .ToList();
        LibrarySongHintText.Text = library.Sources.Count == 0
            ? "Add a local source or a single YouTube video to create reusable cues."
            : "Editing a cue updates every station that references it.";
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
        }
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

    void LibraryOpenSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MediaSource source) return;
        OpenLibrarySource(source);
    }

    void OpenLibrarySource(MediaSource source)
    {
        var inspector = new LibrarySourceInspectorWindow(_config.MusicLibrary, source,
            () => _store.SaveAsync(_config), _config.MpvPath, _config.MpvAudioDeviceName) { Owner = this };
        inspector.ShowDialog();
        RefreshLibrary();
        RefreshDashboardPlaylist();
    }

    async Task AddLibrarySourceAsync(string providerId)
    {
        var dialog = new LibrarySourceWindow(providerId) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Result is not { } result) return;
        try
        {
            var source = MusicLibraryService.EnsureSource(_config.MusicLibrary, result.ProviderId, result.Source, result.Name);
            MusicLibraryService.EnsureWholeSourceSong(_config.MusicLibrary, source, result.Name);
            await _store.SaveAsync(_config);
            RefreshLibrary();
            Footer.Text = "SOURCE ADDED TO LIBRARY · No media file was copied.";
        }
        catch (Exception error) { Footer.Text = "COULD NOT ADD SOURCE · " + error.Message; }
    }


    async void LibraryEditSong_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LibrarySong song) return;
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
        if ((sender as FrameworkElement)?.Tag is not LibrarySong song) return;
        try
        {
            MusicLibraryService.DuplicateSong(_config.MusicLibrary, song.Id);
            await _store.SaveAsync(_config);
            RefreshLibrary();
            Footer.Text = "ALTERNATE CUE CREATED · Both cues reference the same source.";
        }
        catch (Exception error) { Footer.Text = "COULD NOT DUPLICATE CUE · " + error.Message; }
    }

    async void LibrarySongStations_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not LibrarySong song) return;
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
        if ((sender as FrameworkElement)?.Tag is not LibrarySong song) return;
        var usedBy = _config.Profile.Stations.Count(station => station.PlaylistEntries.Any(entry => entry.SongId == song.Id));
        if (MessageBox.Show(this, $"Delete the library cue '{song.Name}'? It will be removed from {usedBy} {Plural(usedBy, "station", "stations")}, but its source media will remain.",
            "Delete library cue", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            MusicLibraryService.DeleteSong(_config, song.Id);
            if (_active is { } active) MusicLibraryService.MaterializeStationPlaylist(_config, active);
            await _store.SaveAsync(_config);
            RefreshLibrary();
            RefreshDashboardPlaylist();
            Footer.Text = "LIBRARY CUE DELETED · Source media was kept.";
        }
        catch (Exception error) { Footer.Text = "COULD NOT DELETE CUE · " + error.Message; }
    }

    async void LibraryDeleteSource_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not MediaSource source) return;
        var cueCount = _config.MusicLibrary.Songs.Count(song => song.SourceId == source.Id);
        if (MessageBox.Show(this, $"Delete '{source.Name}' and its {cueCount} {Plural(cueCount, "cue", "cues")}? It removes their station references but never deletes the original media file or video.",
            "Delete library source", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
        try
        {
            MusicLibraryService.DeleteSource(_config, source.Id);
            if (_active is { } active) MusicLibraryService.MaterializeStationPlaylist(_config, active);
            await _store.SaveAsync(_config);
            RefreshLibrary();
            RefreshDashboardPlaylist();
            Footer.Text = "SOURCE REMOVED FROM LIBRARY · Original media was kept.";
        }
        catch (Exception error) { Footer.Text = "COULD NOT DELETE SOURCE · " + error.Message; }
    }
}
