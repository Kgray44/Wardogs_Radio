using System.Text.Json;
using System.Text.Json.Serialization;

namespace WardogsRadio.Core;

public enum ListeningEntryType { SessionStarted, SessionEnded, Interval, QualifiedPlay, Skipped, Completed }
public enum TrackEndReason { Neutral, Skipped, Completed }

/// <summary>One durable local history fact. Names are snapshots so deleted Library items remain readable.</summary>
public sealed record ListeningHistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public ListeningEntryType Type { get; init; }
    public DateTimeOffset Timestamp { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public double AudibleSeconds { get; init; }
    public Guid SessionId { get; init; }
    public Guid? PlayId { get; init; }
    public Guid? StationId { get; init; }
    public Guid? SongId { get; init; }
    public Guid? SourceId { get; init; }
    public string? StationName { get; init; }
    public string? SongName { get; init; }
    public string? SourceName { get; init; }
}

public sealed record ListeningObservation(Guid StationId, string StationName, Guid? SongId, string? SongName,
    Guid? SourceId, string? SourceName, bool IsPlaying, bool IsActiveStation, double EffectiveGain,
    bool IsMuted = false, bool IsSuppressed = false, bool IsPreview = false, bool HasListeningRoute = true);

public static class ListeningAudibility
{
    public const double MinimumEffectiveGain = .005;
    public static bool IsAudibleListening(ListeningObservation? observation) => observation is
    {
        IsPlaying: true, IsActiveStation: true, IsMuted: false, IsSuppressed: false,
        IsPreview: false, HasListeningRoute: true
    } && observation.EffectiveGain > MinimumEffectiveGain;
}

/// <summary>
/// A deterministic playback-to-history boundary. Only one observation is authoritative at a time;
/// during crossfade the outgoing station owns time until the active station handoff.
/// </summary>
public sealed class ListeningHistoryRecorder
{
    public static readonly TimeSpan PlayQualification = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan SessionIdleLimit = TimeSpan.FromMinutes(30);
    readonly List<ListeningHistoryEntry> _pending = [];
    ListeningObservation? _current;
    Guid _sessionId;
    Guid _playId;
    Guid _intervalId;
    DateTimeOffset? _intervalStart;
    DateTimeOffset? _lastObserved;
    DateTimeOffset? _lastAudible;
    double _attemptAudibleSeconds;
    bool _qualified;

    public void Observe(ListeningObservation? observation, DateTimeOffset now)
    {
        if (_lastObserved is { } prior && now < prior) throw new ArgumentOutOfRangeException(nameof(now));
        var audible = ListeningAudibility.IsAudibleListening(observation);
        var changed = _current is not null && (!audible || observation!.StationId != _current.StationId ||
            observation.SongId != _current.SongId || observation.SourceId != _current.SourceId);
        if (changed) CloseInterval(now);
        if (_current is not null && audible && observation is not null &&
            (observation.StationId != _current.StationId || observation.SongId != _current.SongId ||
             observation.SourceId != _current.SourceId))
        {
            // A station handoff is neither a skip nor a natural completion.
            // A same-station identity change is the provider advancing to a new cue.
            FinishTrack(observation.StationId == _current.StationId ? TrackEndReason.Completed : TrackEndReason.Neutral, now);
        }
        if (_current is not null && _lastAudible is { } last && now - last >= SessionIdleLimit && !audible)
            FinishTrack(TrackEndReason.Neutral, now);
        if (_sessionId != Guid.Empty && _lastAudible is { } idleSince && now - idleSince >= SessionIdleLimit && !audible)
            EndSession(now);
        if (!audible || observation is null) { _lastObserved = now; return; }
        if (_sessionId == Guid.Empty) StartSession(now);
        if (_current is null)
        {
            _current = observation;
            _playId = Guid.NewGuid();
            _attemptAudibleSeconds = 0;
            _qualified = false;
        }
        if (_intervalStart is null)
        {
            _intervalStart = now;
            _intervalId = Guid.NewGuid();
        }
        _lastObserved = now;
        _lastAudible = now;
        QualifyIfNeeded(now);
    }

    public void EndTrack(TrackEndReason reason, DateTimeOffset now)
    {
        CloseInterval(now);
        FinishTrack(reason, now);
        _lastObserved = now;
    }

