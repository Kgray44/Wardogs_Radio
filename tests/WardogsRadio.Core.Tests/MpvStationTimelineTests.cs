using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class MpvStationTimelineTests
{
    [Fact]
    public void RadioPlaylistAdvancesAcrossTracksAndWraps()
    {
        Assert.Equal(new MpvStationCursor(1, 5, false), MpvStationTimeline.Advance([10, 20], 0, 8, 7, StationRepeatMode.Playlist));
        Assert.Equal(new MpvStationCursor(0, 3, false), MpvStationTimeline.Advance([10, 20], 1, 18, 5, StationRepeatMode.Playlist));
        Assert.Equal(new MpvStationCursor(1, 5, false), MpvStationTimeline.Advance([10, 20], 0, 8, 67, StationRepeatMode.Playlist));
    }

    [Fact]
    public void RepeatTrackNeverMovesToAnotherSong()
    {
        Assert.Equal(new MpvStationCursor(1, 3, false), MpvStationTimeline.Advance([10, 20], 1, 18, 25, StationRepeatMode.Track));
    }

    [Fact]
    public void RepeatOffEndsAfterFinalSong()
    {
        Assert.Equal(new MpvStationCursor(1, 20, true), MpvStationTimeline.Advance([10, 20], 0, 8, 40, StationRepeatMode.Off));
    }

    [Fact]
    public void RejectsUnknownDurationInsteadOfGuessing()
    {
        Assert.Throws<ArgumentException>(() => MpvStationTimeline.Advance([10, 0], 0, 1, 15, StationRepeatMode.Playlist));
    }
}
