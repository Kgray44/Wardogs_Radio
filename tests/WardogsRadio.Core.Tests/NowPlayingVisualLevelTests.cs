using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class NowPlayingVisualLevelTests
{
    [Theory]
    [InlineData(0f)]
    [InlineData(.9f)]
    public void DirectListeningAlwaysFollowsHeadphonesInsteadOfIndependentGameFeed(float musicPeak)
    {
        var result = NowPlayingVisualLevelResolver.Resolve(PlaybackListeningTopology.DirectEndpoint, true, false,
            new(true, musicPeak), new(true, .32f));
        Assert.Equal(VisualLevelSource.ListeningEndpoint, result.Source);
        Assert.Equal(.32f, result.Peak);
    }

    [Fact]
    public void DirectPathDoesNotFallBackToGameFeedWhenHeadphonesMeterIsUnavailable() =>
        Assert.Equal(0, NowPlayingVisualLevelResolver.Resolve(PlaybackListeningTopology.DirectEndpoint, true, false,
            new(true, .6f), new(false, 0)).Peak);

    [Fact]
    public void VerifiedMixerPathCanUseActiveStripWhenEndpointMeterIsUnavailable() =>
        Assert.Equal(.3f, NowPlayingVisualLevelResolver.Resolve(PlaybackListeningTopology.Mixer, true, false,
            new(true, .3f), new(false, 0)).Peak);

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void PausedAndLoadingPathsReturnToZero(bool playing, bool switching) =>
        Assert.Equal(0, NowPlayingVisualLevelResolver.Resolve(PlaybackListeningTopology.DirectEndpoint, playing, switching,
            new(true, 1), new(true, 1)).Peak);

    [Fact]
    public void UnavailableAndInvalidSignalsReturnZero()
    {
        Assert.Equal(0, NowPlayingVisualLevelResolver.Resolve(PlaybackListeningTopology.Mixer, true, false,
            new(false, 0), new(false, 0)).Peak);
        Assert.Equal(0, NowPlayingVisualLevelResolver.Resolve(PlaybackListeningTopology.DirectEndpoint, true, false,
            new(true, 1), new(true, float.NaN)).Peak);
        Assert.Equal(0, NowPlayingVisualLevelResolver.Resolve(PlaybackListeningTopology.Unavailable, true, false,
            new(true, 1), new(true, 1)).Peak);
    }

    [Fact]
    public void BarsAttackQuicklyThenDecayAndReduceMotionStillShowsSignal()
    {
        var high = NowPlayingVisualLevelResolver.BarHeight(0, 1, 3, false);
        Assert.True(high > 3);
        var low = NowPlayingVisualLevelResolver.BarHeight(0, 0, high, false);
        Assert.True(low < high);
        for (var frame = 0; frame < 50; frame++) low = NowPlayingVisualLevelResolver.BarHeight(0, 0, low, false);
        Assert.Equal(3, low);
        Assert.True(NowPlayingVisualLevelResolver.BarHeight(0, .32, 3, true) > 3);
        Assert.Equal(3, NowPlayingVisualLevelResolver.BarHeight(0, 0, high, true));
    }
}
