using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class StationCyclePolicyTests
{
    [Fact]
    public void CyclesInPresetOrderAndWraps()
    {
        var last = new Station { Name = "Last", Order = 9 };
        var first = new Station { Name = "First", Order = 0 };
        var disabled = new Station { Name = "Disabled", Order = 1, Enabled = false };
        var stations = new[] { last, disabled, first };
        Assert.Same(first, StationCyclePolicy.Next(stations, null));
        Assert.Same(last, StationCyclePolicy.Next(stations, first.Id));
        Assert.Same(first, StationCyclePolicy.Next(stations, last.Id));
    }

    [Fact]
    public void HandlesMissingActiveAndNoEnabledStations()
    {
        var only = new Station { Order = 3 };
        Assert.Same(only, StationCyclePolicy.Next(new[] { only }, Guid.NewGuid()));
        only.Enabled = false;
        Assert.Null(StationCyclePolicy.Next(new[] { only }, null));
    }

    [Fact]
    public void CatalogExposesCycleWithoutFixedStationTarget()
    {
        var action = MacroActionCatalog.Get(ActionKind.CycleStations);
        Assert.Equal(MacroActionParameter.None, action.Parameter);
        Assert.False(MacroActionCatalog.RequiresStation(ActionKind.CycleStations));
    }

    [Fact]
    public void CycleIncludesYouTubeStationsInPresetOrder()
    {
        var local = new Station { Order = 0, ProviderId = "mpv" };
        var youtube = new Station { Order = 1, ProviderId = "youtube", Source = "https://www.youtube.com/watch?v=C7aG8gHDux4" };
        Assert.Same(youtube, StationCyclePolicy.Next([local, youtube], local.Id));
    }
}
