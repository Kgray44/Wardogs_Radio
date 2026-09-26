namespace WardogsRadio.Voicemeeter;

/// <summary>
/// A reversible lease for Voicemeeter's documented strip brickwall limiter. The
/// owner chooses when to enable it; reading capability never writes to the mixer.
/// </summary>
public sealed record VoicemeeterStripLimiterCapability(bool Available, string Detail, int? Strip = null,
    float? CurrentLimitDb = null);

public sealed class VoicemeeterStripLimiter(IVoicemeeterRemote remote)
{
    const float MinimumLimitDb = -40;
    const float MaximumLimitDb = 12;
    int? _leasedStrip;
    float? _priorLimitDb;
    float? _appliedLimitDb;

    public bool IsActive => _leasedStrip is not null && _priorLimitDb is not null;
    public int? ActiveStrip => _leasedStrip;

    public VoicemeeterStripLimiterCapability Probe(int? strip)
    {
        var status = remote.Probe();
        if (!status.Connected)
            return new(false, "Limiter unavailable: Voicemeeter Remote API is not connected.");
        if (strip is not { } index || index < 0)
            return new(false, "Limiter unavailable: choose the Voicemeeter music strip first.");
        if (!remote.TryGetParameterFloat(Parameter(index), out var current) || !float.IsFinite(current))
            return new(false, $"Limiter unavailable on strip {index}: this Voicemeeter route does not expose a readable strip limiter.");
        return new(true, $"Strip {index} brickwall limiter is available · current limit {current:0.0} dB.", index, current);
    }

    public bool TryApply(int? strip, double ceilingDb, out string detail)
    {
        var capability = Probe(strip);
        if (!capability.Available || capability.Strip is not { } targetStrip || capability.CurrentLimitDb is not { } current)
        {
            detail = capability.Detail;
            return false;
        }

        if (_leasedStrip is { } priorStrip && priorStrip != targetStrip && !TryRestore(out detail))
            return false;

        var target = (float)Math.Clamp(ceilingDb, MinimumLimitDb, MaximumLimitDb);
        if (_leasedStrip == targetStrip && _appliedLimitDb is { } applied && Math.Abs(applied - target) < .05f)
        {
            detail = $"Limiter active on strip {targetStrip} at {target:0.0} dB; its prior { _priorLimitDb:0.0} dB value will be restored when disabled or WARDOGS closes.";
            return true;
        }

        if (!remote.TrySetParameterFloat(Parameter(targetStrip), target) ||
            !remote.TryGetParameterFloat(Parameter(targetStrip), out var verified) || Math.Abs(verified - target) >= .1f)
        {
            detail = $"Limiter write could not be verified on strip {targetStrip}; the mixer was not assumed safe.";
            return false;
        }

        // Do not claim ownership until the first write has been read back. A failed
        // capability/write probe must never leave a phantom lease that could later
        // restore an external mixer value WARDOGS never successfully changed.
        if (_leasedStrip != targetStrip)
        {
            _leasedStrip = targetStrip;
            _priorLimitDb = current;
        }
        _appliedLimitDb = target;
        detail = $"Limiter active on strip {targetStrip} at {target:0.0} dB; its prior {_priorLimitDb:0.0} dB value will be restored when disabled or WARDOGS closes.";
        return true;
    }

    public bool TryRestore(out string detail)
    {
        if (_leasedStrip is not { } strip || _priorLimitDb is not { } prior)
        {
            detail = "WARDOGS does not currently own a Voicemeeter limiter setting.";
            return true;
        }
        if (!remote.TrySetParameterFloat(Parameter(strip), prior) ||
            !remote.TryGetParameterFloat(Parameter(strip), out var verified) || Math.Abs(verified - prior) >= .1f)
        {
            detail = $"Could not restore the prior {prior:0.0} dB limiter value on strip {strip}. WARDOGS remains open so you can retry safely.";
            return false;
        }
        _leasedStrip = null;
        _priorLimitDb = null;
        _appliedLimitDb = null;
        detail = $"Restored the prior {prior:0.0} dB limiter value on strip {strip}.";
        return true;
    }

    static string Parameter(int strip) => $"Strip[{strip}].Limit";
}
