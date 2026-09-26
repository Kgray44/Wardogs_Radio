namespace WardogsRadio.Playback;

/// <summary>Turns a local folder into a bounded, stable list of files mpv can play.</summary>
public static class LocalMediaPlaylist
{
    private const int MaximumTracks = 5000;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma",
        ".aiff", ".aif", ".alac", ".ape", ".wv", ".mka", ".m3u", ".m3u8"
    };

    public static IReadOnlyList<string> FromFiles(IEnumerable<string> paths, bool shuffle, Random? random = null)
    {
        var tracks = new List<string>();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path) || !Extensions.Contains(Path.GetExtension(path)))
                throw new InvalidOperationException($"Unsupported audio or playlist file: {path}");
            if (!File.Exists(path)) throw new FileNotFoundException($"Playlist file not found: {path}", path);
            tracks.Add(Path.GetFullPath(path));
            if (tracks.Count > MaximumTracks) throw new InvalidOperationException($"A playlist can contain at most {MaximumTracks} tracks.");
        }
        if (tracks.Count == 0) throw new InvalidOperationException("Add at least one audio file to the playlist.");
        if (shuffle)
        {
            random ??= Random.Shared;
            for (var i = tracks.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (tracks[i], tracks[j]) = (tracks[j], tracks[i]);
            }
        }
        return tracks;
    }

    public static IReadOnlyList<string> FromDirectory(string path, bool shuffle, Random? random = null)
    {
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException($"Music folder not found: {path}");
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        var tracks = new List<string>();
        foreach (var file in Directory.EnumerateFiles(path, "*", options))
        {
            if (!Extensions.Contains(Path.GetExtension(file))) continue;
            tracks.Add(Path.GetFullPath(file));
            if (tracks.Count > MaximumTracks)
                throw new InvalidOperationException($"Music folder contains more than {MaximumTracks} supported files. Select a smaller folder or playlist.");
        }
        if (tracks.Count == 0) throw new InvalidOperationException("Music folder has no supported audio files or playlists.");
        tracks.Sort(StringComparer.OrdinalIgnoreCase);
        if (shuffle)
        {
            random ??= Random.Shared;
            for (var i = tracks.Count - 1; i > 0; i--)
            {
                var j = random.Next(i + 1);
                (tracks[i], tracks[j]) = (tracks[j], tracks[i]);
            }
        }
        return tracks;
    }
}
