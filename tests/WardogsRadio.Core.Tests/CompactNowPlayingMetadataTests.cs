using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class CompactNowPlayingMetadataTests
{
    [Fact]
    public void Artist_metadata_keeps_the_channel_context()
    {
        var station = new Station { Order = 4, ProviderId = "youtube" };

        Assert.Equal("Walk Off The Earth · CH 05", CompactNowPlayingMetadata.Format(station, "Walk Off The Earth"));
    }

    [Fact]
    public void Missing_artist_uses_provider_not_the_underlying_url()
    {
        var station = new Station { Order = 4, ProviderId = "youtube", Source = "https://www.youtube.com/watch?v=example" };

        var metadata = CompactNowPlayingMetadata.Format(station, null);
        Assert.Equal("CH 05 · YOUTUBE", metadata);
        Assert.DoesNotContain("http", metadata, StringComparison.OrdinalIgnoreCase);
    }
}
