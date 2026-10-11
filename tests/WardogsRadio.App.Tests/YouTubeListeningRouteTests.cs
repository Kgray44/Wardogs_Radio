using System.Diagnostics;
using System.IO;
using System.Text.Json;
using WardogsRadio.App;
using Xunit;

namespace WardogsRadio.App.Tests;

public sealed class YouTubeListeningRouteTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "wardogs-route-tests-" + Guid.NewGuid().ToString("N"));
    string RecoveryPath => Path.Combine(_root, "youtube-listening-policy-recovery.json");

    [Fact]
    public void ExitedProcessRecoveryReleasesLeaseAndRetainsJournal()
    {
        // A short-lived owned process gives a real exited PID, not a guessed one.
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0")
        { UseShellExecute = false, CreateNoWindow = true })!;
        var pid = process.Id;
        process.WaitForExit();
        var saved = Save((uint)pid, 1);
        var route = new YouTubeListeningRoute(_root);

        Assert.True(route.TryRecover(out var detail), detail);
        Assert.False(route.IsActive);
        Assert.False(File.Exists(RecoveryPath));
        Assert.Equal(saved, File.ReadAllText(Assert.Single(Directory.GetFiles(_root, "*.retired-*"))));
        Assert.True(route.TryRecover(out detail), detail);
    }

    [Fact]
    public void ReusedPidRecoveryDoesNotTouchLiveProcess()
    {
        using var process = Process.GetCurrentProcess();
        Save((uint)process.Id, process.StartTime.ToUniversalTime().Ticks - 1);
        var route = new YouTubeListeningRoute(_root);

        Assert.True(route.TryRecover(out var detail), detail);
        Assert.Contains("no other process was changed", detail);
        Assert.False(route.IsActive);
        Assert.Single(Directory.GetFiles(_root, "*.retired-*"));
    }

    [Fact]
    public void InvalidRecoveryRemainsBlockedAndPreserved()
    {
        var saved = Save(0, 0);
        var route = new YouTubeListeningRoute(_root);

        Assert.False(route.TryRecover(out var detail));
        Assert.Contains("Recovery record is invalid", detail);
        Assert.Equal(saved, File.ReadAllText(RecoveryPath));
        Assert.Empty(Directory.GetFiles(_root, "*.retired-*"));
    }

    [Fact]
    public void FailedLiveRestorationRetainsLeaseAndJournal()
    {
        using var process = Process.GetCurrentProcess();
        // The test runner has no owned audio policy. A different saved prior
        // output must not be written into the live runner during recovery.
        var saved = Save((uint)process.Id, process.StartTime.ToUniversalTime().Ticks);
        var route = new YouTubeListeningRoute(_root);

        Assert.False(route.TryRecover(out var detail));
        Assert.Contains("changed outside WARDOGS", detail);
        Assert.True(route.IsActive);
        Assert.Equal(saved, File.ReadAllText(RecoveryPath));
        Assert.Empty(Directory.GetFiles(_root, "*.retired-*"));
    }

    [Fact]
    public async Task ProcessExitAfterFailedRecoveryCanReleaseActiveLease()
    {
        using var process = Process.Start(new ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -Command Start-Sleep -Seconds 30")
        { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Save((uint)process.Id, process.StartTime.ToUniversalTime().Ticks);
            var route = new YouTubeListeningRoute(_root);
            Assert.False(route.TryRecover(out _));
            Assert.True(route.IsActive);
            process.Kill();
            await process.WaitForExitAsync();

            Assert.Contains("exited or restarted", Assert.Throws<InvalidOperationException>(() => route.SetVolume(.5)).Message);
            Assert.Contains("exited or restarted", (await Assert.ThrowsAsync<InvalidOperationException>(() => route.SwitchAsync("raw"))).Message);
            Assert.False(route.TryCheckHealth(out _));
            Assert.True(route.TryEnd(out var detail), detail);
            Assert.False(route.IsActive);
            Assert.Single(Directory.GetFiles(_root, "*.retired-*"));
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
        }
    }

    string Save(uint pid, long startTicks)
    {
        Directory.CreateDirectory(_root);
        var saved = JsonSerializer.Serialize(new
        {
            ProcessId = pid, ProcessStartTicks = startTicks,
            PriorConsole = "prior", PriorMultimedia = "prior",
            AppliedEndpoint = "applied", RawEndpoint = "raw",
            PriorVolume = .5f, AppliedVolume = .5f
        });
        File.WriteAllText(RecoveryPath, saved);
        return saved;
    }

    public void Dispose() => Directory.Delete(_root, true);
}
