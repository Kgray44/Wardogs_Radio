using System.Globalization;

namespace WardogsRadio.Core;

/// <summary>A playable piece of media, shared by any number of library songs.</summary>
public sealed class MediaSource
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProviderId { get; set; } = "mpv";
    public string Source { get; set; } = "";
    public string Name { get; set; } = "Untitled source";
    public double? DurationSeconds { get; set; }
}

/// <summary>A non-destructive cue into a <see cref="MediaSource"/>.</summary>
public sealed class LibrarySong
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceId { get; set; }
    public string Name { get; set; } = "Untitled song";
    public string? Artist { get; set; }
    public double StartSeconds { get; set; }
    public double? EndSeconds { get; set; }
}

/// <summary>The ordering and membership of a song within one station.</summary>
public sealed class StationPlaylistEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SongId { get; set; }
}

public sealed class MusicLibrary
{
    public List<MediaSource> Sources { get; set; } = [];
    public List<LibrarySong> Songs { get; set; } = [];
}

/// <summary>A lightweight runtime join for playback and presentation; it is never persisted.</summary>
public sealed record ResolvedPlaylistSong(Guid EntryId, Guid SongId, Guid SourceId, string Name,
    string? Artist, string ProviderId, string Source, double StartSeconds, double? EndSeconds);

/// <summary>
/// Owns library identity, legacy migration, and referential-integrity normalization. This
/// deliberately contains no provider process or UI logic, so browsing the library is cheap.
/// </summary>
public static class MusicLibraryService
{
    // Schema 12 adds an explicit, opt-in Voicemeeter limiter lease. It remains the single
    // configuration schema marker so a normal save never re-emits retired station-owned
    // playlist fields.
    public const int CurrentSchemaVersion = 12;

    public static IReadOnlyList<ResolvedPlaylistSong> Resolve(AppConfiguration configuration, Station station) =>
        Resolve(configuration.MusicLibrary, station);

    /// <summary>
    /// Makes a short-lived playback projection for existing provider adapters. The projection
    /// is derived from canonical library records every time, so it cannot become a second
    /// source of timing/name truth while adapters are migrated to resolved items.
    /// </summary>
    public static IReadOnlyList<ResolvedPlaylistSong> MaterializeStationPlaylist(AppConfiguration configuration, Station station)
    {
        var resolved = Resolve(configuration, station);
        station.PlaylistSongs = resolved.Select(song => new StationSong
        {
            Id = song.SongId,
            Source = song.Source,
            Name = song.Name,
            StartSeconds = song.StartSeconds,
            EndSeconds = song.EndSeconds
        }).ToList();
        station.PlaylistFiles = resolved.Select(song => song.Source).ToList();
        return resolved;
    }

    public static IReadOnlyList<ResolvedPlaylistSong> Resolve(MusicLibrary library, Station station)
    {
        var songs = library.Songs.ToDictionary(song => song.Id);
        var sources = library.Sources.ToDictionary(source => source.Id);
        return station.PlaylistEntries
            .Where(entry => songs.ContainsKey(entry.SongId) && sources.ContainsKey(songs[entry.SongId].SourceId))
            .Select(entry =>
            {
                var song = songs[entry.SongId];
                var source = sources[song.SourceId];
                return new ResolvedPlaylistSong(entry.Id, song.Id, source.Id, song.Name, song.Artist,
                    source.ProviderId, source.Source, song.StartSeconds, song.EndSeconds);
            })
            .ToList();
    }

