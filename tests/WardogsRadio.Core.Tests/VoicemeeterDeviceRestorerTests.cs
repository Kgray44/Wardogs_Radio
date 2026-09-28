using WardogsRadio.Voicemeeter;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class VoicemeeterDeviceRestorerTests
{
    sealed class Remote : IVoicemeeterRemote
    {
        public readonly List<string> Attempts = [];
        public string Device = "Headphones";
        public string SuccessDriver = "wdm";
        public VoicemeeterStatus Probe() => new(true, true, "Banana", null, null, "fake") { Connected = true };
        public bool TryLogin(out string detail) { detail = "fake"; return true; }
        public bool TryGetLevel(int type, int channel, out float value) { value = 0; return false; }
        public bool TryGetParameterFloat(string name, out float value) { value = 0; return false; }
        public bool TrySetParameterFloat(string name, float value) => false;
        public bool TryGetParameterString(string name, out string value) { value = Device; return true; }
        public bool TrySetParameterString(string name, string value)
        {
            Attempts.Add(name);
            if (name.EndsWith(SuccessDriver, StringComparison.Ordinal)) { Device = value; return true; }
            return false;
        }
        public void Dispose() { }
    }

    [Fact]
    public void EmptyA1RestorationTriesNextDriverAndVerifiesReadback()
    {
        var remote = new Remote();
        Assert.True(VoicemeeterDeviceRestorer.TryClearA1(remote));
        Assert.Equal(["Bus[0].device.mme", "Bus[0].device.wdm"], remote.Attempts);
        Assert.Equal("", remote.Device);
    }

    [Fact]
    public void UnconfirmedClearIsFailure()
    {
        var remote = new Remote { SuccessDriver = "none" };
        Assert.False(VoicemeeterDeviceRestorer.TryClearA1(remote));
        Assert.Equal(3, remote.Attempts.Count);
    }
}
