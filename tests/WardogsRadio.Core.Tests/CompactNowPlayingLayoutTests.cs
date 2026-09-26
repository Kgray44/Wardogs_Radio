using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class CompactNowPlayingLayoutTests
{
    [Theory]
    [InlineData(1215)]
    [InlineData(1066)]
    [InlineData(900)]
    [InlineData(760)]
    public void Compact_zones_are_ordered_and_do_not_compete(double width)
    {
        var layout = CompactNowPlayingLayout.Create(width);

        Assert.True(layout.StationLeft + layout.StationWidth <= layout.TrackLeft);
        Assert.True(layout.TrackLeft + layout.TrackWidth < layout.VisualizerLeft);
        Assert.True(layout.VisualizerLeft + layout.VisualizerWidth < layout.TransportLeft);
        Assert.True(layout.TransportLeft + layout.TransportWidth < layout.TimelineLeft);
        Assert.True(layout.TimelineLeft + layout.TimelineWidth <= width);
        Assert.True(layout.VisualizerWidth >= 84);
        Assert.True(layout.TransportWidth >= 124);
    }

    [Fact]
    public void Narrow_compact_layout_hides_secondary_metadata_before_primary_controls()
    {
        Assert.False(CompactNowPlayingLayout.Create(900).ShowSecondaryMetadata);
        Assert.True(CompactNowPlayingLayout.Create(1066).ShowSecondaryMetadata);
    }
}
