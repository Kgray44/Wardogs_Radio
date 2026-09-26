namespace WardogsRadio.Core;

/// <summary>
/// Runtime-only observation session for the user's own loud-music/microphone check. It never
/// produces sound, changes a route, or alters a gain until an owner explicitly applies its
/// recommendation in the presentation layer.
/// </summary>
public sealed class BroadcastLevelTestSession
{
    double? _musicPeakDbfs;
    double? _microphonePeakDbfs;
    double? _gamePeakDbfs;

    public bool IsRunning { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }

    public void Start(DateTimeOffset now)
    {
        IsRunning = true;
        StartedAt = now;
        _musicPeakDbfs = _microphonePeakDbfs = _gamePeakDbfs = null;
    }

    public void Observe(OutputHealthSnapshot health)
    {
        if (!IsRunning) return;
        Track(ref _musicPeakDbfs, health.Music.PeakDbfs);
        Track(ref _microphonePeakDbfs, health.Microphone.PeakDbfs);
        Track(ref _gamePeakDbfs, health.GameBus.PeakDbfs);
    }

    public BroadcastLevelTestResult Stop(ClipGuardSettings settings, DateTimeOffset now)
    {
        IsRunning = false;
        settings.Normalize();
        var recommendation = _gamePeakDbfs is { } gamePeak && !double.IsNegativeInfinity(gamePeak)
            ? Math.Clamp(gamePeak - settings.SafetyCeilingDbfs + .5, 0, 18)
            : 0;
        var state = Classify(_gamePeakDbfs, settings);
        var summary = _gamePeakDbfs is null
            ? "No valid B1/game-mix telemetry was observed. Check Voicemeeter before making a level decision."
            : recommendation > .05
                ? $"The measured game mix exceeded the {settings.SafetyCeilingDbfs:0.0} dBFS safety ceiling. Reduce requested game music by about {recommendation:0.0} dB, or keep the current setting."
                : "The measured game mix stayed within the configured safety ceiling. No requested-level change is recommended.";
        return new BroadcastLevelTestResult(StartedAt, now, _musicPeakDbfs, _microphonePeakDbfs, _gamePeakDbfs,
            state, recommendation, summary);
    }

    static void Track(ref double? current, double? incoming)
    {
        if (incoming is not { } value || double.IsNegativeInfinity(value)) return;
        current = current is null ? value : Math.Max(current.Value, value);
    }

    static OutputHealthState Classify(double? peak, ClipGuardSettings settings)
    {
        if (peak is null) return OutputHealthState.Unavailable;
        if (double.IsNegativeInfinity(peak.Value)) return OutputHealthState.Safe;
        if (peak >= -.1) return OutputHealthState.Clip;
        if (peak >= settings.NearClipThresholdDbfs) return OutputHealthState.NearClip;
        if (peak >= settings.SafetyCeilingDbfs) return OutputHealthState.Hot;
        return peak >= -6 ? OutputHealthState.Healthy : OutputHealthState.Safe;
    }
}

public sealed record BroadcastLevelTestResult(DateTimeOffset? StartedAt, DateTimeOffset CompletedAt,
    double? MusicPeakDbfs, double? MicrophonePeakDbfs, double? GamePeakDbfs, OutputHealthState State,
    double RecommendedGameMusicReductionDb, string Summary)
{
    public bool HasRecommendation => RecommendedGameMusicReductionDb > .05;
}
