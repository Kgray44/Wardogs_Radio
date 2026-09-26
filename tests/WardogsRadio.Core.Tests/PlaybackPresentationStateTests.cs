using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class PlaybackPresentationStateTests
{
    [Fact]
    public void LoadingYoutubeDoesNotExposeSavedRuntimeAsLiveTimeline()
    {
        var station = new Station { ProviderId = "youtube" };
        var state = PlaybackPresentationState.Create(station, null, ProviderHealth.Warning,
            PlaybackPresentationKind.Loading, false, false, 143, 3576);

        Assert.Null(state.LogicalSongPosition);
        Assert.Null(state.LogicalSongDuration);
        Assert.False(state.CanSeek);
    }

    [Fact]
    public void ReadySegmentUsesOneLogicalTimeline()
    {
        var state = PlaybackPresentationState.Create(new Station(), new StationSong { StartSeconds = 100, EndSeconds = 200 },
            ProviderHealth.Ready, PlaybackPresentationKind.Playing, true, true, 125, 600);

        Assert.Equal(25, state.LogicalSongPosition);
        Assert.Equal(100, state.LogicalSongDuration);
        Assert.True(state.CanSeek);
    }
}
