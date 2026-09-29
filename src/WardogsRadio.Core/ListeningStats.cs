namespace WardogsRadio.Core;

public sealed record ListeningEntityStats(Guid Id, string Name, double AudibleSeconds, int QualifiedPlays,
    int Completed, int Skipped, int UniqueSongs, int Sessions, DateTimeOffset? FirstPlayed,
    DateTimeOffset? LastPlayed);

public sealed record RecentListening(Guid? SongId, string SongName, Guid? StationId, string StationName,
    Guid? SourceId, DateTimeOffset PlayedAt);

public sealed record ListeningStats(double TotalAudibleSeconds, int QualifiedPlays, int UniqueSongs,
    int UniqueStations, int Sessions, double AverageSessionSeconds, double LongestSessionSeconds,
    IReadOnlyList<ListeningEntityStats> Stations, IReadOnlyList<ListeningEntityStats> Songs,
    IReadOnlyList<ListeningEntityStats> Sources, IReadOnlyList<RecentListening> Recent);

public static class ListeningStatsService
{
    public static ListeningStats Aggregate(IEnumerable<ListeningHistoryEntry> history,
        DateTimeOffset? from = null, DateTimeOffset? to = null)
    {
        var all = history.ToArray();
        var lower = from ?? DateTimeOffset.MinValue;
        var upper = to ?? DateTimeOffset.MaxValue;
        var intervals = all.Where(entry => entry.Type == ListeningEntryType.Interval && entry.StartedAt is not null &&
            entry.Timestamp > lower && entry.StartedAt < upper && entry.AudibleSeconds > 0)
            .Select(entry => (Entry: entry, Seconds: ClippedSeconds(entry, lower, upper)))
            .Where(item => item.Seconds > 0).ToArray();
        var events = all.Where(entry => entry.Timestamp >= lower && entry.Timestamp < upper &&
            entry.Type is ListeningEntryType.QualifiedPlay or ListeningEntryType.Skipped or ListeningEntryType.Completed).ToArray();
        var plays = events.Where(entry => entry.Type == ListeningEntryType.QualifiedPlay).ToArray();
        var sessionLengths = intervals.GroupBy(item => item.Entry.SessionId)
            .Select(group => group.Sum(item => item.Seconds)).Where(seconds => seconds > 0).ToArray();
        var stations = BuildEntities(intervals, events, entry => entry.StationId, entry => entry.StationName);
        var songs = BuildEntities(intervals, events, entry => entry.SongId, entry => entry.SongName);
        var sources = BuildEntities(intervals, events, entry => entry.SourceId, entry => entry.SourceName);
        var recent = plays.OrderByDescending(entry => entry.Timestamp).Take(50)
            .Select(entry => new RecentListening(entry.SongId, entry.SongName ?? "Unknown song",
                entry.StationId, entry.StationName ?? "Unknown station", entry.SourceId, entry.Timestamp)).ToArray();
        return new ListeningStats(intervals.Sum(item => item.Seconds), plays.Length,
            plays.Where(entry => entry.SongId is not null).Select(entry => entry.SongId).Distinct().Count(),
            stations.Count, sessionLengths.Length,
            sessionLengths.Length == 0 ? 0 : sessionLengths.Average(),
            sessionLengths.Length == 0 ? 0 : sessionLengths.Max(), stations, songs, sources, recent);
    }

    static IReadOnlyList<ListeningEntityStats> BuildEntities(
        (ListeningHistoryEntry Entry, double Seconds)[] intervals, ListeningHistoryEntry[] events,
        Func<ListeningHistoryEntry, Guid?> id, Func<ListeningHistoryEntry, string?> name)
    {
        var keys = intervals.Select(item => id(item.Entry)).Concat(events.Select(id))
            .Where(value => value is not null).Select(value => value!.Value).Distinct();
        return keys.Select(key =>
        {
            var times = intervals.Where(item => id(item.Entry) == key).ToArray();
            var actions = events.Where(entry => id(entry) == key).ToArray();
            var qualified = actions.Where(entry => entry.Type == ListeningEntryType.QualifiedPlay).ToArray();
            var sample = times.Select(item => item.Entry).Concat(actions).OrderByDescending(entry => entry.Timestamp).First();
            return new ListeningEntityStats(key, name(sample) ?? "Deleted item", times.Sum(item => item.Seconds),
                qualified.Length, actions.Count(entry => entry.Type == ListeningEntryType.Completed),
                actions.Count(entry => entry.Type == ListeningEntryType.Skipped),
                qualified.Where(entry => entry.SongId is not null).Select(entry => entry.SongId).Distinct().Count(),
                times.Select(item => item.Entry.SessionId).Distinct().Count(),
                qualified.Length == 0 ? null : qualified.Min(entry => entry.Timestamp),
                qualified.Length == 0 ? null : qualified.Max(entry => entry.Timestamp));
        }).OrderByDescending(item => item.AudibleSeconds).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    static double ClippedSeconds(ListeningHistoryEntry entry, DateTimeOffset from, DateTimeOffset to)
    {
        var start = entry.StartedAt!.Value;
        var end = entry.Timestamp;
        if (end <= start) return 0;
        var clippedStart = start < from ? from : start;
        var clippedEnd = end > to ? to : end;
        if (clippedEnd <= clippedStart) return 0;
        return entry.AudibleSeconds * (clippedEnd - clippedStart).TotalSeconds / (end - start).TotalSeconds;
    }
}
