using System.Text.RegularExpressions;

namespace WardogsRadio.Playback;

/// <summary>Matches an MPV WASAPI device to the same Windows audio endpoint by its complete endpoint GUID.</summary>
public static partial class AudioDeviceIdentity
{
    [GeneratedRegex(@"\{([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}")]
    private static partial Regex BracedGuid();

    public static bool SameEndpoint(string? windowsEndpointId, string? mpvDeviceName)
    {
        if (string.IsNullOrWhiteSpace(windowsEndpointId) || string.IsNullOrWhiteSpace(mpvDeviceName)) return false;
        var windows = BracedGuid().Matches(windowsEndpointId);
        var mpv = BracedGuid().Matches(mpvDeviceName);
        return windows.Count > 0 && mpv.Count > 0 &&
            Guid.TryParse(windows[^1].Groups[1].Value, out var windowsGuid) &&
            Guid.TryParse(mpv[^1].Groups[1].Value, out var mpvGuid) && windowsGuid == mpvGuid;
    }
}
