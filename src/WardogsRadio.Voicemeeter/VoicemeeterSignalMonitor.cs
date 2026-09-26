namespace WardogsRadio.Voicemeeter;

public sealed record SignalLevel(bool Available, float Peak);
public sealed record RawLevelChannel(int Type, int Channel, bool Available, int ResultCode, float Peak, double? Dbfs);
public sealed record RawMeterPath(string Name, int Type, int FirstChannel, int ChannelCount, bool Available,
    int ResultCode, float Peak, double? Dbfs, IReadOnlyList<RawLevelChannel> Channels);
public sealed record VoicemeeterMeterForensics(string? Edition, IReadOnlyList<RawMeterPath> Strips,
    IReadOnlyList<RawMeterPath> Buses, IReadOnlyList<RawLevelChannel> ChannelScan)
{
    public RawMeterPath? Strip(int index) => Strips.FirstOrDefault(path => path.Name == $"Strip {index}");
    public RawMeterPath? Bus(string name) => Buses.FirstOrDefault(path => path.Name == name);
}

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

    /// <summary>
    /// Captures the documented strip/bus blocks plus a raw 0..3 API-type channel scan.
    /// The scan deliberately retains native return codes, so a live diagnosis can prove
    /// a mapping mismatch instead of treating an unavailable read as silence.
    /// </summary>
    public VoicemeeterMeterForensics CaptureForensics(string? edition)
    {
        var strips = new List<RawMeterPath>();
        var buses = new List<RawMeterPath>();
        var stripCount = edition switch { "Standard" => 3, "Banana" => 5, "Potato" => 8, _ => 0 };
        for (var strip = 0; strip < stripCount; strip++)
            if (StripChannels(edition, strip, out var first, out var count))
                strips.Add(ReadRawPath($"Strip {strip}", 2, first, count));

        var busNames = edition switch
        {
            "Standard" => new[] { "A1", "B1" },
            "Banana" => new[] { "A1", "A2", "A3", "B1", "B2" },
            "Potato" => new[] { "A1", "A2", "A3", "A4", "A5", "B1", "B2", "B3" },
            _ => []
        };
        foreach (var bus in busNames)
            if (BusChannels(edition, bus, out var first)) buses.Add(ReadRawPath(bus, 3, first, 2));

        var scan = new List<RawLevelChannel>();
        for (var type = 0; type <= 3; type++)
            for (var channel = 0; channel < 40; channel++) scan.Add(ReadRawChannel(type, channel));
        return new(edition, strips, buses, scan);
    }

    RawMeterPath ReadRawPath(string name, int type, int first, int count)
    {
        var channels = Enumerable.Range(first, count).Select(channel => ReadRawChannel(type, channel)).ToList();
        var available = channels.All(channel => channel.Available);
        var result = channels.FirstOrDefault(channel => !channel.Available)?.ResultCode ?? 0;
        // Keep any successfully returned peak even when another channel failed.
        // The path remains unavailable for control purposes, but the forensic log
        // can distinguish a partial API block from a genuinely quiet one.
        var peak = channels.Where(channel => channel.Available).Select(channel => channel.Peak).DefaultIfEmpty(0).Max();
        return new(name, type, first, count, available, result, peak, Dbfs(peak), channels);
    }

    RawLevelChannel ReadRawChannel(int type, int channel)
    {
        var result = remote.GetLevelResult(type, channel, out var value);
        var available = result == 0 && float.IsFinite(value);
        var peak = available ? Math.Max(0, value) : 0;
        return new(type, channel, available, result, peak, available ? Dbfs(peak) : null);
    }

    public static double Dbfs(float peak) => peak <= 0 ? double.NegativeInfinity : 20 * Math.Log10(peak);

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
