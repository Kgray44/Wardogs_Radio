using System.Diagnostics;
using System.Text;
using WardogsRadio.Core;
using WardogsRadio.Playback;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class MpvPlaylistIntegrationTests
{
    [Fact]
    public async Task OneLocalFileCanLoadAsMultipleNamedSongRangesInSavedOrder()
    {
        if (new MpvLocator().Find(null) is null) return;
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-mpv-cues-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "mix.wav");
            WriteSilentWave(file, 8);
            var station = new Station { Source = file, PlaylistFiles = [file, file, file], PlaylistSongs =
            [
                new() { Source = file, Name = "Middle", StartSeconds = 2, EndSeconds = 5 },
                new() { Source = file, Name = "Intro", StartSeconds = 0, EndSeconds = 2 },
                new() { Source = file, Name = "Ending", StartSeconds = 5 }
            ] };
            await using var player = new MpvProvider(new MpvLocator());
            await player.LoadAsync(station);
            Assert.Equal(3, player.LoadedFiles.Count);
            Assert.All(player.LoadedFiles, loaded => Assert.Equal(file, loaded));
            await player.SelectTrackAsync(2, station.PlaylistSongs[2].StartSeconds);
            Assert.Equal(2, player.CurrentPlaylistIndex);
            Assert.InRange((await player.RefreshAsync()).PositionSeconds, 4.8, 5.2);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task TwoNativePlayersKeepIndependentGainsAndTrackPositions()
    {
        if (new MpvLocator().Find(null) is null) return;
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-mpv-dual-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var first = Path.Combine(folder, "first.wav");
            var second = Path.Combine(folder, "second.wav");
            WriteSilentWave(first, 10);
            WriteSilentWave(second, 10);
            var station = new Station { PlaylistFiles = [first, second], Shuffle = true, ShuffleSeed = 408 };
            await using var headset = new MpvProvider(new MpvLocator());
            await using var game = new MpvProvider(new MpvLocator());
            await headset.LoadAsync(station);
            await game.LoadAsync(station);
            Assert.Equal(headset.LoadedFiles, game.LoadedFiles);
            await headset.SetVolumeAsync(.52);
            await game.SetVolumeAsync(.17);
            Assert.InRange(await headset.ReadVolumeAsync(), .519, .521);
            Assert.InRange(await game.ReadVolumeAsync(), .169, .171);
            await headset.SelectTrackAsync(1, 4);
            await game.SelectTrackAsync(headset.CurrentPlaylistIndex, (await headset.RefreshAsync()).PositionSeconds);
            Assert.Equal(headset.CurrentPlaylistIndex, game.CurrentPlaylistIndex);
            Assert.InRange((await game.RefreshAsync()).PositionSeconds, 3.8, 4.2);
            await headset.SetVolumeAsync(.78);
            Assert.InRange(await game.ReadVolumeAsync(), .169, .171);
            await game.SetVolumeAsync(0);
            Assert.InRange(await game.ReadVolumeAsync(), 0, .0001);
            Assert.True(await game.ReadMuteAsync());
            Assert.False(await headset.ReadMuteAsync());
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task OptionalOwnerMp3CanReportDurationAndResume()
    {
        var file = Environment.GetEnvironmentVariable("WARDOGS_TEST_MEDIA");
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file) || new MpvLocator().Find(null) is null) return;
        await using var provider = new MpvProvider(new MpvLocator());
        await provider.LoadAsync(new Station { Source = file });
        PlaybackSnapshot snapshot = provider.Snapshot;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            snapshot = await provider.RefreshAsync();
            if (snapshot.DurationSeconds is > 0) break;
            await Task.Delay(50);
        }
        Assert.True(snapshot.DurationSeconds is > 10, "The owner MP3 did not report a usable duration.");
        await provider.SelectTrackAsync(0, 6);
        snapshot = await provider.RefreshAsync();
        Assert.InRange(snapshot.PositionSeconds, 5.8, 6.2);
        Assert.False(snapshot.IsPlaying);
    }

    [Fact]
    public async Task RepeatOffReportsStoppedAtPlaylistEndAndCanRestart()
    {
        if (new MpvLocator().Find(null) is null) return;
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-mpv-repeat-off-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "short.wav");
            WriteSilentWave(file);
            await using var provider = new MpvProvider(new MpvLocator());
            await provider.LoadAsync(new Station { Source = file, Loop = false, RepeatMode = StationRepeatMode.Off });
            await provider.PlayAsync();
            var stopped = false;
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(100);
                if (!(await provider.RefreshAsync()).IsPlaying) { stopped = true; break; }
            }
            Assert.True(stopped, "mpv still reported playing after a non-repeating one-track playlist ended.");
            await provider.PlayAsync();
            Assert.True((await provider.RefreshAsync()).IsPlaying);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task MpvAppliesLiveGainAndRestoresPlaylistTrack()
    {
        if (new MpvLocator().Find(null) is null) return;
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-mpv-retune-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var first = Path.Combine(folder, "first.wav");
            var second = Path.Combine(folder, "second.wav");
            WriteSilentWave(first, 10);
            WriteSilentWave(second, 10);
            await using var provider = new MpvProvider(new MpvLocator());
            await provider.LoadAsync(new Station { Source = first, PlaylistFiles = [first, second] });
            await provider.SetVolumeAsync(.25);
            Assert.InRange(await provider.ReadVolumeAsync(), .249, .251);
            await provider.SetVolumeAsync(.65);
            Assert.InRange(await provider.ReadVolumeAsync(), .649, .651);
            await provider.SelectTrackAsync(1, 6);
            var snapshot = await provider.RefreshAsync();
            Assert.Equal(1, provider.CurrentPlaylistIndex);
            Assert.Contains("second", snapshot.Track?.Title, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(snapshot.PositionSeconds, 5.8, 6.2);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task InstalledMpvExposesRealAudioOutputChoices()
    {
        if (new MpvLocator().Find(null) is null) return;
        await using var provider = new MpvProvider(new MpvLocator());
        var devices = await provider.ListAudioDevicesAsync();
        Assert.Contains(devices, x => x.Name == "auto");
        Assert.All(devices, x => Assert.False(string.IsNullOrWhiteSpace(x.Description)));
        await provider.SetAudioDeviceAsync("auto");
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SetAudioDeviceAsync("not-a-real-device"));
    }

    [Fact]
    public async Task FirstPlayUsesStartupRampButResumingDoesNot()
    {
        if (new MpvLocator().Find(null) is null) return;
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-mpv-startup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "silent.wav");
            WriteSilentWave(file, 3);
            await using var provider = new MpvProvider(new MpvLocator());
            await provider.LoadAsync(new Station { Source = file });
            await provider.SetVolumeAsync(.5);
            var watch = Stopwatch.StartNew();
            await provider.PlayAsync();
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds >= 300, $"Startup ramp lasted only {watch.ElapsedMilliseconds} ms.");
            Assert.True((await provider.RefreshAsync()).IsPlaying);
            await provider.PauseAsync();
            watch.Restart();
            await provider.PlayAsync();
            watch.Stop();
            Assert.True(watch.ElapsedMilliseconds < 300, $"Resume unexpectedly replayed the ramp for {watch.ElapsedMilliseconds} ms.");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task ExplicitPlaylistLoadsTracksInChosenOrderWhenMpvIsInstalled()
    {
        if (new MpvLocator().Find(null) is null) return;
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-mpv-playlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var first = Path.Combine(folder, "first.wav");
            var second = Path.Combine(folder, "second.wav");
            WriteSilentWave(first);
            WriteSilentWave(second);
            await using var provider = new MpvProvider(new MpvLocator());
            await provider.LoadAsync(new Station { Source = first, PlaylistFiles = [first, second], Loop = false });
            Assert.Contains("first", provider.Snapshot.Track?.Title, StringComparison.OrdinalIgnoreCase);
            await provider.NextAsync();
            for (var attempt = 0; attempt < 20; attempt++)
            {
                var snapshot = await provider.RefreshAsync();
                if (snapshot.Track?.Title.Contains("second", StringComparison.OrdinalIgnoreCase) == true)
                {
                    await provider.SetRepeatModeAsync(StationRepeatMode.Track);
                    await provider.SetRepeatModeAsync(StationRepeatMode.Off);
                    await provider.SetRepeatModeAsync(StationRepeatMode.Playlist);
                    return;
                }
                await Task.Delay(100);
            }
            Assert.Fail("mpv did not advance to the second playlist track.");
        }
        finally { Directory.Delete(folder, true); }
    }

    [Fact]
    public async Task PreviousRestartsAfterFiveSecondsAndSkipsBeforeFiveSecondsWhenMpvIsInstalled()
    {
        if (new MpvLocator().Find(null) is null) return;
        var folder = Path.Combine(Path.GetTempPath(), "wardogs-mpv-previous-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var first = Path.Combine(folder, "first.wav");
            var second = Path.Combine(folder, "second.wav");
            WriteSilentWave(first, 10);
            WriteSilentWave(second, 10);
            await using var provider = new MpvProvider(new MpvLocator());
            await provider.LoadAsync(new Station { Source = first, PlaylistFiles = [first, second], Loop = false });
            await provider.NextAsync();
            for (var attempt = 0; attempt < 20 && (await provider.RefreshAsync()).Track?.Title.Contains("second", StringComparison.OrdinalIgnoreCase) != true; attempt++)
                await Task.Delay(50);
            Assert.Contains("second", (await provider.RefreshAsync()).Track?.Title, StringComparison.OrdinalIgnoreCase);
            await provider.SeekAsync(6);
            for (var attempt = 0; attempt < 20 && (await provider.RefreshAsync()).PositionSeconds <= 5; attempt++)
                await Task.Delay(50);
            Assert.True((await provider.RefreshAsync()).PositionSeconds > 5);
            await provider.PreviousAsync();
            var restarted = await provider.RefreshAsync();
            Assert.Contains("second", restarted.Track?.Title, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(restarted.PositionSeconds, 0, 1);
            await provider.PreviousAsync();
            var skipped = await provider.RefreshAsync();
            Assert.Contains("first", skipped.Track?.Title, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(folder, true); }
    }

    static void WriteSilentWave(string path, int seconds = 1)
    {
        const int sampleRate = 8000;
        var dataBytes = sampleRate * 2 * seconds;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
    }
}
