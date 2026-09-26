namespace WardogsRadio.Core;

public static class StationReturnPolicy
{
    public static double StartingSeconds(PlaybackMode mode, double savedSeconds) =>
        mode == PlaybackMode.RestartTrack ? 0 : Math.Max(0, savedSeconds);
}
