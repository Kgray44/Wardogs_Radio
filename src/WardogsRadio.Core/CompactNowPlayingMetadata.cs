namespace WardogsRadio.Core;

/// <summary>
/// Compact metadata intentionally describes the tuned channel and provider,
/// never an underlying source URL or local file path.
/// </summary>
public static class CompactNowPlayingMetadata
{
    public static string Format(Station station, string? artist)
    {
        var channel = $"CH {station.Order + 1:00}";
        if (!string.IsNullOrWhiteSpace(artist)) return $"{artist} · {channel}";
        var provider = station.ProviderId.Equals("youtube", StringComparison.OrdinalIgnoreCase) ? "YOUTUBE" :
            station.ProviderId.Equals("mpv", StringComparison.OrdinalIgnoreCase) ? "LOCAL" :
            station.ProviderId.Equals("external-audio", StringComparison.OrdinalIgnoreCase) ? "EXTERNAL" :
            station.ProviderId.ToUpperInvariant();
        return $"{channel} · {provider}";
    }
}
