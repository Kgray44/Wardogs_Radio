namespace WardogsRadio.Core;

public static class YouTubeReturnPolicy
{
    public static double StartingSeconds(PlaybackMode mode, double savedSeconds, double durationSeconds)
    {
        if (mode == PlaybackMode.RestartTrack || !double.IsFinite(savedSeconds)) return 0;
        var position = Math.Max(0, savedSeconds);
        if (double.IsFinite(durationSeconds) && durationSeconds > 0 && position >= durationSeconds - 1.5)
            return 0;
        return position;
    }
}
