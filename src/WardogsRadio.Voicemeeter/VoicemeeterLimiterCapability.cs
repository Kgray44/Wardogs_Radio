namespace WardogsRadio.Voicemeeter;

public sealed record VoicemeeterLimiterCapability(bool Available, string Detail);

/// <summary>
/// Capability boundary for any future Voicemeeter limiter control. The Remote API exposes a
/// generic parameter surface, but this product currently has no documented, route-safe limiter
/// parameter that can be read, written, and restored across Standard/Banana/Potato editions.
/// Deliberately do not probe by writing guessed parameter names into an owner's live mixer.
/// </summary>
public static class VoicemeeterLimiterCapabilityProbe
{
    public static VoicemeeterLimiterCapability Probe(IVoicemeeterRemote remote)
    {
        var status = remote.Probe();
        if (!status.Connected)
            return new(false, "Limiter unavailable: Voicemeeter Remote API is not connected.");
        return new(false, $"Limiter unavailable on {status.Edition ?? "this Voicemeeter edition"}: no verified route-safe Remote API limiter parameter is available. Clip Guard uses reversible game-player gain only.");
    }
}
