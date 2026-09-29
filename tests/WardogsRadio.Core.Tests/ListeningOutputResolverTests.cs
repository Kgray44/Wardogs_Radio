using WardogsRadio.Playback;
using Xunit;

namespace WardogsRadio.Core.Tests;

public sealed class ListeningOutputResolverTests
{
    const string EndpointId = @"SWD\MMDEVAPI\{0.0.0.00000000}.{D19C6579-B0C2-43D8-9E5F-F66A242151A7}";
    static readonly WindowsAudioEndpoint Endpoint = new(EndpointId, "Speakers (Razer Barracuda Pro 2.4)", false);

    [Fact]
    public void StableEndpointGuidWinsWhenDescriptionsDiffer()
    {
        var player = new MpvAudioDevice("wasapi/{D19C6579-B0C2-43D8-9E5F-F66A242151A7}", "Generic USB Audio");
        var result = ListeningOutputResolver.Resolve(Endpoint, [Endpoint], [player]);
        Assert.Equal(player, result.Device);
        Assert.Equal("endpoint GUID", result.Strategy);
    }

    [Fact]
    public void PreviouslyBoundPlayerIdSurvivesDescriptionChange()
    {
        var player = new MpvAudioDevice("wasapi/custom-stable-id", "Renamed audio endpoint");
        var result = ListeningOutputResolver.Resolve(Endpoint, [Endpoint], [player], player.Name, true);
        Assert.Equal(player, result.Device);
        Assert.Equal("saved player ID", result.Strategy);
        Assert.False(ListeningOutputResolver.Resolve(Endpoint, [Endpoint], [player], player.Name, false).Resolved);
    }

    [Fact]
    public void SlightlyDifferentUniqueDescriptionsCanResolveSafely()
    {
        var player = new MpvAudioDevice("wasapi/non-guid-backend-id", "WASAPI: Headphones - Razer Barracuda Pro 2.4");
        var result = ListeningOutputResolver.Resolve(Endpoint, [Endpoint], [player]);
        Assert.Equal(player, result.Device);
        Assert.Equal("unique normalized description", result.Strategy);
    }

    [Fact]
    public void DuplicatePlayerDescriptionsAreRejected()
    {
        var devices = new[]
        {
            new MpvAudioDevice("wasapi/one", "Razer Barracuda Pro 2.4"),
            new MpvAudioDevice("wasapi/two", "Headphones (Razer Barracuda Pro 2.4)")
        };
        var result = ListeningOutputResolver.Resolve(Endpoint, [Endpoint], devices);
        Assert.False(result.Resolved);
        Assert.Equal("ambiguous description", result.Strategy);
    }

    [Fact]
    public void DuplicateWindowsDescriptionsAreRejected()
    {
        var other = new WindowsAudioEndpoint(@"SWD\MMDEVAPI\{0.0.0.00000000}.{00000000-0000-0000-0000-000000000001}",
            Endpoint.Name, false);
        var result = ListeningOutputResolver.Resolve(Endpoint, [Endpoint, other],
            [new MpvAudioDevice("wasapi/non-guid-backend-id", Endpoint.Name)]);
        Assert.False(result.Resolved);
        Assert.Equal("ambiguous description", result.Strategy);
    }

    [Fact]
    public void NonWasapiDescriptionNeverSilentlyOverridesPhysicalSelection()
    {
        var result = ListeningOutputResolver.Resolve(Endpoint, [Endpoint],
            [new MpvAudioDevice("auto", Endpoint.Name), new MpvAudioDevice("dsound/device", Endpoint.Name)]);
        Assert.False(result.Resolved);
    }

    [Fact]
    public void UnplugAndReplugKeepTheConfiguredChoiceVisible()
    {
        var unplugged = ListeningOutputInventory.IncludeSavedSelection([], Endpoint.Id, Endpoint.Name);
        var selectedOffline = Assert.Single(unplugged);
        Assert.Equal(Endpoint.Id, selectedOffline.Id);
        Assert.Equal(Endpoint.Name, selectedOffline.Name);
        Assert.False(selectedOffline.IsPresent);

        var replugged = ListeningOutputInventory.IncludeSavedSelection([Endpoint], Endpoint.Id, Endpoint.Name);
        Assert.Equal(Endpoint, Assert.Single(replugged));
        Assert.True(replugged[0].IsPresent);
    }

    [Fact]
    public void SwitchingPhysicalOutputsChangesResolutionWithoutGuessing()
    {
        var second = new WindowsAudioEndpoint(@"SWD\MMDEVAPI\{0.0.0.00000000}.{00000000-0000-0000-0000-000000000002}",
            "Speakers (Realtek Audio)", false);
        var firstPlayer = new MpvAudioDevice("wasapi/{D19C6579-B0C2-43D8-9E5F-F66A242151A7}", "USB headset");
        var secondPlayer = new MpvAudioDevice("wasapi/{00000000-0000-0000-0000-000000000002}", "Realtek Audio");
        var windows = new[] { Endpoint, second };
        var players = new[] { firstPlayer, secondPlayer };
        Assert.Equal(firstPlayer, ListeningOutputResolver.Resolve(Endpoint, windows, players).Device);
        Assert.Equal(secondPlayer, ListeningOutputResolver.Resolve(second, windows, players,
            firstPlayer.Name, savedIdBelongsToEndpoint: false).Device);
    }

    [Fact]
    public void SetupAndRoutingProjectTheSameSavedEndpointImmediately()
    {
        var second = new WindowsAudioEndpoint(@"SWD\MMDEVAPI\{0.0.0.00000000}.{00000000-0000-0000-0000-000000000002}",
            "Speakers (Realtek Audio)", false);
        var setupDevices = new[] { Endpoint, second };
        var routingDevices = new[] { Endpoint, second };
        var savedEndpointId = second.Id;
        Assert.Equal(second, ListeningOutputInventory.Selected(setupDevices, savedEndpointId));
        Assert.Equal(second, ListeningOutputInventory.Selected(routingDevices, savedEndpointId));
    }
}