    public static MediaSource EnsureSource(MusicLibrary library, string providerId, string source,
        string? name = null, double? durationSeconds = null)
    {
        providerId = NormalizeProvider(providerId);
        source = NormalizeSource(providerId, source);
        if (string.IsNullOrWhiteSpace(source))
            throw new InvalidOperationException("A library source needs a playable media identity.");

        var key = SourceKey(providerId, source);
        var existing = library.Sources.FirstOrDefault(candidate =>
            string.Equals(SourceKey(candidate.ProviderId, candidate.Source), key, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (durationSeconds is > 0 && double.IsFinite(durationSeconds.Value)) existing.DurationSeconds = durationSeconds;
            if (string.IsNullOrWhiteSpace(existing.Name) && !string.IsNullOrWhiteSpace(name)) existing.Name = name.Trim();
            return existing;
        }

        var created = new MediaSource
        {
            ProviderId = providerId,
            Source = source,
            Name = string.IsNullOrWhiteSpace(name) ? DisplayName(source) : name.Trim(),
            DurationSeconds = durationSeconds is > 0 && double.IsFinite(durationSeconds.Value) ? durationSeconds : null
        };
        library.Sources.Add(created);
        return created;
    }

    public static LibrarySong EnsureWholeSourceSong(MusicLibrary library, MediaSource source, string songName)
    {
        songName = string.IsNullOrWhiteSpace(songName) ? source.Name : songName.Trim();
        var existing = library.Songs.FirstOrDefault(song => song.SourceId == source.Id && song.StartSeconds == 0 &&
            song.EndSeconds is null && string.Equals(song.Name, songName, StringComparison.Ordinal));
        if (existing is not null) return existing;
        var created = new LibrarySong { SourceId = source.Id, Name = songName };
        library.Songs.Add(created);
        return created;
    }

    public static LibrarySong DuplicateSong(MusicLibrary library, Guid songId, string? name = null)
    {
        var original = library.Songs.FirstOrDefault(song => song.Id == songId)
            ?? throw new InvalidOperationException("The selected library song no longer exists.");
        var copy = new LibrarySong
        {
            SourceId = original.SourceId,
            Name = string.IsNullOrWhiteSpace(name) ? original.Name + " — Alternate Edit" : name.Trim(),
            Artist = original.Artist,
            StartSeconds = original.StartSeconds,
            EndSeconds = original.EndSeconds
        };
        library.Songs.Add(copy);
        return copy;
    }

    public static LibrarySong CreateSong(MusicLibrary library, Guid sourceId, string name, double startSeconds = 0,
        double? endSeconds = null, string? artist = null, double? sourceDurationSeconds = null)
    {
        if (!library.Sources.Any(source => source.Id == sourceId))
            throw new InvalidOperationException("The selected library source no longer exists.");
        var created = new LibrarySong { SourceId = sourceId, Artist = artist };
        library.Songs.Add(created);
        try
        {
            UpdateSong(library, created.Id, name, startSeconds, endSeconds, sourceDurationSeconds);
            return created;
        }
        catch
        {
            library.Songs.Remove(created);
            throw;
        }
    }

    public static LibrarySong SplitSong(MusicLibrary library, Guid songId, double seconds,
        double durationSeconds, string name, bool nameAfter)
    {
        var original = library.Songs.FirstOrDefault(song => song.Id == songId)
            ?? throw new InvalidOperationException("The selected library song no longer exists.");
        var end = original.EndSeconds ?? durationSeconds;
        if (!double.IsFinite(seconds) || !double.IsFinite(end) ||
            seconds <= original.StartSeconds + .25 || seconds >= end - .25)
            throw new InvalidOperationException("Choose a point inside the song, at least a quarter second from either edge.");
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Give the new song a name.");

        var created = new LibrarySong
        {
            SourceId = original.SourceId,
            Name = name,
            Artist = original.Artist,
            StartSeconds = nameAfter ? seconds : original.StartSeconds,
            EndSeconds = nameAfter ? original.EndSeconds : seconds
        };
        if (nameAfter) original.EndSeconds = seconds;
        else original.StartSeconds = seconds;
        library.Songs.Add(created);
        return created;
    }

    public static void UpdateSong(MusicLibrary library, Guid songId, string name, double startSeconds,
        double? endSeconds, double? sourceDurationSeconds = null)
    {
        var song = library.Songs.FirstOrDefault(candidate => candidate.Id == songId)
            ?? throw new InvalidOperationException("The selected library song no longer exists.");
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Give the song a name.");
        if (!double.IsFinite(startSeconds) || startSeconds < 0 ||
            endSeconds is { } end && (!double.IsFinite(end) || end <= startSeconds + .25))
            throw new InvalidOperationException("The stop time must be at least a quarter second after the start time.");
        if (sourceDurationSeconds is { } duration && double.IsFinite(duration) && duration > 0 &&
            (startSeconds >= duration - .25 || endSeconds is { } stop && stop > duration + .01))
            throw new InvalidOperationException("The song times must fit inside the original track.");
        song.Name = name;
        song.StartSeconds = startSeconds;
        song.EndSeconds = endSeconds;
    }

    public static StationPlaylistEntry AddSongToStation(Station station, Guid songId, int? index = null)
    {
        station.PlaylistEntries ??= [];
        if (station.PlaylistEntries.Any(entry => entry.SongId == songId))
            throw new InvalidOperationException("That song is already assigned to this station.");
        var entry = new StationPlaylistEntry { SongId = songId };
        station.PlaylistEntries.Insert(Math.Clamp(index ?? station.PlaylistEntries.Count, 0, station.PlaylistEntries.Count), entry);
        return entry;
    }

    public static void RemoveSongFromStation(Station station, Guid songId) =>
        station.PlaylistEntries.RemoveAll(entry => entry.SongId == songId);

    /// <summary>Moves only a station reference; the library song itself is not modified.</summary>
    public static void MoveStationEntry(Station station, Guid entryId, int destinationIndex)
    {
        var from = station.PlaylistEntries.FindIndex(entry => entry.Id == entryId);
        if (from < 0) throw new InvalidOperationException("The selected station entry no longer exists.");
        destinationIndex = Math.Clamp(destinationIndex, 0, station.PlaylistEntries.Count - 1);
        if (from == destinationIndex) return;
        var entry = station.PlaylistEntries[from];
        station.PlaylistEntries.RemoveAt(from);
        station.PlaylistEntries.Insert(destinationIndex, entry);
    }

    /// <summary>Shuffles only a station's ordered references, leaving global cue metadata intact.</summary>
    public static void ShuffleStationEntries(Station station, Random? random = null)
    {
        random ??= Random.Shared;
        for (var index = station.PlaylistEntries.Count - 1; index > 0; index--)
        {
            var swap = random.Next(index + 1);
            (station.PlaylistEntries[index], station.PlaylistEntries[swap]) =
                (station.PlaylistEntries[swap], station.PlaylistEntries[index]);
        }
    }

    public static StationPlaylistEntry InsertSplitEntry(Station station, Guid existingEntryId, Guid newSongId, bool after)
    {
        var index = station.PlaylistEntries.FindIndex(entry => entry.Id == existingEntryId);
        if (index < 0) throw new InvalidOperationException("The selected station entry no longer exists.");
        var entry = new StationPlaylistEntry { SongId = newSongId };
        station.PlaylistEntries.Insert(after ? index + 1 : index, entry);
        return entry;
    }

    public static IReadOnlyList<Guid> DeleteSong(AppConfiguration configuration, Guid songId)
    {
        if (configuration.MusicLibrary.Songs.RemoveAll(song => song.Id == songId) == 0)
            throw new InvalidOperationException("The selected library song no longer exists.");
        var affected = configuration.Profile.Stations
            .Where(station => station.PlaylistEntries.Any(entry => entry.SongId == songId))
            .Select(station => station.Id).ToList();
        foreach (var station in configuration.Profile.Stations)
            station.PlaylistEntries.RemoveAll(entry => entry.SongId == songId);
        return affected;
    }

    public static IReadOnlyList<Guid> DeleteSource(AppConfiguration configuration, Guid sourceId)
    {
        if (configuration.MusicLibrary.Sources.RemoveAll(source => source.Id == sourceId) == 0)
            throw new InvalidOperationException("The selected library source no longer exists.");
        var songIds = configuration.MusicLibrary.Songs.Where(song => song.SourceId == sourceId)
            .Select(song => song.Id).ToHashSet();
        configuration.MusicLibrary.Songs.RemoveAll(song => songIds.Contains(song.Id));
        var affected = configuration.Profile.Stations
            .Where(station => station.PlaylistEntries.Any(entry => songIds.Contains(entry.SongId)))
            .Select(station => station.Id).ToList();
        foreach (var station in configuration.Profile.Stations)
            station.PlaylistEntries.RemoveAll(entry => songIds.Contains(entry.SongId));
        return affected;
    }

    public static void NormalizeAndMigrate(AppConfiguration configuration)
    {
        configuration.MusicLibrary ??= new MusicLibrary();
        configuration.MusicLibrary.Sources ??= [];
        configuration.MusicLibrary.Songs ??= [];
        configuration.Profile ??= Defaults.Profile();
        configuration.Profile.Stations ??= [];

        foreach (var source in configuration.MusicLibrary.Sources)
        {
            source.ProviderId = NormalizeProvider(source.ProviderId);
            source.Source = NormalizeSource(source.ProviderId, source.Source);
            source.Name = string.IsNullOrWhiteSpace(source.Name) ? DisplayName(source.Source) : source.Name.Trim();
            if (source.DurationSeconds is not > 0 || !double.IsFinite(source.DurationSeconds.Value)) source.DurationSeconds = null;
        }
        configuration.MusicLibrary.Sources.RemoveAll(source => string.IsNullOrWhiteSpace(source.Source));
        DeduplicateSources(configuration.MusicLibrary);

        foreach (var station in configuration.Profile.Stations)
        {
            station.PlaylistEntries ??= [];
            station.PlaylistSongs ??= [];
            station.PlaylistFiles ??= [];
            if (configuration.SchemaVersion < CurrentSchemaVersion)
                MigrateStation(configuration.MusicLibrary, station);
        }

        NormalizeSongs(configuration.MusicLibrary);
        var knownSongs = configuration.MusicLibrary.Songs.Select(song => song.Id).ToHashSet();
        foreach (var station in configuration.Profile.Stations)
            station.PlaylistEntries.RemoveAll(entry => !knownSongs.Contains(entry.SongId));
        configuration.SchemaVersion = Math.Max(CurrentSchemaVersion, configuration.SchemaVersion);
    }

    /// <summary>Registers a station-created source and its existing playlist with the canonical library.</summary>
    public static void EnsureStationLibrary(AppConfiguration configuration, Station station)
    {
        configuration.MusicLibrary ??= new MusicLibrary();
        configuration.MusicLibrary.Sources ??= [];
        configuration.MusicLibrary.Songs ??= [];
        station.PlaylistEntries ??= [];
        station.PlaylistSongs ??= [];
        station.PlaylistFiles ??= [];
        MigrateStation(configuration.MusicLibrary, station);
        NormalizeSongs(configuration.MusicLibrary);
    }

    /// <summary>
    /// Rebuilds one station's ordered references from its editor/playback projection. Existing
    /// library songs are reused by their stable IDs or exact cue identity; only the station's
    /// membership changes.
    /// </summary>
    public static void ReconcileStationLibrary(AppConfiguration configuration, Station station)
    {
        station.PlaylistEntries ??= [];
        station.PlaylistEntries.Clear();
        EnsureStationLibrary(configuration, station);
    }

    static void MigrateStation(MusicLibrary library, Station station)
    {
        if (station.PlaylistEntries.Count > 0) return;
        foreach (var legacySong in station.PlaylistSongs)
        {
            if (string.IsNullOrWhiteSpace(legacySong.Source)) continue;
            var source = EnsureSource(library, station.ProviderId, legacySong.Source, station.Name);
            var canonical = library.Songs.FirstOrDefault(song => song.Id == legacySong.Id && song.SourceId == source.Id)
                ?? FindOrAddSong(library, source, legacySong.Name, legacySong.StartSeconds, legacySong.EndSeconds);
            station.PlaylistEntries.Add(new StationPlaylistEntry { SongId = canonical.Id });
        }
        if (station.PlaylistEntries.Count > 0) return;

        foreach (var path in station.PlaylistFiles)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var source = EnsureSource(library, station.ProviderId, path, DisplayName(path));
            var song = EnsureWholeSourceSong(library, source, DisplayName(path));
            station.PlaylistEntries.Add(new StationPlaylistEntry { SongId = song.Id });
        }
        if (station.PlaylistEntries.Count > 0 || string.IsNullOrWhiteSpace(station.Source)) return;

        if (station.ProviderId == "youtube" && TryGetYouTubePlaylistId(station.Source, out _))
        {
            EnsureSource(library, "youtube", station.Source, station.Name);
            return; // A multi-video playlist is a source collection, not one seekable song.
        }

        if (!SupportsCueRanges(station.ProviderId, station.Source)) return;
        var stationSource = EnsureSource(library, station.ProviderId, station.Source, station.Name);
        var whole = EnsureWholeSourceSong(library, stationSource, station.Name);
        station.PlaylistEntries.Add(new StationPlaylistEntry { SongId = whole.Id });
    }

