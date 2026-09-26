namespace WardogsRadio.Core;

public sealed record MpvStationCursor(int Index, double Seconds, bool Ended);

/// <summary>Calculates the audible position when a local station is re-tuned in Radio mode.</summary>
public static class MpvStationTimeline
{
    public static MpvStationCursor Advance(IReadOnlyList<double> durations, int index, double seconds,
        double elapsedSeconds, StationRepeatMode repeat)
    {
        if (durations.Count == 0 || durations.Any(x => !double.IsFinite(x) || x <= 0))
            throw new ArgumentException("Every track needs a positive known duration.", nameof(durations));
        if (index < 0 || index >= durations.Count) throw new ArgumentOutOfRangeException(nameof(index));
        var position = Math.Clamp(seconds, 0, durations[index]);
        var elapsed = Math.Max(0, elapsedSeconds);
        if (repeat == StationRepeatMode.Track)
            return new(index, (position + elapsed) % durations[index], false);
        var total = durations.Sum();
        var absolute = durations.Take(index).Sum() + position + elapsed;
        if (repeat == StationRepeatMode.Off && absolute >= total)
            return new(durations.Count - 1, durations[^1], true);
        if (repeat == StationRepeatMode.Playlist) absolute %= total;
        for (var track = 0; track < durations.Count; track++)
        {
            if (absolute < durations[track]) return new(track, absolute, false);
            absolute -= durations[track];
        }
        return new(durations.Count - 1, durations[^1], true);
    }
}
