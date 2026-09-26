using WardogsRadio.Voicemeeter;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class VoicemeeterSharedOutputSelectorTests
{
    [Fact]
    public void FindsTruncatedSharedMmeNameAheadOfExactExclusiveWdmName()
    {
        const string endpoint = "Speakers (Razer Barracuda Pro 2.4)";
        var devices = new[]
        {
            new VoicemeeterAudioDevice(3, endpoint, "wdm"),
            new VoicemeeterAudioDevice(1, "Speakers (Razer Barracuda Pro 2", "mme")
        };

        Assert.Equal("mme", VoicemeeterSharedOutputSelector.Find(devices, endpoint)?.HardwareId);
    }

    [Fact]
    public void NeverFallsBackToExclusiveDevice()
    {
        const string endpoint = "Speakers (Razer Barracuda Pro 2.4)";
        var devices = new[] { new VoicemeeterAudioDevice(3, endpoint, "wdm") };

        Assert.Null(VoicemeeterSharedOutputSelector.Find(devices, endpoint));
    }

    [Fact]
    public void RejectsAmbiguousShortPrefix()
    {
        var devices = new[] { new VoicemeeterAudioDevice(1, "Speakers", "mme") };

        Assert.Null(VoicemeeterSharedOutputSelector.Find(devices, "Speakers (Razer Barracuda Pro 2.4)"));
    }
}