    static LibrarySong FindOrAddSong(MusicLibrary library, MediaSource source, string name, double start, double? end)
    {
        name = string.IsNullOrWhiteSpace(name) ? source.Name : name.Trim();
        start = double.IsFinite(start) ? Math.Max(0, start) : 0;
        if (end is { } stop && (!double.IsFinite(stop) || stop <= start + .25)) end = null;
        var existing = library.Songs.FirstOrDefault(song => song.SourceId == source.Id && song.StartSeconds == start &&
            song.EndSeconds == end && string.Equals(song.Name, name, StringComparison.Ordinal));
        if (existing is not null) return existing;
        var created = new LibrarySong { SourceId = source.Id, Name = name, StartSeconds = start, EndSeconds = end };
        library.Songs.Add(created);
        return created;
    }

    static void NormalizeSongs(MusicLibrary library)
    {
        var sourceIds = library.Sources.Select(source => source.Id).ToHashSet();
        foreach (var song in library.Songs)
        {
            song.Name = string.IsNullOrWhiteSpace(song.Name) ? "Untitled song" : song.Name.Trim();
            song.StartSeconds = double.IsFinite(song.StartSeconds) ? Math.Max(0, song.StartSeconds) : 0;
            if (song.EndSeconds is { } stop && (!double.IsFinite(stop) || stop <= song.StartSeconds + .25)) song.EndSeconds = null;
        }
        library.Songs.RemoveAll(song => !sourceIds.Contains(song.SourceId));
    }

