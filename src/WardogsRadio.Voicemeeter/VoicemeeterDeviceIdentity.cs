namespace WardogsRadio.Voicemeeter;

public enum VoicemeeterDeviceResolutionKind { Resolved, Unresolved, Ambiguous }
public sealed record VoicemeeterDeviceResolution(VoicemeeterDeviceResolutionKind Kind,
    VoicemeeterAudioDevice? Device, bool Normalized, string Detail);

/// <summary>Complete names only. A presentation prefix is also driver evidence, never a fuzzy match.</summary>
public static class VoicemeeterDeviceIdentity
{
    public static (string Name, string? Driver) Parse(string? name)
    {
        var value = name?.Trim() ?? "";
        foreach (var driver in new[] { "WDM", "MME", "KS", "ASIO" })
            if (value.StartsWith(driver + ":", StringComparison.OrdinalIgnoreCase))
                return (value[(driver.Length + 1)..].Trim(), driver.ToLowerInvariant());
        return (value, null);
    }

    public static VoicemeeterDeviceResolution Resolve(string? readback,
        IReadOnlyList<VoicemeeterAudioDevice> devices, string? requiredDriver = null)
    {
        var identity = Parse(readback);
        var candidates = devices.Where(device =>
        {
            var candidate = Parse(device.Name);
            var driver = device.InterfaceName.ToLowerInvariant();
            return identity.Name.Length > 0 && SameName(identity.Name, candidate.Name) &&
                (identity.Driver is null || identity.Driver == driver) &&
                (requiredDriver is null || driver.Equals(requiredDriver, StringComparison.OrdinalIgnoreCase));
        }).ToArray();
        var kind = candidates.Length switch { 1 => VoicemeeterDeviceResolutionKind.Resolved,
            0 => VoicemeeterDeviceResolutionKind.Unresolved, _ => VoicemeeterDeviceResolutionKind.Ambiguous };
        var device = candidates.Length == 1 ? candidates[0] : null;
        return new(kind, device, device is not null && readback != device.Name,
            $"{kind}: readback='{readback}', driver={identity.Driver ?? requiredDriver ?? "unknown"}, candidates={candidates.Length}, resolved={device?.Name ?? "none"}/{device?.InterfaceName ?? "none"}, normalized={device is not null && readback != device.Name}");
    }

    public static bool SameName(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);
        return a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase) &&
            (a.Driver is null || b.Driver is null || a.Driver == b.Driver);
    }

    public static bool MatchesAssignment(string? readback, string? expected, string? driver)
    {
        var actual = Parse(readback);
        return SameName(readback, expected) && (actual.Driver is null ||
            actual.Driver.Equals(driver, StringComparison.OrdinalIgnoreCase));
    }

    public static bool CanReuseOwnedStrip(string? current, string? applied, string? prior) =>
        !string.IsNullOrWhiteSpace(applied) && MatchesAssignment(current, applied, "wdm") ||
        prior is not null && SameName(current, prior);
}
