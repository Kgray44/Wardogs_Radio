using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class OutputTelemetryConfidenceTests
{
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
