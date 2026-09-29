using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class ListeningHistoryTests
{
    static readonly DateTimeOffset Start = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
    static readonly Guid Cruise = Guid.NewGuid(), Combat = Guid.NewGuid(), Song = Guid.NewGuid(), Source = Guid.NewGuid();

    [Fact]
    public void AudibleSixtySecondsQualifiesOnceAndPauseTimeIsExcluded()
    {
        var recorder = new ListeningHistoryRecorder();
        recorder.Observe(Observation(Cruise), Start);
        recorder.Observe(Observation(Cruise), Start.AddSeconds(60));
        recorder.Observe(Observation(Cruise) with { IsPlaying = false }, Start.AddSeconds(60));
        recorder.Observe(Observation(Cruise) with { IsPlaying = false }, Start.AddMinutes(5));
        recorder.Observe(Observation(Cruise), Start.AddMinutes(5));
        recorder.Observe(Observation(Cruise), Start.AddMinutes(5).AddSeconds(30));
        recorder.Close(Start.AddMinutes(5).AddSeconds(30));

        var stats = ListeningStatsService.Aggregate(recorder.Drain());
        Assert.Equal(90, stats.TotalAudibleSeconds, 3);
        Assert.Equal(1, stats.QualifiedPlays);
        Assert.Equal(1, stats.Sessions);
        Assert.Equal(0, Assert.Single(stats.Songs).Skipped);
    }

    [Fact]
    public void SilentRadioModeAndPreviewNeverCount()
    {
        var recorder = new ListeningHistoryRecorder();
        recorder.Observe(Observation(Combat) with { IsActiveStation = false }, Start);
        recorder.Observe(Observation(Combat) with { IsActiveStation = false }, Start.AddSeconds(60));
        recorder.Observe(Observation(Combat) with { IsPreview = true }, Start.AddSeconds(90));
        recorder.Observe(Observation(Cruise), Start.AddSeconds(90));
        recorder.Observe(Observation(Cruise), Start.AddSeconds(150));
        recorder.Close(Start.AddSeconds(150));

        var stats = ListeningStatsService.Aggregate(recorder.Drain());
        Assert.Equal(60, stats.TotalAudibleSeconds, 3);
        Assert.Equal(Cruise, Assert.Single(stats.Stations).Id);
    }

    [Fact]
    public void QualificationSeparatesShortSkipQualifiedSkipAndCompletion()
    {
        var recorder = new ListeningHistoryRecorder();
        recorder.Observe(Observation(Cruise), Start);
        recorder.Observe(Observation(Cruise), Start.AddSeconds(2));
        recorder.EndTrack(TrackEndReason.Skipped, Start.AddSeconds(2));
        recorder.Observe(Observation(Cruise), Start.AddSeconds(3));
        recorder.Observe(Observation(Cruise), Start.AddSeconds(20));
        recorder.EndTrack(TrackEndReason.Skipped, Start.AddSeconds(20));
        recorder.Observe(Observation(Cruise), Start.AddSeconds(21));
        recorder.Observe(Observation(Cruise), Start.AddSeconds(40));
        recorder.EndTrack(TrackEndReason.Completed, Start.AddSeconds(40));

        var stats = ListeningStatsService.Aggregate(recorder.Drain());
        Assert.Equal(2, stats.QualifiedPlays);
        Assert.Equal(1, Assert.Single(stats.Songs).Skipped);
        Assert.Equal(1, Assert.Single(stats.Songs).Completed);
    }

    [Fact]
    public void StationHandoffNeverDoublesCrossfadeWallClock()
    {
        var recorder = new ListeningHistoryRecorder();
        recorder.Observe(Observation(Cruise), Start);
        recorder.Observe(Observation(Cruise), Start.AddSeconds(30));
        // The incoming player can be audible during this fade, but the outgoing
        // active station remains the sole accounting owner until handoff.
        recorder.Observe(Observation(Combat), Start.AddSeconds(32));
        recorder.Observe(Observation(Combat), Start.AddSeconds(62));
        recorder.Close(Start.AddSeconds(62));

        var stats = ListeningStatsService.Aggregate(recorder.Drain());
        Assert.Equal(62, stats.TotalAudibleSeconds, 3);
        Assert.Equal(32, stats.Stations.Single(value => value.Id == Cruise).AudibleSeconds, 3);
        Assert.Equal(30, stats.Stations.Single(value => value.Id == Combat).AudibleSeconds, 3);
        Assert.Equal(0, stats.Stations.Single(value => value.Id == Cruise).Completed);
    }

    [Fact]
    public async Task JournalSurvivesRestartAndCheckpointDoesNotDuplicateClosedInterval()
    {
        var root = Path.Combine(Path.GetTempPath(), "wardogs-listening-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ListeningHistoryStore(root);
            var recorder = new ListeningHistoryRecorder();
            recorder.Observe(Observation(Cruise), Start);
            recorder.Observe(Observation(Cruise), Start.AddSeconds(30));
            await store.AppendAsync(recorder.Drain());
            await store.CheckpointAsync(recorder.Checkpoint());
            recorder.Close(Start.AddSeconds(30));
            await store.AppendAsync(recorder.Drain());
            var recovered = await new ListeningHistoryStore(root).ReadAsync();

            Assert.Equal(30, ListeningStatsService.Aggregate(recovered).TotalAudibleSeconds, 3);
            Assert.Single(recovered, entry => entry.Type == ListeningEntryType.Interval);
            await store.ClearAsync();
            Assert.Empty(await store.ReadAsync());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task StaleCheckpointRecoversOnlyObservedTime()
    {
        var root = Path.Combine(Path.GetTempPath(), "wardogs-listening-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ListeningHistoryStore(root);
            var recorder = new ListeningHistoryRecorder();
            recorder.Observe(Observation(Cruise), Start);
            recorder.Observe(Observation(Cruise), Start.AddSeconds(30));
            await store.AppendAsync(recorder.Drain());
            await store.CheckpointAsync(recorder.Checkpoint());
            await new ListeningHistoryStore(root).RecoverCheckpointAsync();

            var recovered = await new ListeningHistoryStore(root).ReadAsync();
            Assert.Equal(30, ListeningStatsService.Aggregate(recovered).TotalAudibleSeconds, 3);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void DeletedLibraryObjectsStillHaveReadableHistoricalNames()
    {
        var recorder = new ListeningHistoryRecorder();
        recorder.Observe(Observation(Cruise), Start);
        recorder.Observe(Observation(Cruise), Start.AddSeconds(20));
        recorder.Close(Start.AddSeconds(20));
        var stats = ListeningStatsService.Aggregate(recorder.Drain());
        Assert.Equal("Cruise", Assert.Single(stats.Stations).Name);
        Assert.Equal("Fortunate Son", Assert.Single(stats.Songs).Name);
    }

    static ListeningObservation Observation(Guid stationId) => new(stationId,
        stationId == Cruise ? "Cruise" : "Combat", Song, "Fortunate Son", Source, "CCR source",
        IsPlaying: true, IsActiveStation: true, EffectiveGain: .7);
}
