using NAudio.CoreAudioApi;
using WardogsRadio.Playback;
using Xunit;
using Xunit.Abstractions;

namespace WardogsRadio.Core.Tests;

[Collection("Live Voicemeeter")]
public sealed class LiveWindowsAudioInteropTests(ITestOutputHelper output)
{
    [Fact]
    public async Task StartupMultimediaDefaultsMapToCanonicalInventoryWithoutChangingWindows()
    {
        if (Environment.GetEnvironmentVariable("WARDOGS_LIVE_DEFAULT_PROBE") != "1") return;
        var snapshot = new WindowsDefaultAudioEndpointService().Read();
        var inventory = await new WindowsAudioEndpointService().DiscoverAsync();
        foreach (var endpoint in new[] { snapshot.Capture, snapshot.Render })
        {
            Assert.NotNull(endpoint);
            output.WriteLine($"Multimedia default: {endpoint.Name} / {endpoint.Id}; input={endpoint.IsInput}; active={endpoint.IsPresent}");
            var match = Assert.Single(inventory, item => item.IsInput == endpoint.IsInput && AudioDeviceIdentity.SameEndpoint(item.Id, endpoint.Id));
            output.WriteLine($"Canonical inventory: {match.Id}");
            using var meter = new WindowsAudioPeakMeter();
            Assert.True(meter.TryRead(match.Id, out var peak));
            output.WriteLine($"Endpoint meter readable; peak={peak}");
        }
        var after = new WindowsDefaultAudioEndpointService().Read();
        Assert.Equal(snapshot.Capture!.Id, after.Capture!.Id);
        Assert.Equal(snapshot.Render!.Id, after.Render!.Id);
    }

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
