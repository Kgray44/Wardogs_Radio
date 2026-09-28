using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class OutputTelemetryConfidenceTests
{
    [Fact]
    public void BriefDisagreementPausesProtectionButDoesNotFlipOverallReadiness()
    {
        var tracker = new OutputTelemetryReadinessTracker();
        var start = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var conflict = OutputTelemetryAssessor.Assess(true, .1f, true, 0);
        Assert.False(conflict.CanControlClipGuard);

        tracker.Observe(conflict.Confidence, start);
        tracker.Observe(conflict.Confidence, start.AddMilliseconds(200));
        Assert.False(tracker.HasSustainedConflict);

        tracker.Observe(conflict.Confidence, start.AddMilliseconds(300));
        Assert.True(tracker.HasSustainedConflict);

        tracker.Observe(OutputTelemetryConfidence.Verified, start.AddMilliseconds(340));
        Assert.False(tracker.HasSustainedConflict);
    }

    [Fact]
    public void MatchingLiveRemoteAndWindowsB1IsVerified()
    {
        var result = OutputTelemetryAssessor.Assess(true, .12f, true, .08f);
        Assert.Equal(OutputTelemetryConfidence.Verified, result.Confidence);
        Assert.True(result.CanControlClipGuard);
    }

    [Fact]
    public void LiveWindowsB1AndQuietRemoteB1IsConflictingAndCannotControlProtection()
    {
        var result = OutputTelemetryAssessor.Assess(true, .0007f, true, .12f);
        Assert.Equal(OutputTelemetryConfidence.Conflicting, result.Confidence);
        Assert.False(result.CanControlClipGuard);
    }

    [Fact]
    public void MissingOneObservationIsPartial()
    {
        var result = OutputTelemetryAssessor.Assess(true, .1f, false, 0);
        Assert.Equal(OutputTelemetryConfidence.Partial, result.Confidence);
    }
}