    static void DeduplicateSources(MusicLibrary library)
    {
        var canonical = new Dictionary<string, MediaSource>(StringComparer.Ordinal);
        var replacements = new Dictionary<Guid, Guid>();
        foreach (var source in library.Sources.ToList())
        {
            var key = SourceKey(source.ProviderId, source.Source);
            if (!canonical.TryGetValue(key, out var kept))
            {
                canonical[key] = source;
                continue;
            }
            if (kept.DurationSeconds is null && source.DurationSeconds is > 0) kept.DurationSeconds = source.DurationSeconds;
            replacements[source.Id] = kept.Id;
            library.Sources.Remove(source);
        }
        foreach (var song in library.Songs)
            if (replacements.TryGetValue(song.SourceId, out var replacement)) song.SourceId = replacement;
    }

    static string NormalizeProvider(string? providerId) => string.IsNullOrWhiteSpace(providerId) ? "mpv" : providerId.Trim().ToLowerInvariant();

    static string SourceKey(string providerId, string source) => NormalizeProvider(providerId) + "|" + NormalizeSource(providerId, source).ToUpperInvariant();

    public static string NormalizeSource(string? providerId, string? source)
    {
        var provider = NormalizeProvider(providerId);
        var value = source?.Trim() ?? "";
        if (provider == "youtube" && TryGetYouTubeVideoId(value, out var videoId))
            return "https://www.youtube.com/watch?v=" + videoId;
        if (provider == "youtube" && TryGetYouTubePlaylistId(value, out var playlistId))
            return "https://www.youtube.com/playlist?list=" + playlistId;
        if (provider == "mpv" && !Uri.TryCreate(value, UriKind.Absolute, out _))
        {
            try { return Path.GetFullPath(value); }
            catch (Exception) { return value; }
        }
        return value;
    }

