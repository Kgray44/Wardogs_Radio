namespace WardogsRadio.Core;

/// <summary>Confidence in the final game-output observation, independent of peak classification.</summary>
public enum OutputTelemetryConfidence { Unavailable, Partial, Conflicting, Verified }

public sealed record OutputTelemetryAssessment(OutputTelemetryConfidence Confidence, string Detail)
{
    public bool CanControlClipGuard => Confidence == OutputTelemetryConfidence.Verified;
}

/// <summary>
/// Compares the Remote API B1 observation with the independently captured Windows B1
/// endpoint. A live/quiet disagreement is evidence, not silence, and must never drive
/// automatic protection.
/// </summary>
public static class OutputTelemetryAssessor
{
    public const float SignalThreshold = .005f;

    public static OutputTelemetryAssessment Assess(bool remoteAvailable, float remotePeak,
        bool endpointAvailable, float endpointPeak)
    {
        if (!remoteAvailable && !endpointAvailable)
            return new(OutputTelemetryConfidence.Unavailable, "Neither the Remote API B1 meter nor the Windows B1 endpoint is available.");
        if (!remoteAvailable || !endpointAvailable)
            return new(OutputTelemetryConfidence.Partial, "Only one B1 observation is available; automatic protection is paused.");

        var remoteLive = remotePeak > SignalThreshold;
        var endpointLive = endpointPeak > SignalThreshold;
        if (remoteLive != endpointLive)
            return new(OutputTelemetryConfidence.Conflicting,
                "Remote API B1 and Windows Voicemeeter Out B1 disagree about live signal; automatic protection is paused.");
        return new(OutputTelemetryConfidence.Verified,
            remoteLive ? "Remote API B1 and Windows Voicemeeter Out B1 both show live signal." :
                "Remote API B1 and Windows Voicemeeter Out B1 both show quiet output.");
    }
}
