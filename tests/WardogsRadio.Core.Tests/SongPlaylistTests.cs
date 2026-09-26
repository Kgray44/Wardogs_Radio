using WardogsRadio.Core;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class SongPlaylistTests
{
    [Fact]
    public void SplitNamesFollowingSegmentAndKeepsOriginalMedia()
    {
        var songs = new List<StationSong> { new() { Source = "video-1", Name = "Long mix" } };
        var named = SongPlaylist.Split(songs, songs[0].Id, 120, 600, "Fortunate Son", true);
        Assert.Equal(2, songs.Count);
        Assert.Equal(120, songs[0].EndSeconds);
        Assert.Equal(120, named.StartSeconds);
        Assert.Null(named.EndSeconds);
        Assert.All(songs, song => Assert.Equal("video-1", song.Source));
    }

    [Fact]
    public void SplitNamesPrecedingSegmentInsideExistingRange()
    {
        var original = new StationSong { Source = "file.mp3", Name = "Remainder", StartSeconds = 60, EndSeconds = 180 };
        var songs = new List<StationSong> { original };
        var named = SongPlaylist.Split(songs, original.Id, 90, 300, "First verse", false);
        Assert.Same(named, songs[0]);
        Assert.Equal(60, named.StartSeconds);
        Assert.Equal(90, named.EndSeconds);
        Assert.Equal(90, original.StartSeconds);
        Assert.Equal(180, original.EndSeconds);
    }

    [Fact]
    public void MoveAndShuffleChangePhysicalOrderWithoutChangingRanges()
    {
        var songs = Enumerable.Range(0, 4).Select(i => new StationSong { Source = "same", Name = i.ToString(), StartSeconds = i * 30, EndSeconds = (i + 1) * 30 }).ToList();
        SongPlaylist.Move(songs, 0, 3);
        Assert.Equal("0", songs[3].Name);
        var order = songs.Select(x => x.Id).ToArray();
        SongPlaylist.Shuffle(songs, new Random(8));
        Assert.False(order.SequenceEqual(songs.Select(x => x.Id)));
        Assert.Equal(4, songs.Select(x => x.Id).Distinct().Count());
        Assert.Equal(2, SongPlaylist.FindCurrent(songs, "same", 75) switch { var index when index >= 0 => (int)(songs[index].StartSeconds / 30), _ => -1 });
    }

    [Fact]
    public void EditChangesOnlyChosenSongMetadataAndKeepsSource()
    {
        var songs = new List<StationSong>
        {
            new() { Source = "long.mp3", Name = "One", EndSeconds = 120 },
            new() { Source = "long.mp3", Name = "Two", StartSeconds = 120, EndSeconds = 240 }
        };
        var first = songs[0];
        var second = songs[1];
        SongPlaylist.Update(songs, second.Id, "Edited", 123.5, 238, 300);
        Assert.Same(first, songs[0]);
        Assert.Equal(120, first.EndSeconds);
        Assert.Equal("long.mp3", second.Source);
        Assert.Equal("Edited", second.Name);
        Assert.Equal(123.5, second.StartSeconds);
        Assert.Equal(238, second.EndSeconds);
    }

    [Fact]
    public void EditRejectsBadRangesWithoutChangingSong()
    {
        var song = new StationSong { Source = "video", Name = "Original", StartSeconds = 20, EndSeconds = 40 };
        var songs = new List<StationSong> { song };
        Assert.Throws<InvalidOperationException>(() => SongPlaylist.Update(songs, song.Id, "Changed", 35, 35, 100));
        Assert.Throws<InvalidOperationException>(() => SongPlaylist.Update(songs, song.Id, "Changed", 35, 101, 100));
        Assert.Equal("Original", song.Name);
        Assert.Equal(20, song.StartSeconds);
        Assert.Equal(40, song.EndSeconds);
    }

    [Theory]
    [InlineData("06:19", 379)]
    [InlineData("1:02:03.5", 3723.5)]
    [InlineData("90.25", 90.25)]
    public void SongTimeParserUsesMinutesAndSeconds(string text, double expected)
    {
        Assert.True(SongPlaylist.TryParseTime(text, out var seconds));
        Assert.Equal(expected, seconds);
        Assert.False(SongPlaylist.TryParseTime("1:99", out _));
    }

    [Fact]
    public async Task NamedRangesAndPhysicalOrderSurviveConfigurationReload()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-song-config-" + Guid.NewGuid().ToString("N"));
        try
        {
            var station = new Station { Name = "Mix", PlaylistSongs =
            [
                new() { Source = "video-1", Name = "Second", StartSeconds = 90, EndSeconds = 170 },
                new() { Source = "video-1", Name = "First", StartSeconds = 0, EndSeconds = 90 }
            ] };
            var config = new AppConfiguration { Profile = new RadioProfile { Stations = [station] } };
            var store = new ConfigurationStore(folder);
            await store.SaveAsync(config);
            var restored = Assert.Single((await store.LoadAsync()).Profile.Stations);
            Assert.Equal(["Second", "First"], restored.PlaylistSongs.Select(x => x.Name));
            Assert.Equal(90, restored.PlaylistSongs[0].StartSeconds);
            Assert.Equal(170, restored.PlaylistSongs[0].EndSeconds);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
