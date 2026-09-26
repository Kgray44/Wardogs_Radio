using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class NowPlayingTimelineTests
{
    [Fact]
    public void Segment_cursor_is_logical_not_source_relative()
    {
        var timeline = NowPlayingTimeline.FromSource(857, 3822, 754, 941);
        Assert.Equal(103, timeline.PositionSeconds, 3);
        Assert.Equal(187, timeline.DurationSeconds!.Value, 3);
    }

    [Fact]
    public void Seek_is_translated_and_clamped_to_segment()
    {
        Assert.Equal(857, NowPlayingTimeline.ToSourcePosition(103, 754, 941), 3);
        Assert.Equal(941, NowPlayingTimeline.ToSourcePosition(999, 754, 941), 3);
        Assert.Equal(754, NowPlayingTimeline.ToSourcePosition(-1, 754, 941), 3);
    }

    [Fact]
    public void Open_ended_song_uses_remaining_source_duration()
    {
        var timeline = NowPlayingTimeline.FromSource(82, 300, 40);
        Assert.True(timeline.CanSeek);
        Assert.Equal(42, timeline.PositionSeconds, 3);
        Assert.Equal(260, timeline.DurationSeconds!.Value, 3);
    }

    [Fact]
    public void Unknown_source_duration_keeps_timeline_non_seekable()
    {
        var timeline = NowPlayingTimeline.FromSource(82, 0, 40);
        Assert.False(timeline.CanSeek);
        Assert.Equal(42, timeline.PositionSeconds, 3);
        Assert.Null(timeline.DurationSeconds);
    }
}
