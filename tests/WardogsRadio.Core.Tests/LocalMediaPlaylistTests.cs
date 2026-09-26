using WardogsRadio.Playback;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class LocalMediaPlaylistTests
{
    [Fact]
    public void EnumeratesSupportedAudioAndPlaylistFilesInStableOrder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-playlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        try
        {
            File.WriteAllText(Path.Combine(folder, "b.FLAC"), "");
            File.WriteAllText(Path.Combine(folder, "nested", "a.mp3"), "");
            File.WriteAllText(Path.Combine(folder, "nested", "c.m3u8"), "");
            File.WriteAllText(Path.Combine(folder, "notes.txt"), "");
            var files = LocalMediaPlaylist.FromDirectory(folder, false);
            Assert.Equal(3, files.Count);
            Assert.Equal(files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase), files);
            Assert.DoesNotContain(files, x => x.EndsWith("notes.txt"));
            Assert.Equal(files, LocalMediaPlaylist.FromDirectory(folder, false));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void RejectsEmptyMusicFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-playlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { Assert.Throws<InvalidOperationException>(() => LocalMediaPlaylist.FromDirectory(folder, false)); }
        finally { Directory.Delete(folder); }
    }

    [Fact]
    public void ShufflePreservesTrackSet()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-playlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            for (var i = 0; i < 12; i++) File.WriteAllText(Path.Combine(folder, $"track{i:00}.mp3"), "");
            var ordered = LocalMediaPlaylist.FromDirectory(folder, false);
            var shuffled = LocalMediaPlaylist.FromDirectory(folder, true, new Random(13));
            Assert.Equal(ordered.OrderBy(x => x), shuffled.OrderBy(x => x));
            Assert.False(ordered.SequenceEqual(shuffled));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ExplicitPlaylistKeepsChosenOrderAndCanRepeatATrack()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-playlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var a = Path.Combine(folder, "a.mp3");
            var b = Path.Combine(folder, "b.flac");
            var c = Path.Combine(folder, "c.m3u8");
            File.WriteAllText(a, "");
            File.WriteAllText(b, "");
            File.WriteAllText(c, "#EXTM3U");
            Assert.Equal([b, a, b, c], LocalMediaPlaylist.FromFiles([b, a, b, c], false));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ExplicitPlaylistReshuffleChangesOrderWithoutChangingTracks()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-playlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var files = Enumerable.Range(0, 12).Select(i => Path.Combine(folder, $"track{i:00}.mp3")).ToArray();
            foreach (var file in files) File.WriteAllText(file, "");
            var first = LocalMediaPlaylist.FromFiles(files, true, new Random(13));
            var again = LocalMediaPlaylist.FromFiles(files, true, new Random(19));
            Assert.False(first.SequenceEqual(again));
            Assert.Equal(first.OrderBy(x => x), again.OrderBy(x => x));
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public void ExplicitPlaylistRejectsMissingOrUnsupportedTracks()
    {
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-playlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var textFile = Path.Combine(folder, "notes.txt");
            File.WriteAllText(textFile, "");
            Assert.Throws<InvalidOperationException>(() => LocalMediaPlaylist.FromFiles([textFile], false));
            Assert.Throws<FileNotFoundException>(() => LocalMediaPlaylist.FromFiles([Path.Combine(folder, "missing.mp3")], false));
        }
        finally { Directory.Delete(folder, true); }
    }
}
