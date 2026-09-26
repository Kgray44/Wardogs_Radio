namespace WardogsRadio.Core;

/// <summary>Persistent playback order and non-destructive song boundaries.</summary>
public static class SongPlaylist
{
    public static bool HasBoundaries(Station station) => station.PlaylistSongs.Any(song => song.StartSeconds > 0 || song.EndSeconds is not null);

    public static void EnsureLocal(Station station)
    {
        if (station.PlaylistSongs.Count > 0) return;
        var sources = station.PlaylistFiles.Count > 0
            ? station.PlaylistFiles.ToList()
            : File.Exists(station.Source) ? [station.Source] : [];
        station.PlaylistSongs = sources.Select(path => new StationSong
        {
            Source = path,
            Name = Path.GetFileNameWithoutExtension(path)
        }).ToList();
        if (station.Shuffle && station.ShuffleSeed is { } seed)
            Shuffle(station.PlaylistSongs, new Random(seed));
    }

    public static StationSong Split(List<StationSong> songs, Guid songId, double seconds,
        double durationSeconds, string name, bool nameAfter)
    {
        var index = songs.FindIndex(song => song.Id == songId);
        if (index < 0) throw new InvalidOperationException("The selected song is no longer in this station.");
        var original = songs[index];
        var end = original.EndSeconds ?? durationSeconds;
        if (!double.IsFinite(seconds) || !double.IsFinite(end) ||
            seconds <= original.StartSeconds + .25 || seconds >= end - .25)
            throw new InvalidOperationException("Choose a point inside the song, at least a quarter second from either edge.");
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Give the new song a name.");
        var newSong = new StationSong
        {
            Source = original.Source,
            Name = name,
            StartSeconds = nameAfter ? seconds : original.StartSeconds,
            EndSeconds = nameAfter ? original.EndSeconds : seconds
        };
        if (nameAfter)
        {
            original.EndSeconds = seconds;
            songs.Insert(index + 1, newSong);
        }
        else
        {
            original.StartSeconds = seconds;
            songs.Insert(index, newSong);
        }
        return newSong;
    }

    public static void Update(List<StationSong> songs, Guid songId, string name,
        double startSeconds, double? endSeconds, double? durationSeconds = null)
    {
        var song = songs.FirstOrDefault(item => item.Id == songId)
            ?? throw new InvalidOperationException("The selected song is no longer in this station.");
        name = name.Trim();
        if (name.Length == 0) throw new InvalidOperationException("Give the song a name.");
        if (!double.IsFinite(startSeconds) || startSeconds < 0 ||
            endSeconds is { } end && (!double.IsFinite(end) || end <= startSeconds + .25))
            throw new InvalidOperationException("The stop time must be at least a quarter second after the start time.");
        if (durationSeconds is { } duration && double.IsFinite(duration) && duration > 0 &&
            (startSeconds >= duration - .25 || endSeconds is { } stop && stop > duration + .01))
            throw new InvalidOperationException("The song times must fit inside the original track.");
        song.Name = name;
        song.StartSeconds = startSeconds;
        song.EndSeconds = endSeconds;
    }

    public static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length == 1)
            return double.TryParse(parts[0], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out seconds) && double.IsFinite(seconds) && seconds >= 0;
        if (parts.Length is < 2 or > 3) return false;
        double total = 0;
        for (var index = 0; index < parts.Length; index++)
        {
            if (!double.TryParse(parts[index], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var part) || !double.IsFinite(part) || part < 0 ||
                index > 0 && part >= 60 || index < parts.Length - 1 && part != Math.Floor(part)) return false;
            total = total * 60 + part;
        }
        seconds = total;
        return true;
    }

    public static void Move(List<StationSong> songs, int from, int to)
    {
        if (from < 0 || from >= songs.Count || to < 0 || to >= songs.Count)
            throw new ArgumentOutOfRangeException(nameof(from));
        var song = songs[from];
        songs.RemoveAt(from);
        songs.Insert(to, song);
    }

    public static void Shuffle(List<StationSong> songs, Random? random = null)
    {
        random ??= Random.Shared;
        if (songs.Count < 2) return;
        var before = songs.Select(x => x.Id).ToArray();
        for (var index = songs.Count - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (songs[index], songs[other]) = (songs[other], songs[index]);
        }
        if (songs.Select(x => x.Id).SequenceEqual(before))
            (songs[0], songs[1]) = (songs[1], songs[0]);
    }

    public static int FindCurrent(IReadOnlyList<StationSong> songs, string source, double seconds)
    {
        for (var index = 0; index < songs.Count; index++)
        {
            var song = songs[index];
            if (string.Equals(song.Source, source, StringComparison.OrdinalIgnoreCase) &&
                seconds >= song.StartSeconds && (song.EndSeconds is null || seconds < song.EndSeconds))
                return index;
        }
        return -1;
    }
}
