using WardogsRadio.Voicemeeter;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class VoicemeeterDeviceIdentityTests
{
    [Theory]
    [InlineData("Microphone (USB Audio Device)", 3)]
    [InlineData("WDM: Microphone (USB Audio Device)", 3)]
    [InlineData("MME: Microphone (USB Audio Device)", 1)]
    [InlineData("KS: Microphone (USB Audio Device)", 4)]
    [InlineData("  wDm: microphone (USB AUDIO device)  ", 3)]
    public void ResolvesCompleteDecoratedNames(string readback, int driver)
    {
        var result = VoicemeeterDeviceIdentity.Resolve(readback, [new(driver, "Microphone (USB Audio Device)", "hardware")]);
        Assert.Equal(VoicemeeterDeviceResolutionKind.Resolved, result.Kind);
        Assert.Equal(driver, result.Device!.InterfaceType);
    }

    [Fact]
    public void PrefixDisambiguatesDriverButBareMultiDriverNameCannotGuess()
    {
        VoicemeeterAudioDevice[] devices = [new(3, "USB Microphone", "a"), new(1, "USB Microphone", "a")];
        Assert.Equal(VoicemeeterDeviceResolutionKind.Ambiguous, VoicemeeterDeviceIdentity.Resolve("USB Microphone", devices).Kind);
        Assert.Equal(1, VoicemeeterDeviceIdentity.Resolve("MME: USB Microphone", devices).Device!.InterfaceType);
        Assert.Equal(3, VoicemeeterDeviceIdentity.Resolve("USB Microphone", devices, "wdm").Device!.InterfaceType);
    }

    [Fact]
    public void SimilarNamesUnknownPrefixesAndDuplicateHardwareNeverMatchLoosely()
    {
        Assert.Equal(VoicemeeterDeviceResolutionKind.Unresolved,
            VoicemeeterDeviceIdentity.Resolve("USB Microphone", [new(3, "USB Microphone 2", "a")]).Kind);
        Assert.Equal(VoicemeeterDeviceResolutionKind.Unresolved,
            VoicemeeterDeviceIdentity.Resolve("XYZ: USB Microphone", [new(3, "USB Microphone", "a")]).Kind);
        Assert.Equal(VoicemeeterDeviceResolutionKind.Ambiguous,
            VoicemeeterDeviceIdentity.Resolve("WDM: USB Microphone", [new(3, "USB Microphone", "a"), new(3, "USB Microphone", "b")]).Kind);
        Assert.False(VoicemeeterDeviceIdentity.SameName("WDM: USB Microphone", "MME: USB Microphone"));
    }

    [Theory]
    [InlineData("WDM: Wardogs microphone", true)]
    [InlineData("MME: Owner microphone", true)]
    [InlineData("Other microphone", false)]
    [InlineData("Wardogs microphone 2", false)]
    [InlineData("MME: Wardogs microphone", false)]
    public void SavedOwnershipAcceptsAppliedOrPriorAndRejectsExternalChange(string current, bool safe) =>
        Assert.Equal(safe, VoicemeeterDeviceIdentity.CanReuseOwnedStrip(current, "Wardogs microphone", "MME: Owner microphone"));
}
