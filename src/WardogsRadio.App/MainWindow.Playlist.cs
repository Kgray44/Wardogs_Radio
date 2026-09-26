using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WardogsRadio.Core;
using WardogsRadio.Playback;

namespace WardogsRadio.App;

public partial class MainWindow
{
    sealed record SongCard(Guid EntryId, StationSong Song, string Number, string Name, string Detail);
    Point _playlistDragStart;
    StationSong? _playlistDragSong;
    StationSong? _playlistContextSong;
    string? _lastPlaylistDisplay;
    bool _timelineDragging;
    double _timelineContextSeconds;
    bool _localPlaylistEnded;

    void RefreshDashboardPlaylist()
    {
        if (DashboardPlaylist is null) return;
        var station = _active;
        if (station is not null) MusicLibraryService.MaterializeStationPlaylist(_config, station);
        var songs = station?.PlaylistSongs ?? [];
        var current = station?.ProviderId == "mpv" ? _mpvProvider?.CurrentPlaylistIndex ?? station?.Runtime.SequenceIndex ?? 0
            : station?.Runtime.SequenceIndex ?? 0;
        DashboardShuffleButton.IsEnabled = station?.ProviderId is "mpv" or "youtube" && songs.Count > 1;
        DashboardPlaylistHint.Text = station is null ? "Choose a station to see its songs."
            : songs.Count == 0 && !MusicLibraryService.SupportsCueRanges(station.ProviderId, station.Source)
                ? "This source does not support reliable song segmentation."
            : songs.Count == 0 && station.ProviderId == "youtube" ? "Play the video, then mark song boundaries on the timeline."
            : songs.Count == 0 ? "This station has no local song list."
            : "Double-click to play · drag to reorder · shuffle mixes this list now.";
        var signature = $"{station?.Id}:{current}:{string.Join(',', songs.Select(x => $"{x.Id:N}:{x.Name}:{x.StartSeconds}:{x.EndSeconds}:{(station is null ? null : KnownSongDuration(station, x))}"))}";
        if (signature == _lastPlaylistDisplay) return;
        _lastPlaylistDisplay = signature;
        var entryIds = station?.PlaylistEntries.Select(entry => entry.Id).ToList() ?? [];
        DashboardPlaylist.ItemsSource = songs.Select((song, index) => new SongCard(entryIds.ElementAtOrDefault(index), song,
            index == current ? "▶" : $"{index + 1:00}", song.Name,
            SongCardDetail(station, song))).ToList();
        DashboardPlaylist.SelectedIndex = songs.Count > 0 ? Math.Clamp(current, 0, songs.Count - 1) : -1;
    }

    string SongCardDetail(Station? station, StationSong song)
    {
        var sourceName = Path.GetFileName(song.Source);
        if (string.IsNullOrWhiteSpace(sourceName)) sourceName = song.Source;
        var end = song.EndSeconds ?? (station is null ? null : KnownSongDuration(station, song));
        return end is { } knownEnd
            ? $"{DisplayTime(song.StartSeconds)}–{DisplayTime(knownEnd)} · {sourceName}"
            : song.StartSeconds > 0 ? $"from {DisplayTime(song.StartSeconds)} · {sourceName}"
            : sourceName;
    }

    async Task<PlaybackSnapshot> EnforceLocalSongBoundaryAsync(Station station, MpvProvider provider, PlaybackSnapshot playback)
    {
        var songs = station.PlaylistSongs;
        if (songs.Count != provider.LoadedFiles.Count || songs.Count == 0) return playback;
        if (SongPlaylist.HasBoundaries(station) && !playback.IsPlaying && station.Runtime.WasPlaying &&
            station.Runtime.SequenceIndex == songs.Count - 1 &&
            playback.DurationSeconds is > 0 && station.Runtime.PositionSeconds > playback.DurationSeconds.Value - 2)
        {
            if (station.EffectiveRepeatMode == StationRepeatMode.Off)
            {
                _localPlaylistEnded = true;
                return playback;
            }
            var target = station.EffectiveRepeatMode == StationRepeatMode.Track ? songs.Count - 1 : 0;
            await provider.SelectTrackAsync(target, songs[target].StartSeconds);
            await provider.PlayAsync();
            return await provider.RefreshAsync();
        }
        var index = Math.Clamp(provider.CurrentPlaylistIndex, 0, songs.Count - 1);
        var song = songs[index];
        if (index != station.Runtime.SequenceIndex && playback.PositionSeconds + .15 < song.StartSeconds)
        {
            await provider.SeekAsync(song.StartSeconds);
            return await provider.RefreshAsync();
        }
        if (song.EndSeconds is not { } end || !playback.IsPlaying || playback.PositionSeconds < end - .07)
            return playback;
        var next = station.EffectiveRepeatMode == StationRepeatMode.Track ? index : index + 1;
        if (next >= songs.Count)
        {
            if (station.EffectiveRepeatMode == StationRepeatMode.Off)
            {
                await provider.PauseAsync();
                await provider.SeekAsync(Math.Max(song.StartSeconds, end - .03));
                _localPlaylistEnded = true;
                return await provider.RefreshAsync();
            }
            next = 0;
        }
        _localPlaylistEnded = false;
        await provider.SelectTrackAsync(next, songs[next].StartSeconds);
        await provider.PlayAsync();
        return await provider.RefreshAsync();
    }