    public static bool TryGetYouTubeVideoId(string? source, out string videoId)
    {
        videoId = "";
        if (string.IsNullOrWhiteSpace(source)) return false;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri))
        {
            if (source.Length is < 6 or > 32 || source.Any(character => !char.IsLetterOrDigit(character) && character is not '-' and not '_')) return false;
            videoId = source;
            return true;
        }
        if (uri.Host.Equals("youtu.be", StringComparison.OrdinalIgnoreCase)) videoId = uri.AbsolutePath.Trim('/');
        else if (IsYouTubeHost(uri.Host))
            videoId = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2)).FirstOrDefault(part => part[0].Equals("v", StringComparison.OrdinalIgnoreCase))?.ElementAtOrDefault(1) ?? "";
        return videoId.Length is >= 6 and <= 32 && videoId.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
    }

    public static bool TryGetYouTubePlaylistId(string? source, out string playlistId)
    {
        playlistId = "";
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || !IsYouTubeHost(uri.Host)) return false;
        playlistId = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).FirstOrDefault(part => part[0].Equals("list", StringComparison.OrdinalIgnoreCase))?.ElementAtOrDefault(1) ?? "";
        return playlistId.Length is >= 6 and <= 100 && playlistId.All(character => char.IsLetterOrDigit(character) || character is '-' or '_');
    }

    static bool IsYouTubeHost(string host) => host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("music.youtube.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Only sources with a stable single-media timeline may expose cue boundaries.</summary>
    public static bool SupportsCueRanges(string? providerId, string? source)
    {
        var provider = NormalizeProvider(providerId);
        if (provider == "youtube") return TryGetYouTubeVideoId(source, out _);
        if (provider != "mpv" || string.IsNullOrWhiteSpace(source)) return false;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)) return uri.Scheme.Equals("file", StringComparison.OrdinalIgnoreCase);
        return File.Exists(source);
    }

    static string DisplayName(string source)
    {
        if (TryGetYouTubeVideoId(source, out var videoId)) return "YouTube " + videoId;
        var fileName = Path.GetFileNameWithoutExtension(source);
        return string.IsNullOrWhiteSpace(fileName) ? source : fileName;
    }
}
