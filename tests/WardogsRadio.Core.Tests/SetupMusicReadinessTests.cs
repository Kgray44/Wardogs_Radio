using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class SetupMusicReadinessTests
{
    [Fact]
    public void YoutubeCanBeReadyWithoutAnMpvPlayer()
    {
        var station = new Station { ProviderId = "youtube" };
        Assert.Empty(SetupMusicReadiness.Missing(station, false, true, true, true, false));
    }

    [Fact]
    public void YoutubeNeedsBothVisiblePlaybackAndItsGameFeed()
    {
        var station = new Station { ProviderId = "youtube" };
        var missing = SetupMusicReadiness.Missing(station, false, false, false, true, false);
        Assert.Equal(2, missing.Count);
        Assert.Contains(missing, item => item.Contains("YouTube player"));
        Assert.Contains(missing, item => item.Contains("game feed"));
    }

    [Fact]
    public void LocalMusicStillRequiresItsSeparateGamePlayerWhenSelected()
    {
        var station = new Station { ProviderId = "mpv" };
        Assert.Contains("connect game music player", SetupMusicReadiness.Missing(station, true, false, false, true, false));
        Assert.Empty(SetupMusicReadiness.Missing(station, true, false, false, true, true));
    }

    [Fact]
    public void ControlOnlyExternalAudioCannotClaimGameRouteReady()
    {
        var station = new Station { ProviderId = "external-audio" };
        Assert.NotEmpty(SetupMusicReadiness.Missing(station, false, false, false, true, false));
    }
}
