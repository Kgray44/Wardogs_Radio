using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class StationSourceSelectionTests
{
    [Fact]
    public void ChoosingLocalConvertsAnyProviderToLocalMpv()
    {
        Assert.Equal(StationSourceSelection.LocalMpv, StationSourceSelection.ForSourceType(true, "youtube"));
        Assert.Equal("mpv", StationSourceSelection.ProviderId(StationSourceSelection.LocalMpv));
    }

    [Fact]
    public void ChoosingOnlineFromLocalSelectsInternetRadioWithoutChangingOtherOnlineProviders()
    {
        Assert.Equal(StationSourceSelection.LinkMpv, StationSourceSelection.ForSourceType(false, StationSourceSelection.LocalMpv));
        Assert.Equal("youtube", StationSourceSelection.ForSourceType(false, "youtube"));
        Assert.Equal("mpv", StationSourceSelection.ProviderId(StationSourceSelection.LinkMpv));
    }

    [Fact]
    public void ExistingMpvStationOpensInCorrectSourceView()
    {
        Assert.Equal(StationSourceSelection.LocalMpv, StationSourceSelection.ForStation("mpv", true));
        Assert.Equal(StationSourceSelection.LinkMpv, StationSourceSelection.ForStation("mpv", false));
        Assert.Equal("soundcloud", StationSourceSelection.ForStation("soundcloud", false));
    }
}
