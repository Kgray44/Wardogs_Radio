using System.Text;

namespace WardogsRadio.Playback;

/// <summary>Resolves one saved Windows render endpoint to one concrete player output.</summary>
public static class ListeningOutputResolver
{
    public sealed record Result(MpvAudioDevice? Device, string Strategy, string Detail)
    {
        public bool Resolved => Device is not null;
    }

    public static Result Resolve(WindowsAudioEndpoint endpoint,
        IReadOnlyList<WindowsAudioEndpoint> windowsEndpoints,
        IReadOnlyList<MpvAudioDevice> playerDevices, string? savedPlayerId = null,
        bool savedIdBelongsToEndpoint = false)
    {
        if (endpoint.IsInput) return new(null, "invalid endpoint", "The selected Windows device is a recording endpoint.");
        var candidates = playerDevices.Where(device => device.Name.StartsWith("wasapi/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (savedIdBelongsToEndpoint && !string.IsNullOrWhiteSpace(savedPlayerId))
        {
            var saved = candidates.Where(device => string.Equals(device.Name, savedPlayerId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (saved.Length == 1)
                return new(saved[0], "saved player ID", "The player still lists the device previously bound to this Windows endpoint.");
        }

        var identity = candidates.Where(device => AudioDeviceIdentity.SameEndpoint(endpoint.Id, device.Name)).ToArray();
        if (identity.Length == 1)
            return new(identity[0], "endpoint GUID", "The Windows endpoint and player device contain the same endpoint GUID.");
        if (identity.Length > 1)
            return new(null, "ambiguous endpoint GUID", $"{identity.Length} player devices claim the selected endpoint GUID.");

        var normalized = Normalize(endpoint.Name);
        if (normalized.Length == 0) return new(null, "unresolved", "The Windows endpoint name has no usable description.");
        var sameWindowsNames = windowsEndpoints.Count(other => !other.IsInput && Normalize(other.Name) == normalized);
        var descriptions = candidates.Where(device => Normalize(device.Description) == normalized).ToArray();
        if (sameWindowsNames == 1 && descriptions.Length == 1)
            return new(descriptions[0], "unique normalized description", "Exactly one Windows render endpoint and one WASAPI player device share this description.");
        return new(null, descriptions.Length > 1 || sameWindowsNames > 1 ? "ambiguous description" : "unresolved",
            $"Description matches: {descriptions.Length} player device(s), {sameWindowsNames} Windows endpoint(s). No output was guessed.");
    }

    static string Normalize(string value)
    {
        var text = value.Trim();
        var prefixes = new[] { "wasapi:", "speakers", "speaker", "headphones", "headset", "digital audio", "output" };
        while (true)
        {
            var prefix = prefixes.FirstOrDefault(candidate => text.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (prefix is null) break;
            text = text[prefix.Length..].TrimStart(' ', ':', '-', '(', ')');
        }
        var result = new StringBuilder(text.Length);
        foreach (var character in text)
            if (char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
        return result.ToString();
    }
}
