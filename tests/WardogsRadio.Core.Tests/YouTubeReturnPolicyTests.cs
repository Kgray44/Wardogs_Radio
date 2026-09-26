using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class YouTubeReturnPolicyTests
{
    [Theory]
    [InlineData(PlaybackMode.Player, 120, 300, 120)]
    [InlineData(PlaybackMode.Radio, 120, 300, 120)]
    [InlineData(PlaybackMode.RestartTrack, 120, 300, 0)]
    [InlineData(PlaybackMode.Player, 299, 300, 0)]
    [InlineData(PlaybackMode.Radio, 300, 300, 0)]
    [InlineData(PlaybackMode.Player, 120, 0, 120)]
    public void ResumesUnlessTrackAlreadyEnded(PlaybackMode mode, double saved, double duration, double expected) =>
        Assert.Equal(expected, YouTubeReturnPolicy.StartingSeconds(mode, saved, duration));
}
