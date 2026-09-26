using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class StationEditPlannerTests
{
    [Fact]
    public void ControlAndIdentityEditsDoNotReloadTheActiveSong()
    {
        var first = "C:\\Music\\one.mp3";
        var second = "C:\\Music\\two.mp3";
        var station = new Station { ProviderId = "mpv", Source = second, PlaylistFiles = [second, first],
            Name = "Renamed", Shuffle = true, ShuffleSeed = 321, Volume = .4, GameVolume = .2,
            RepeatMode = StationRepeatMode.Track };
        Assert.False(StationEditPlanner.RequiresPlaybackReload("mpv", first, [first, second], station));
    }

    [Fact]
    public void FileOrOnlineSourceChangesRequireReload()
    {
        var first = "C:\\Music\\one.mp3";
        var second = "C:\\Music\\two.mp3";
        var third = "C:\\Music\\three.mp3";
        var station = new Station { ProviderId = "mpv", Source = first, PlaylistFiles = [first, second, third] };
        Assert.True(StationEditPlanner.RequiresPlaybackReload("mpv", first, [first, second], station));
        station.PlaylistFiles = [];
        station.Source = "https://radio.example/new";
        Assert.True(StationEditPlanner.RequiresPlaybackReload("mpv", "https://radio.example/old", [], station));
        Assert.True(StationEditPlanner.RequiresPlaybackReload("youtube", station.Source, [], station));
    }
}
