namespace WardogsRadio.Core;

/// <summary>
/// Calculates the compact control-surface zones without depending on WPF
/// layout. The timeline deliberately receives all remaining width after the
/// protected station, signal, and transport zones have been allocated.
/// </summary>
public readonly record struct CompactNowPlayingLayout(
    double StationLeft,
    double StationWidth,
    double TrackLeft,
    double TrackWidth,
    double VisualizerLeft,
    double VisualizerWidth,
    double TransportLeft,
    double TransportWidth,
    double TimelineLeft,
    double TimelineWidth,
    bool ShowSecondaryMetadata)
{
    public static CompactNowPlayingLayout Create(double surfaceWidth)
    {
        var width = double.IsFinite(surfaceWidth) ? Math.Max(540, surfaceWidth) : 1100;
        var compact = width < 1000;
        var stationLeft = compact ? 12d : 14d;
        var stationWidth = compact ? Math.Clamp(width * .16, 112, 146) : 164d;
        var stationGap = compact ? 14d : 16d;
        var trackLeft = stationLeft + stationWidth + stationGap;
        var visualizerWidth = compact ? Math.Clamp(width * .115, 84, 104) : 132d;
        var beforeVisualizer = compact ? 16d : 24d;
        var transportWidth = compact ? 124d : 140d;
        var afterVisualizer = compact ? 18d : 24d;
        var beforeTimeline = compact ? 24d : 34d;
        var rightInset = compact ? 14d : 16d;
        var desiredTimeline = compact ? 132d : 190d;
        var trackBudget = width - trackLeft - beforeVisualizer - visualizerWidth - afterVisualizer -
            transportWidth - beforeTimeline - rightInset - desiredTimeline;
        var trackWidth = Math.Clamp(trackBudget, compact ? 100d : 170d, compact ? 220d : 300d);
        var visualizerLeft = trackLeft + trackWidth + beforeVisualizer;
        var transportLeft = visualizerLeft + visualizerWidth + afterVisualizer;
        var timelineLeft = transportLeft + transportWidth + beforeTimeline;
        var timelineWidth = Math.Max(36, width - timelineLeft - rightInset);

        return new(stationLeft, stationWidth, trackLeft, trackWidth, visualizerLeft,
            visualizerWidth, transportLeft, transportWidth, timelineLeft, timelineWidth,
            width >= 930);
    }
}
