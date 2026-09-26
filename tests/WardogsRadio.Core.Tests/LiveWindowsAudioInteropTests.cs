using NAudio.CoreAudioApi;
using WardogsRadio.Playback;
using Xunit;

namespace WardogsRadio.Core.Tests;

[Collection("Live Voicemeeter")]
public sealed class LiveWindowsAudioInteropTests
{
    [Fact]
    public async Task EndpointMeterAndAuditionEnumeratorShareOneComWrapper()
    {
        if (Environment.GetEnvironmentVariable("WARDOGS_LIVE_AUDIO_INTEROP_PROBE") != "1") return;
        var endpoints = await new WindowsAudioEndpointService().DiscoverAsync();
        var headphones = Assert.Single(endpoints, x => x.Name.Equals("Headphones (soundcore P31i)", StringComparison.OrdinalIgnoreCase));
        var b1 = Assert.Single(endpoints, x => x.IsInput && x.Name.StartsWith("Voicemeeter Out B1", StringComparison.OrdinalIgnoreCase));

        using var meter = new WindowsAudioPeakMeter();
        Assert.True(meter.TryRead(headphones.Id, out _), "The live Windows headphone endpoint meter did not open.");
        using var enumerator = new MMDeviceEnumerator();
        using var capture = enumerator.GetDevice(b1.Id.Split('\\').Last());
        using var render = enumerator.GetDevice(headphones.Id.Split('\\').Last());
        Assert.Equal(DataFlow.Capture, capture.DataFlow);
        Assert.Equal(DataFlow.Render, render.DataFlow);
    }
}