    async Task SelectLocalSongAsync(int index, double? absoluteSeconds = null, bool playIfPaused = false)
    {
        if (_active is not { } station || _mpvProvider is not { } provider || index < 0 || index >= station.PlaylistSongs.Count) return;
        var song = station.PlaylistSongs[index];
        var target = Math.Max(song.StartSeconds, absoluteSeconds ?? song.StartSeconds);
        if (song.EndSeconds is { } end) target = Math.Min(target, end - .03);
        await provider.SelectTrackAsync(index, target);
        station.Runtime.SequenceIndex = index;
        station.Runtime.PositionSeconds = target;
        _localPlaylistEnded = false;
        await SyncGameToHeadsetAsync(provider);
        RefreshDashboardPlaylist();
        if (playIfPaused && !station.Runtime.WasPlaying) await ToggleActiveAsync();
    }

    async Task NextSongAsync()
    {
        if (_active is not { } station || _mpvProvider is not { } provider) return;
        var next = provider.CurrentPlaylistIndex + 1;
        if (next >= station.PlaylistSongs.Count)
            next = station.EffectiveRepeatMode == StationRepeatMode.Off ? station.PlaylistSongs.Count - 1 : 0;
        await SelectLocalSongAsync(next);
    }

    async Task PreviousSongAsync()
    {
        if (_active is not { } station || _mpvProvider is not { } provider) return;
        var index = Math.Clamp(provider.CurrentPlaylistIndex, 0, station.PlaylistSongs.Count - 1);
        var current = station.PlaylistSongs[index];
        var target = provider.Snapshot.PositionSeconds > current.StartSeconds + 5 ? index : Math.Max(0, index - 1);
        await SelectLocalSongAsync(target);
    }

