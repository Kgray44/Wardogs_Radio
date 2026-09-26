namespace WardogsRadio.Core;

/// <summary>
/// Converts a provider's source-relative cursor into the user-facing cursor for a
/// non-destructive library song. Providers remain the owners of absolute seeking.
/// </summary>
public readonly record struct NowPlayingTimeline(double PositionSeconds, double? DurationSeconds)
{
    public bool CanSeek => DurationSeconds is > 0;
    public double ProgressPercent => DurationSeconds is > 0 ? Math.Clamp(PositionSeconds / DurationSeconds.Value * 100, 0, 100) : 0;

    public static NowPlayingTimeline FromSource(double sourcePositionSeconds, double sourceDurationSeconds,
        double songStartSeconds = 0, double? songEndSeconds = null)
    {
        var start = Math.Max(0, double.IsFinite(songStartSeconds) ? songStartSeconds : 0);
        double? end = songEndSeconds is { } explicitEnd && double.IsFinite(explicitEnd) && explicitEnd > start
            ? explicitEnd
            : double.IsFinite(sourceDurationSeconds) && sourceDurationSeconds > start ? (double?)sourceDurationSeconds : null;
        double? duration = end is { } resolvedEnd ? Math.Max(0, resolvedEnd - start) : null;
        var position = Math.Max(0, double.IsFinite(sourcePositionSeconds) ? sourcePositionSeconds - start : 0);
        if (duration is { } known) position = Math.Clamp(position, 0, known);
        return new(position, duration);
    }

    public static double ToSourcePosition(double logicalPositionSeconds, double songStartSeconds, double? songEndSeconds,
        double? sourceDurationSeconds = null)
    {
        var start = Math.Max(0, double.IsFinite(songStartSeconds) ? songStartSeconds : 0);
        var maximum = songEndSeconds is { } explicitEnd && double.IsFinite(explicitEnd) && explicitEnd >= start
            ? explicitEnd
            : sourceDurationSeconds is { } sourceEnd && double.IsFinite(sourceEnd) && sourceEnd >= start ? sourceEnd : (double?)null;
        var source = start + Math.Max(0, double.IsFinite(logicalPositionSeconds) ? logicalPositionSeconds : 0);
        return maximum is { } end ? Math.Clamp(source, start, end) : Math.Max(start, source);
    }
}
