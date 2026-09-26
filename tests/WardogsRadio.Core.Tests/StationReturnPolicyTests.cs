using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class StationReturnPolicyTests
{
    [Theory]
    [InlineData(PlaybackMode.Player, 62, 62)]
    [InlineData(PlaybackMode.Radio, 62, 62)]
    [InlineData(PlaybackMode.RestartTrack, 62, 0)]
    public void ReturningToStationUsesChosenStartPoint(PlaybackMode mode, double saved, double expected) =>
        Assert.Equal(expected, StationReturnPolicy.StartingSeconds(mode, saved));
}
