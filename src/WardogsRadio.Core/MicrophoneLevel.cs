namespace WardogsRadio.Core;

/// <summary>Maps the displayed linear mic percentage to Voicemeeter strip gain.</summary>
public static class MicrophoneLevel
{
    public const double Maximum = 1.6;

    public static float GainDb(double level)
    {
        if (!double.IsFinite(level)) throw new ArgumentOutOfRangeException(nameof(level));
        var normalized = Math.Clamp(level, 0, Maximum);
        return normalized == 0 ? -60f : (float)(20 * Math.Log10(normalized));
    }
}
