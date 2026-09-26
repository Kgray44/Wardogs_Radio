namespace WardogsRadio.Core;

public static class StationEditPlanner
{
    /// <summary>Changing the queue order or playback controls must not restart the active source.</summary>
    public static bool RequiresPlaybackReload(string previousProvider, string previousSource,
        IReadOnlyList<string>? previousFiles, Station edited)
    {
        if (!string.Equals(previousProvider, edited.ProviderId, StringComparison.OrdinalIgnoreCase)) return true;
        var before = previousFiles ?? [];
        var after = (IReadOnlyList<string>?)edited.PlaylistFiles ?? [];
        if (before.Count != after.Count) return true;
        if (!before.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .SequenceEqual(after.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase)) return true;
        return before.Count == 0 && !string.Equals(previousSource, edited.Source, StringComparison.Ordinal);
    }
}
