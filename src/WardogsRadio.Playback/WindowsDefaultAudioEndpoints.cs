using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using WardogsRadio.Core;

namespace WardogsRadio.Playback;

public sealed record WindowsDefaultAudioSnapshot(WindowsAudioEndpoint? Capture, WindowsAudioEndpoint? Render,
    string? CaptureError = null, string? RenderError = null);

/// <summary>One launch-time snapshot of Multimedia defaults. Does not subscribe to default changes.</summary>
public sealed class WindowsDefaultAudioEndpointService
{
    public WindowsDefaultAudioSnapshot Read()
    {
        (WindowsAudioEndpoint? Endpoint, string? Error) ReadFlow(DataFlow flow)
        {
            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
                return (new WindowsAudioEndpoint(@"SWD\MMDEVAPI\" + device.ID, device.FriendlyName,
                    flow == DataFlow.Capture, device.State == DeviceState.Active), null);
            }
            catch (Exception error) when (error is COMException or InvalidOperationException or InvalidCastException)
            { return (null, error.GetType().Name); }
        }
        var capture = ReadFlow(DataFlow.Capture);
        var render = ReadFlow(DataFlow.Render);
        return new(capture.Endpoint, render.Endpoint, capture.Error, render.Error);
    }
}

public sealed record StartupAudioDevicePlan(WindowsAudioEndpoint? Microphone, WindowsAudioEndpoint? Listening,
    string? MicrophoneAttention, string? ListeningAttention);

public static class StartupAudioDevicePolicy
{
    public static bool IsVirtualMicrophone(WindowsAudioEndpoint endpoint) => endpoint.IsInput &&
        (endpoint.Name.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase) ||
         endpoint.Name.Contains("WARDOGS", StringComparison.OrdinalIgnoreCase));

    public static StartupAudioDevicePlan Plan(StartupAudioDeviceMode mode, string? savedMic, string? savedListening,
        WindowsDefaultAudioSnapshot defaults, IReadOnlyList<WindowsAudioEndpoint> inventory)
    {
        if (mode != StartupAudioDeviceMode.WindowsDefaults) return new(null, null, null, null);
        WindowsAudioEndpoint? Match(WindowsAudioEndpoint? endpoint, bool input)
        {
            if (endpoint is not { IsPresent: true } || endpoint.IsInput != input) return null;
            var matches = inventory.Where(item => item.IsPresent && item.IsInput == input &&
                AudioDeviceIdentity.SameEndpoint(item.Id, endpoint.Id)).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        var mic = Match(defaults.Capture, true);
        var listening = Match(defaults.Render, false);
        var micAttention = defaults.Capture is { } capture && IsVirtualMicrophone(capture)
            ? "WINDOWS DEFAULT MICROPHONE IS A VIRTUAL AUDIO ROUTE · Choose a physical microphone."
            : mic is null ? "WINDOWS DEFAULT MICROPHONE IS UNAVAILABLE · Previous WARDOGS selection retained; choose a physical microphone if needed." : null;
        if (micAttention is not null) mic = null;
        var listeningAttention = listening is null
            ? "WINDOWS DEFAULT LISTENING OUTPUT IS UNAVAILABLE · Previous WARDOGS selection retained; review Audio & Routing." : null;
        return new(mic is not null && !AudioDeviceIdentity.SameEndpoint(mic.Id, savedMic) ? mic : null,
            listening is not null && !AudioDeviceIdentity.SameEndpoint(listening.Id, savedListening) ? listening : null,
            micAttention, listeningAttention);
    }
}
