namespace WardogsRadio.Voicemeeter;

public sealed record SignalLevel(bool Available, float Peak);

/// <summary>Reads real post-mute strip and output-bus peaks from the Voicemeeter Remote API.</summary>
public sealed class VoicemeeterSignalMonitor(IVoicemeeterRemote remote)
{
    // The Remote API can expose a small constant digital noise floor even while
    // the route carries no source audio. Keep it out of the visual meter so a
    // "NO SIGNAL" label never accompanies a misleading fixed bar.
    public const float VisualSignalFloor = .005f;
    public SignalLevel ReadStrip(string? edition, int? strip)
    {
        if (strip is not { } index || !StripChannels(edition, index, out var first, out var count)) return new(false, 0);
        return ReadChannels(2, first, count);
    }

    public SignalLevel ReadBus(string? edition, string bus)
    {
        if (!BusChannels(edition, bus, out var first)) return new(false, 0);
        return ReadChannels(3, first, 2);
    }

    SignalLevel ReadChannels(int type, int first, int count)
    {
        var peak = 0f;
        for (var channel = first; channel < first + count; channel++)
        {
            if (!remote.TryGetLevel(type, channel, out var value) || !float.IsFinite(value)) return new(false, 0);
            peak = Math.Max(peak, Math.Max(0, value));
        }
        return new(true, peak);
    }

    public static double BarValue(SignalLevel level)
    {
        if (!level.Available || level.Peak < VisualSignalFloor) return 0;
        var db = 20 * Math.Log10(level.Peak);
        return Math.Clamp((db + 60) / 60 * 100, 0, 100);
    }

    public static bool StripChannels(string? edition, int strip, out int first, out int count)
    {
        first = 0; count = 0;
        var physical = edition switch { "Standard" => 2, "Banana" => 3, "Potato" => 5, _ => 0 };
        var total = edition switch { "Standard" => 3, "Banana" => 5, "Potato" => 8, _ => 0 };
        if (strip < 0 || strip >= total) return false;
        first = strip < physical ? strip * 2 : physical * 2 + (strip - physical) * 8;
        count = strip < physical ? 2 : 8;
        return true;
    }

    public static bool BusChannels(string? edition, string bus, out int first)
    {
        first = 0;
        var index = edition switch
        {
            "Standard" => bus switch { "A1" => 0, "B1" => 1, _ => -1 },
            "Banana" => bus switch { "A1" => 0, "A2" => 1, "A3" => 2, "B1" => 3, "B2" => 4, _ => -1 },
            "Potato" => bus switch { "A1" => 0, "A2" => 1, "A3" => 2, "A4" => 3, "A5" => 4, "B1" => 5, "B2" => 6, "B3" => 7, _ => -1 },
            _ => -1
        };
        if (index < 0) return false;
        first = index * 8;
        return true;
    }
}
