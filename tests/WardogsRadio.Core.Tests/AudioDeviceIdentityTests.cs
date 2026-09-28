using WardogsRadio.Playback;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class AudioDeviceIdentityTests
{
    [Fact]
    public void CompleteEndpointGuidMatchesAcrossWindowsAndMpvFormats()
    {
        const string windows = @"SWD\MMDEVAPI\{0.0.0.00000000}.{D19C6579-B0C2-43D8-9E5F-F66A242151A7}";
        const string mpv = "wasapi/{d19c6579-b0c2-43d8-9e5f-f66a242151a7}";
        Assert.True(AudioDeviceIdentity.SameEndpoint(windows, mpv));
    }

    [Theory]
    [InlineData(@"SWD\MMDEVAPI\{0.0.0.00000000}.{D19C6579-B0C2-43D8-9E5F-F66A242151A7}", "wasapi/{D19C6579-B0C2-43D8-9E5F-F66A242151A8}")]
    [InlineData(@"SWD\MMDEVAPI\{0.0.0.00000000}.{D19C6579-B0C2-43D8-9E5F-F66A242151A7}", "auto")]
    [InlineData("", "wasapi/{D19C6579-B0C2-43D8-9E5F-F66A242151A7}")]
    public void AmbiguousOrDifferentIdentityDoesNotMatch(string windows, string mpv) =>
        Assert.False(AudioDeviceIdentity.SameEndpoint(windows, mpv));
}