    public void Close(DateTimeOffset now)
    {
        EndTrack(TrackEndReason.Neutral, now);
        EndSession(now);
    }

    public ListeningHistoryEntry? Checkpoint()
    {
        if (_current is null || _intervalStart is not { } start || _lastObserved is not { } last || last <= start) return null;
        return Entry(ListeningEntryType.Interval, last) with
        {
            Id = _intervalId, StartedAt = start, AudibleSeconds = (last - start).TotalSeconds
        };
    }

    public IReadOnlyList<ListeningHistoryEntry> Drain()
    {
        var result = _pending.ToArray();
        _pending.Clear();
        return result;
    }

    void StartSession(DateTimeOffset now)
    {
        _sessionId = Guid.NewGuid();
        _pending.Add(new ListeningHistoryEntry { Type = ListeningEntryType.SessionStarted, Timestamp = now, SessionId = _sessionId });
    }

    void EndSession(DateTimeOffset now)
    {
        if (_sessionId == Guid.Empty) return;
        _pending.Add(new ListeningHistoryEntry { Type = ListeningEntryType.SessionEnded, Timestamp = now, SessionId = _sessionId });
        _sessionId = Guid.Empty;
    }

    void CloseInterval(DateTimeOffset now)
    {
        if (_current is null || _intervalStart is not { } start) return;
        // The last observed state is trustworthy; a hung UI or crash must not fabricate hours.
        var end = _lastObserved is { } observed && observed.AddSeconds(2) < now ? observed.AddSeconds(2) : now;
        if (end > start)
        {
            var seconds = (end - start).TotalSeconds;
            _attemptAudibleSeconds += seconds;
            _pending.Add(Entry(ListeningEntryType.Interval, end) with
            {
                Id = _intervalId, StartedAt = start, AudibleSeconds = seconds
            });
            _intervalStart = null;
            QualifyIfNeeded(end);
        }
        _intervalStart = null;
        _intervalId = Guid.Empty;
    }

    void QualifyIfNeeded(DateTimeOffset now)
    {
        if (_qualified || _current is null) return;
        var liveSeconds = _intervalStart is { } start ? Math.Max(0, (now - start).TotalSeconds) : 0;
        if (_attemptAudibleSeconds + liveSeconds < PlayQualification.TotalSeconds) return;
        _qualified = true;
        _pending.Add(Entry(ListeningEntryType.QualifiedPlay, now));
    }

    void FinishTrack(TrackEndReason reason, DateTimeOffset now)
    {
        if (_current is null) return;
        if (_qualified && reason is TrackEndReason.Skipped or TrackEndReason.Completed)
            _pending.Add(Entry(reason == TrackEndReason.Skipped ? ListeningEntryType.Skipped : ListeningEntryType.Completed, now));
        _current = null;
        _playId = Guid.Empty;
        _attemptAudibleSeconds = 0;
        _qualified = false;
    }

    ListeningHistoryEntry Entry(ListeningEntryType type, DateTimeOffset now) => new()
    {
        Type = type, Timestamp = now, SessionId = _sessionId, PlayId = _playId,
        StationId = _current?.StationId, StationName = _current?.StationName,
        SongId = _current?.SongId, SongName = _current?.SongName,
        SourceId = _current?.SourceId, SourceName = _current?.SourceName
    };
}

/// <summary>Append-only local journal plus an atomic bounded checkpoint for crash recovery.</summary>
public sealed class ListeningHistoryStore(string applicationRoot)
{
    static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    readonly SemaphoreSlim _gate = new(1, 1);
    public string DirectoryPath => Path.Combine(applicationRoot, "Listening");
    public string JournalPath => Path.Combine(DirectoryPath, "history-v1.jsonl");
    public string CheckpointPath => Path.Combine(DirectoryPath, "checkpoint-v1.json");

