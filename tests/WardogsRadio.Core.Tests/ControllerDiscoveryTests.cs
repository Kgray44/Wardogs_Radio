using WardogsRadio.Input;
using WardogsRadio.Playback;
using Xunit;
using Xunit.Abstractions;

namespace WardogsRadio.Core.Tests;

public sealed class ControllerDiscoveryTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0x01, 0x04, true)]
    [InlineData(0x01, 0x05, true)]
    [InlineData(0x01, 0x08, true)]
    [InlineData(0x01, 0x02, false)]
    [InlineData(0x01, 0x06, false)]
    [InlineData(0x0C, 0x01, false)]
    public void OnlyGameControllerTopLevelUsagesPass(int page, int usage, bool expected)
        => Assert.Equal(expected, ControllerUsage.IsGameController((ushort)page, (ushort)usage));

    [Fact]
    public void LiveInventoryDoesNotUseNameMatchingForHostOrSystemControllers()
    {
        var devices = new XInputControllerService().Enumerate().ToList();
        foreach (var device in devices)
        {
            output.WriteLine($"{device.Kind}: {device.Name} [{device.DeviceId}]");
            Assert.DoesNotContain("host controller", device.Name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("system controller", device.Name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(@"SWD\MMDEVAPI\{0.0.1.00000000}.{123}", true)]
    [InlineData(@"SWD\MMDEVAPI\{0.0.0.00000000}.{456}", false)]
    public void EndpointDirectionUsesWindowsIdentityNotProductName(string id, bool capture)
        => Assert.Equal(capture, WindowsAudioEndpointService.IsCaptureEndpoint(id));
}