    static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    void DashboardPlaylist_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _playlistDragStart = e.GetPosition(DashboardPlaylist);
        _playlistDragSong = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is SongCard card ? card.Song : null;
    }

    void DashboardPlaylist_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        _playlistContextSong = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is SongCard card ? card.Song : null;
    }

    void DashboardPlaylist_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_active is not { ProviderId: "mpv" or "youtube" })
        {
            e.Handled = true;
            return;
        }
        if (Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is SongCard card)
            _playlistContextSong = card.Song;
        var onSong = _playlistContextSong is not null && _active.PlaylistSongs.Any(x => x.Id == _playlistContextSong.Id);
        var source = _playlistContextSong?.Source
            ?? _active.PlaylistSongs.ElementAtOrDefault(Math.Clamp(_active.Runtime.SequenceIndex, 0, Math.Max(0, _active.PlaylistSongs.Count - 1)))?.Source
            ?? _active.Source;
        if (!MusicLibraryService.SupportsCueRanges(_active.ProviderId, source) || !onSong && _active.Runtime.DurationSeconds <= 0)
        {
            e.Handled = true;
            return;
        }
        PlaylistCreateSongMenuItem.Visibility = onSong ? Visibility.Collapsed : Visibility.Visible;
        PlaylistEditSongMenuItem.Visibility = onSong ? Visibility.Visible : Visibility.Collapsed;
        PlaylistSongActionsSeparator.Visibility = onSong ? Visibility.Visible : Visibility.Collapsed;
        PlaylistOpenLibraryMenuItem.Visibility = onSong ? Visibility.Visible : Visibility.Collapsed;
        PlaylistOpenSourceMenuItem.Visibility = onSong ? Visibility.Visible : Visibility.Collapsed;
        PlaylistRemoveFromStationMenuItem.Visibility = onSong ? Visibility.Visible : Visibility.Collapsed;
        if (!onSong)
            _timelineContextSeconds = _active.ProviderId == "mpv"
                ? _mpvProvider?.Snapshot.PositionSeconds ?? _active.Runtime.PositionSeconds
                : _active.Runtime.PositionSeconds;
    }

    double? KnownSongDuration(Station station, StationSong song)
    {
        var source = _config.MusicLibrary.Sources.FirstOrDefault(candidate =>
            candidate.ProviderId.Equals(station.ProviderId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(candidate.Source, song.Source, StringComparison.OrdinalIgnoreCase));
        if (source?.DurationSeconds is > 0) return source.DurationSeconds;
        if (station.ProviderId == "youtube") return station.Runtime.DurationSeconds > 0 ? station.Runtime.DurationSeconds : null;
        if (station.ProviderId != "mpv") return null;
        var key = DurationKey(song.Source);
        if (key is not null && _config.LocalDurationCache.TryGetValue(key, out var cached) && cached > 0) return cached;
        var current = _mpvProvider?.CurrentPlaylistIndex ?? -1;
        return current >= 0 && current < station.PlaylistSongs.Count &&
            string.Equals(station.PlaylistSongs[current].Source, song.Source, StringComparison.OrdinalIgnoreCase) &&
            station.Runtime.DurationSeconds > 0 ? station.Runtime.DurationSeconds : null;
    }

    async void DashboardPlaylist_EditSong_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not { } station || _playlistContextSong is not { } target) return;
        var song = station.PlaylistSongs.FirstOrDefault(item => item.Id == target.Id);
        if (song is null) return;
        var dialog = new EditSongWindow(song, KnownSongDuration(station, song)) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            MusicLibraryService.UpdateSong(_config.MusicLibrary, song.Id, dialog.SongTitle,
                dialog.StartSeconds, dialog.EndSeconds, KnownSongDuration(station, song));
            MusicLibraryService.MaterializeStationPlaylist(_config, station);
            song = station.PlaylistSongs.First(item => item.Id == target.Id);
            if (station.ProviderId == "mpv" && _mpvProvider is { } provider &&
                provider.CurrentPlaylistIndex >= 0 && provider.CurrentPlaylistIndex < station.PlaylistSongs.Count &&
                station.PlaylistSongs[provider.CurrentPlaylistIndex].Id == song.Id &&
                (provider.Snapshot.PositionSeconds < song.StartSeconds ||
                    song.EndSeconds is { } localEnd && provider.Snapshot.PositionSeconds >= localEnd))
                await SelectLocalSongAsync(provider.CurrentPlaylistIndex);
            else if (station.ProviderId == "youtube" && _youtubePlayerReady)
            {
                await SetYouTubeSongsAsync(station);
                if (station.PlaylistSongs.ElementAtOrDefault(station.Runtime.SequenceIndex)?.Id == song.Id &&
                    (station.Runtime.PositionSeconds < song.StartSeconds ||
                        song.EndSeconds is { } youtubeEnd && station.Runtime.PositionSeconds >= youtubeEnd))
                    await YouTubeCommandAsync($"seek({song.StartSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})");
            }
            _lastPlaylistDisplay = null;
            RefreshDashboardPlaylist();
            await _store.SaveAsync(_config);
            Footer.Text = "SONG SAVED · The original media was not changed.";
        }
        catch (Exception error)
        {
            _lastPlaylistDisplay = null;
            RefreshDashboardPlaylist();
            Footer.Text = "COULD NOT EDIT SONG · " + error.Message;
        }
    }

    void DashboardPlaylist_OpenLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_playlistContextSong is null) return;
        RefreshLibrary();
        Show(LibraryView, "MUSIC LIBRARY", "Sources and song cues shared by your stations", LibraryNav);
    }

    void DashboardPlaylist_OpenSource_Click(object sender, RoutedEventArgs e)
    {
        if (_playlistContextSong is not { } song) return;
        var librarySong = _config.MusicLibrary.Songs.FirstOrDefault(candidate => candidate.Id == song.Id);
        var source = librarySong is null ? null : _config.MusicLibrary.Sources.FirstOrDefault(candidate => candidate.Id == librarySong.SourceId);
        if (source is null) return;
        OpenLibrarySource(source);
    }

    async void DashboardPlaylist_RemoveFromStation_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not { } station || _playlistContextSong is not { } song) return;
        try
        {
            MusicLibraryService.RemoveSongFromStation(station, song.Id);
            MusicLibraryService.MaterializeStationPlaylist(_config, station);
            if (station.PlaylistEntries.Count == 0)
            {
                if (station.ProviderId == "youtube" && _youtubePlayerReady) await YouTubeCommandAsync("pause()");
                else if (station.ProviderId == "mpv" && _mpvProvider is not null) await _mpvProvider.PauseAsync();
                station.Runtime.WasPlaying = false;
                station.Runtime.IsOnAir = false;
            }
            else await ApplyPlaylistChangeAsync(CurrentSongId());
            await _store.SaveAsync(_config);
            _lastPlaylistDisplay = null;
            RefreshCollections();
            Footer.Text = $"REMOVED FROM {station.Name.ToUpperInvariant()} · The shared library cue was kept.";
        }
        catch (Exception error) { Footer.Text = "COULD NOT REMOVE FROM STATION · " + error.Message; }
    }

    void DashboardPlaylist_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _playlistDragSong is null) return;
        var point = e.GetPosition(DashboardPlaylist);
        if (Math.Abs(point.X - _playlistDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _playlistDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(DashboardPlaylist, _playlistDragSong, DragDropEffects.Move);
        _playlistDragSong = null;
    }

    void DashboardPlaylist_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(StationSong)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    async void DashboardPlaylist_Drop(object sender, DragEventArgs e)
    {
        if (_active is not { } station || e.Data.GetData(typeof(StationSong)) is not StationSong song) return;
        var target = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as SongCard;
        if (target is null || target.Song.Id == song.Id) return;
        var sourceEntry = station.PlaylistEntries.FirstOrDefault(entry => entry.SongId == song.Id);
        var to = station.PlaylistEntries.FindIndex(entry => entry.Id == target.EntryId);
        if (sourceEntry is null || to < 0) return;
        var currentId = CurrentSongId();
        MusicLibraryService.MoveStationEntry(station, sourceEntry.Id, to);
        MusicLibraryService.MaterializeStationPlaylist(_config, station);
        station.Shuffle = false;
        try { await ApplyPlaylistChangeAsync(currentId); }
        catch (Exception error) { Footer.Text = "COULD NOT REORDER PLAYLIST · " + error.Message; }
    }

    async void DashboardPlaylist_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_active is not { } station || Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not SongCard card) return;
        var index = station.PlaylistSongs.FindIndex(x => x.Id == card.Song.Id);
        try
        {
            if (station.ProviderId == "mpv") await SelectLocalSongAsync(index, playIfPaused: true);
            else if (station.ProviderId == "youtube") await YouTubeCommandAsync($"selectSegment({index})");
        }
        catch (Exception error) { Footer.Text = "COULD NOT PLAY SONG · " + error.Message; }
    }

    async void DashboardShuffle_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not { } station || station.PlaylistSongs.Count < 2) return;
        var currentId = CurrentSongId();
        MusicLibraryService.ShuffleStationEntries(station);
        MusicLibraryService.MaterializeStationPlaylist(_config, station);
        station.Shuffle = true;
        station.ShuffleSeed = null;
        try { await ApplyPlaylistChangeAsync(currentId); }
        catch (Exception error) { Footer.Text = "COULD NOT SHUFFLE PLAYLIST · " + error.Message; }
    }

    Guid? CurrentSongId()
    {
        if (_active is not { } station || station.PlaylistSongs.Count == 0) return null;
        var index = station.ProviderId == "mpv" ? _mpvProvider?.CurrentPlaylistIndex ?? 0 : station.Runtime.SequenceIndex;
        return station.PlaylistSongs[Math.Clamp(index, 0, station.PlaylistSongs.Count - 1)].Id;
    }

    async Task ApplyPlaylistChangeAsync(Guid? activeSongId)
    {
        if (_active is not { } station) return;
        MusicLibraryService.MaterializeStationPlaylist(_config, station);
        var index = activeSongId is { } id ? station.PlaylistSongs.FindIndex(x => x.Id == id) : -1;
        station.Runtime.SequenceIndex = Math.Max(0, index);
        if (station.ProviderId == "mpv")
        {
            station.PlaylistFiles = station.PlaylistSongs.Select(x => x.Source).ToList();
            await ReloadLocalPlaylistAsync(station);
        }
        else if (station.ProviderId == "youtube" && _youtubePlayerReady)
            await SetYouTubeSongsAsync(station);
        _lastPlaylistDisplay = null;
        RefreshActiveStationPresentation(station);
        RefreshDashboardPlaylist();
        await _store.SaveAsync(_config);
        Footer.Text = "PLAYLIST SAVED · Songs play in the order shown.";
    }

    async Task ReloadLocalPlaylistAsync(Station station)
    {
        if (_mpvProvider is not { } old) return;
        var wasPlaying = old.Snapshot.IsPlaying;
        var position = old.Snapshot.PositionSeconds;
        MpvProvider? replacement = null;
        try
        {
            await old.PauseAsync();
            if (_gameMpvProvider is not null) await _gameMpvProvider.PauseAsync();
            replacement = new MpvProvider(new MpvLocator(), _config.MpvPath, _config.MpvAudioDeviceName, LocalHeadsetLoudnessCalibrationDb);
            await replacement.LoadAsync(station);
            await replacement.SetVolumeAsync(_config.MasterVolume * station.Volume);
            var index = Math.Clamp(station.Runtime.SequenceIndex, 0, station.PlaylistSongs.Count - 1);
            var song = station.PlaylistSongs[index];
            if (position < song.StartSeconds || song.EndSeconds is { } end && position >= end) position = song.StartSeconds;
            await replacement.SelectTrackAsync(index, position);
            if (wasPlaying) await replacement.PlayAsync();
            await StopGameOutputAsync();
            _mpvProvider = replacement;
            replacement = null;
            await old.DisposeAsync();
            await LoadGameOutputAsync(station, _mpvProvider);
            if (wasPlaying && _gameMpvProvider is not null) await _gameMpvProvider.PlayAsync();
            station.Runtime.Sequence = station.PlaylistSongs.Select(x => x.Id.ToString("N")).ToList();
            station.Runtime.PositionSeconds = position;
        }
        catch (Exception error)
        {
            if (replacement is not null) await replacement.DisposeAsync();
            if (ReferenceEquals(_mpvProvider, old) && wasPlaying) try { await old.PlayAsync(); } catch { }
            Footer.Text = "PLAYLIST RELOAD FAILED · " + error.Message;
            throw;
        }
    }

    async Task SetYouTubeSongsAsync(Station station)
    {
        var songs = MusicLibraryService.Resolve(_config, station);
        var segments = songs.Select(song =>
        {
            if (!MusicLibraryService.TryGetYouTubeVideoId(song.Source, out var videoId))
                throw new InvalidOperationException("Library song segmentation is available only for a single YouTube video.");
            return new { id = song.SongId.ToString("N"), source = videoId, start = song.StartSeconds, end = song.EndSeconds };
        }).ToList();
        var json = JsonSerializer.Serialize(segments);
        await YouTubeCommandAsync($"setSegments({json},{station.Runtime.SequenceIndex})");
    }

    double TimelineSeconds(Point point)
    {
        var duration = _active?.Runtime.DurationSeconds ?? 0;
        return duration > 0 && TimelineSurface.ActualWidth > 0
            ? Math.Clamp(point.X / TimelineSurface.ActualWidth, 0, 1) * duration : 0;
    }

    void Timeline_MouseMove(object sender, MouseEventArgs e)
    {
        if (_active?.Runtime.DurationSeconds is not > 0) return;
        var point = e.GetPosition(TimelineSurface);
        var seconds = TimelineSeconds(point);
        TimelineHoverText.Text = DisplayTime(seconds);
        TimelineHover.HorizontalOffset = Math.Clamp(point.X - 20, 0, Math.Max(0, TimelineSurface.ActualWidth - 45));
        TimelineHover.VerticalOffset = -23;
        TimelineHover.IsOpen = true;
        if (_timelineDragging) Progress.Value = Math.Clamp(100 * seconds / _active.Runtime.DurationSeconds, 0, 100);
    }

    void Timeline_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_timelineDragging) TimelineHover.IsOpen = false;
    }

    void Timeline_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_active?.Runtime.DurationSeconds is not > 0) return;
        _timelineDragging = true;
        TimelineSurface.CaptureMouse();
        Timeline_MouseMove(sender, e);
        e.Handled = true;
    }

    async void Timeline_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_timelineDragging) return;
        var seconds = TimelineSeconds(e.GetPosition(TimelineSurface));
        _timelineDragging = false;
        TimelineSurface.ReleaseMouseCapture();
        TimelineHover.IsOpen = false;
        try { await SeekTimelineAsync(seconds); }
        catch (Exception error) { Footer.Text = "SEEK FAILED · " + error.Message; }
        e.Handled = true;
    }

    async Task SeekTimelineAsync(double seconds)
    {
        if (_active is not { } station) return;
        if (_mpvProvider is { } provider)
        {
            var current = Math.Clamp(provider.CurrentPlaylistIndex, 0, Math.Max(0, station.PlaylistSongs.Count - 1));
            if (station.PlaylistSongs.Count > 0)
            {
                var source = station.PlaylistSongs[current].Source;
                var index = SongPlaylist.FindCurrent(station.PlaylistSongs, source, seconds);
                await SelectLocalSongAsync(index >= 0 ? index : current, seconds);
            }
            else { await provider.SeekAsync(seconds); await SyncGameToHeadsetAsync(provider); }
        }
        else if (station.ProviderId == "youtube")
        {
            await YouTubeCommandAsync($"seek({seconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})");
            station.Runtime.PositionSeconds = seconds;
        }
        else if (_externalProvider is not null) await _externalProvider.SeekAsync(seconds);
    }

    void Timeline_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _timelineDragging = false;
        TimelineSurface.ReleaseMouseCapture();
        _timelineContextSeconds = TimelineSeconds(Mouse.GetPosition(TimelineSurface));
        var source = _active?.PlaylistSongs.ElementAtOrDefault(Math.Clamp(_active?.Runtime.SequenceIndex ?? 0, 0,
            Math.Max(0, (_active?.PlaylistSongs.Count ?? 0) - 1)))?.Source ?? _active?.Source;
        if (_active?.Runtime.DurationSeconds is not > 0 || _active.ProviderId is not ("mpv" or "youtube") ||
            !MusicLibraryService.SupportsCueRanges(_active.ProviderId, source))
            e.Handled = true;
    }

    async void CreateSong_Click(object sender, RoutedEventArgs e)
    {
        if (_active is not { } station || station.Runtime.DurationSeconds <= 0) return;
        try
        {
            StationSong? current;
            if (station.ProviderId == "mpv" && _mpvProvider is { } provider)
            {
                SongPlaylist.EnsureLocal(station);
                var index = Math.Clamp(provider.CurrentPlaylistIndex, 0, station.PlaylistSongs.Count - 1);
                var source = station.PlaylistSongs[index].Source;
                var containing = SongPlaylist.FindCurrent(station.PlaylistSongs, source, _timelineContextSeconds);
                current = station.PlaylistSongs[containing >= 0 ? containing : index];
            }
            else if (station.ProviderId == "youtube")
            {
                if (station.Source.Contains("list=", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Song splitting currently supports a single YouTube video, not a multi-video playlist.");
                if (station.PlaylistSongs.Count == 0)
                    throw new InvalidOperationException("The station has no canonical library cue for this video.");
                var containing = SongPlaylist.FindCurrent(station.PlaylistSongs, station.PlaylistSongs[0].Source, _timelineContextSeconds);
                if (containing < 0) throw new InvalidOperationException("Choose a point inside an existing song.");
                current = station.PlaylistSongs[containing];
            }
            else return;
            if (!MusicLibraryService.SupportsCueRanges(station.ProviderId, current.Source))
            {
                Footer.Text = "THIS SOURCE DOES NOT SUPPORT RELIABLE SONG SEGMENTATION.";
                return;
            }
            var dialog = new CreateSongWindow(current.Name, DisplayTime(_timelineContextSeconds)) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var playingSource = station.ProviderId == "mpv" && _mpvProvider is { } playingProvider &&
                playingProvider.CurrentPlaylistIndex >= 0 && playingProvider.CurrentPlaylistIndex < station.PlaylistSongs.Count
                ? station.PlaylistSongs[playingProvider.CurrentPlaylistIndex].Source : current.Source;
            var position = station.Runtime.PositionSeconds;
            var entry = station.PlaylistEntries.FirstOrDefault(item => item.SongId == current.Id)
                ?? throw new InvalidOperationException("The current song is not assigned to this station.");
            var created = MusicLibraryService.SplitSong(_config.MusicLibrary, current.Id, _timelineContextSeconds,
                station.Runtime.DurationSeconds, dialog.SongTitle, dialog.NameAfter);
            MusicLibraryService.InsertSplitEntry(station, entry.Id, created.Id, dialog.NameAfter);
            MusicLibraryService.MaterializeStationPlaylist(_config, station);
            var playingIndex = SongPlaylist.FindCurrent(station.PlaylistSongs, playingSource, position);
            var currentId = playingIndex >= 0 ? station.PlaylistSongs[playingIndex].Id : CurrentSongId();
            await ApplyPlaylistChangeAsync(currentId);
        }
        catch (Exception error) { Footer.Text = "COULD NOT CREATE SONG · " + error.Message; }
    }
}
