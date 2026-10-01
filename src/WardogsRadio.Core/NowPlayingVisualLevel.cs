namespace WardogsRadio.Core;

public enum PlaybackListeningTopology { Unavailable, DirectEndpoint, Mixer }
public enum VisualLevelSource { None, ListeningEndpoint, MusicStrip }
public sealed record VisualSignal(bool Available, float Peak);
public sealed record NowPlayingVisualLevel(VisualLevelSource Source, float Peak);

public static class NowPlayingVisualLevelResolver
{
    public static NowPlayingVisualLevel Resolve(PlaybackListeningTopology topology, bool playing, bool switching,
        VisualSignal music, VisualSignal listening)
    {
        bool Valid(VisualSignal signal) => signal.Available && float.IsFinite(signal.Peak);
        NowPlayingVisualLevel Level(VisualLevelSource source, VisualSignal signal) => new(source, Math.Clamp(signal.Peak, 0, 1));
        if (!playing || switching || topology == PlaybackListeningTopology.Unavailable) return new(VisualLevelSource.None, 0);
        // Direct playback's game-feed strip is not evidence of audible listening.
        if (topology == PlaybackListeningTopology.DirectEndpoint)
            return Valid(listening) ? Level(VisualLevelSource.ListeningEndpoint, listening) : new(VisualLevelSource.None, 0);
        // For an independently verified mixer listening path, prefer its physical endpoint;
        // the active mixer signal is an alternate only when that endpoint meter is unavailable.
        if (Valid(listening)) return Level(VisualLevelSource.ListeningEndpoint, listening);
        return Valid(music) ? Level(VisualLevelSource.MusicStrip, music) : new(VisualLevelSource.None, 0);
    }

    public static double BarHeight(int index, double level, double previous, bool reduceMotion)
    {
        var shape = .44 + ((index * 5 + 3) % 7) * .075;
        var desired = 3 + Math.Clamp(level, 0, 1) * 52 * shape;
        return reduceMotion ? desired : desired >= previous ? previous + (desired - previous) * .58
            : Math.Max(desired, previous - 1.55);
    }
}