    public async Task AppendAsync(IEnumerable<ListeningHistoryEntry> entries, CancellationToken cancellationToken = default)
    {
        var records = entries.ToArray();
        if (records.Length == 0) return;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            await using var stream = new FileStream(JournalPath, FileMode.Append, FileAccess.Write, FileShare.Read,
                8192, FileOptions.Asynchronous | FileOptions.WriteThrough);
            await using var writer = new StreamWriter(stream);
            foreach (var entry in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(entry, Json));
            }
            await writer.FlushAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task CheckpointAsync(ListeningHistoryEntry? interval, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            if (interval is null) { if (File.Exists(CheckpointPath)) File.Delete(CheckpointPath); return; }
            var temporary = CheckpointPath + ".new";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(interval, Json), cancellationToken);
            File.Move(temporary, CheckpointPath, true);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<ListeningHistoryEntry>> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var records = new List<ListeningHistoryEntry>();
            var ids = new HashSet<Guid>();
            if (File.Exists(JournalPath))
            {
                using var reader = new StreamReader(JournalPath);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<ListeningHistoryEntry>(line, Json);
                        if (entry is not null && ids.Add(entry.Id)) records.Add(entry);
                    }
                    catch (JsonException) { /* A truncated final line cannot destroy prior history. */ }
                }
            }
            if (File.Exists(CheckpointPath))
            {
                try
                {
                    var checkpoint = JsonSerializer.Deserialize<ListeningHistoryEntry>(
                        await File.ReadAllTextAsync(CheckpointPath, cancellationToken), Json);
                    if (checkpoint is not null && ids.Add(checkpoint.Id)) records.Add(checkpoint);
                }
                catch (JsonException) { /* Ignore an incomplete crash checkpoint. */ }
            }
            return records;
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(JournalPath)) File.Delete(JournalPath);
            if (File.Exists(CheckpointPath)) File.Delete(CheckpointPath);
        }
        finally { _gate.Release(); }
    }

    public async Task RecoverCheckpointAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(CheckpointPath)) return;
            ListeningHistoryEntry? checkpoint;
            try { checkpoint = JsonSerializer.Deserialize<ListeningHistoryEntry>(
                await File.ReadAllTextAsync(CheckpointPath, cancellationToken), Json); }
            catch (JsonException) { checkpoint = null; }
            if (checkpoint is not null)
            {
                var alreadyWritten = false;
                if (File.Exists(JournalPath))
                {
                    using var reader = new StreamReader(JournalPath);
                    while (await reader.ReadLineAsync(cancellationToken) is { } line)
                    {
                        try
                        {
                            if (JsonSerializer.Deserialize<ListeningHistoryEntry>(line, Json)?.Id == checkpoint.Id)
                            { alreadyWritten = true; break; }
                        }
                        catch (JsonException) { }
                    }
                }
                if (!alreadyWritten)
                    await File.AppendAllTextAsync(JournalPath, JsonSerializer.Serialize(checkpoint, Json) + Environment.NewLine, cancellationToken);
            }
            File.Delete(CheckpointPath);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Atomically replaces or deduplicates imported history after package validation.</summary>
    public async Task ImportAsync(IEnumerable<ListeningHistoryEntry> imported, bool replace,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var records = new Dictionary<Guid, ListeningHistoryEntry>();
            if (!replace && File.Exists(JournalPath))
            {
                using var reader = new StreamReader(JournalPath);
                while (await reader.ReadLineAsync(cancellationToken) is { } line)
                {
                    try
                    {
                        var entry = JsonSerializer.Deserialize<ListeningHistoryEntry>(line, Json);
                        if (entry is not null) records.TryAdd(entry.Id, entry);
                    }
                    catch (JsonException) { }
                }
            }
            if (!replace && File.Exists(CheckpointPath))
            {
                try
                {
                    var checkpoint = JsonSerializer.Deserialize<ListeningHistoryEntry>(
                        await File.ReadAllTextAsync(CheckpointPath, cancellationToken), Json);
                    if (checkpoint is not null) records.TryAdd(checkpoint.Id, checkpoint);
                }
                catch (JsonException) { }
            }
            foreach (var entry in imported) records.TryAdd(entry.Id, entry);
            var temporary = JournalPath + ".import-new";
            try
            {
                await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                    8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
                await using (var writer = new StreamWriter(stream))
                {
                    foreach (var entry in records.Values.OrderBy(value => value.Timestamp))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await writer.WriteLineAsync(JsonSerializer.Serialize(entry, Json));
                    }
                    await writer.FlushAsync(cancellationToken);
                }
                File.Move(temporary, JournalPath, true);
                if (File.Exists(CheckpointPath)) File.Delete(CheckpointPath);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        finally { _gate.Release(); }
    }
}
