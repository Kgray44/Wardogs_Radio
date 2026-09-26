using WardogsRadio.Voicemeeter;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class VoicemeeterSignalMonitorTests
{
    sealed class Remote : IVoicemeeterRemote
    {
        public Dictionary<(int Type, int Channel), float> Levels { get; } = [];
        public VoicemeeterStatus Probe() => new(true, true, "Banana", null, null, "connected") { Connected = true };
        public bool TryLogin(out string detail) { detail = "connected"; return true; }
        public bool TryGetLevel(int type, int channel, out float value) => Levels.TryGetValue((type, channel), out value);
        public bool TryGetParameterFloat(string name, out float value) { value = 0; return false; }
        public bool TrySetParameterFloat(string name, float value) => false;
        public void Dispose() { }
    }

    [Theory]
    [InlineData("Standard", 2, 4, 8)]
    [InlineData("Banana", 0, 0, 2)]
    [InlineData("Banana", 3, 6, 8)]
    [InlineData("Banana", 4, 14, 8)]
    [InlineData("Potato", 5, 10, 8)]
    public void StripMappingMatchesOfficialRemoteApi(string edition, int strip, int first, int count)
    {
        Assert.True(VoicemeeterSignalMonitor.StripChannels(edition, strip, out var actualFirst, out var actualCount));
        Assert.Equal(first, actualFirst);
        Assert.Equal(count, actualCount);
    }

    [Theory]
    [InlineData("Standard", "B1", 8)]
    [InlineData("Banana", "A1", 0)]
    [InlineData("Banana", "B1", 24)]
    [InlineData("Potato", "B1", 40)]
    public void BusMappingMatchesOfficialRemoteApi(string edition, string bus, int first)
    {
        Assert.True(VoicemeeterSignalMonitor.BusChannels(edition, bus, out var actual));
        Assert.Equal(first, actual);
    }

    [Fact]
    public void ReadsActualStereoBusPeakAndDoesNotInventMissingMeter()
    {
        var remote = new Remote();
        var monitor = new VoicemeeterSignalMonitor(remote);
        remote.Levels[(3, 24)] = .1f;
        remote.Levels[(3, 25)] = .4f;
        Assert.Equal(.4f, monitor.ReadBus("Banana", "B1").Peak);
        Assert.True(VoicemeeterSignalMonitor.BarValue(monitor.ReadBus("Banana", "B1")) > 0);
        Assert.False(monitor.ReadStrip("Banana", 3).Available);
        Assert.Equal(0, VoicemeeterSignalMonitor.BarValue(monitor.ReadStrip("Banana", 3)));
    }

    [Fact]
    public void BarSuppressesTheSubSignalNoiseFloor()
    {
        var belowFloor = new SignalLevel(true, VoicemeeterSignalMonitor.VisualSignalFloor / 2);
        var audible = new SignalLevel(true, VoicemeeterSignalMonitor.VisualSignalFloor * 2);

        Assert.Equal(0, VoicemeeterSignalMonitor.BarValue(belowFloor));
        Assert.True(VoicemeeterSignalMonitor.BarValue(audible) > 0);
    }

    [Fact]
    public void InstalledVoicemeeterRemoteReturnsLiveBusSamplesWhenConnected()
    {
        using var remote = new VoicemeeterRemote();
        if (!remote.TryLogin(out _)) return;
        var edition = remote.Probe().Edition;
        if (edition is null) return;
        var monitor = new VoicemeeterSignalMonitor(remote);
        Assert.True(monitor.ReadBus(edition, "A1").Available);
        Assert.True(monitor.ReadBus(edition, "B1").Available);
    }
}
