namespace WardogsRadio.Core;

/// <summary>
/// The single UI-facing description of the current player. Runtime resume fields
/// are deliberately excluded until the provider has a real, ready player.
/// </summary>
public enum PlaybackPresentationKind { Idle, Loading, Ready, Playing, Paused, Error, RouteRepairRequired, Unavailable }

public sealed record PlaybackPresentationState(
    Station? Station,
    StationSong? Song,
    ProviderHealth ProviderHealth,
    PlaybackPresentationKind Kind,
    bool IsReady,
    bool IsPlaying,
    double? AbsoluteSourcePosition,
    double? AbsoluteSourceDuration,
    double? LogicalSongPosition,
    double? LogicalSongDuration,
    bool CanSeek)
{
    public static PlaybackPresentationState Idle { get; } = new(null, null, ProviderHealth.Unknown,
        PlaybackPresentationKind.Idle, false, false, null, null, null, null, false);

    public static PlaybackPresentationState Create(Station station, StationSong? song, ProviderHealth providerHealth,
        PlaybackPresentationKind kind, bool isReady, bool isPlaying, double? sourcePosition, double? sourceDuration)
    {
        var knownDuration = sourceDuration.GetValueOrDefault();
        if (!isReady || sourcePosition is null || knownDuration <= 0)
            return new(station, song, providerHealth, kind, isReady, isPlaying, null, null, null, null, false);

        var timeline = NowPlayingTimeline.FromSource(sourcePosition.Value, knownDuration, song?.StartSeconds ?? 0, song?.EndSeconds);
        return new(station, song, providerHealth, kind, isReady, isPlaying, sourcePosition, sourceDuration,
            timeline.PositionSeconds, timeline.DurationSeconds, timeline.CanSeek);
    }
}
