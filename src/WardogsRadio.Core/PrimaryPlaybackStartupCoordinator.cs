namespace WardogsRadio.Core;

/// <summary>
/// Starts required audible playback before an optional secondary delivery path.
/// The secondary task is deliberately observed but never awaited by the caller,
/// so a game-feed/capture startup cannot delay the visible player or headset path.
/// </summary>
public sealed class PrimaryPlaybackStartupCoordinator
{
    public event EventHandler<Exception>? SecondaryStartupFailed;

    public async Task StartAsync(Func<Task> startPrimaryPlayback, Func<Task> startSecondaryFeed)
    {
        ArgumentNullException.ThrowIfNull(startPrimaryPlayback);
        ArgumentNullException.ThrowIfNull(startSecondaryFeed);
        await startPrimaryPlayback();
        _ = ObserveSecondaryStartupAsync(startSecondaryFeed);
    }

    async Task ObserveSecondaryStartupAsync(Func<Task> startSecondaryFeed)
    {
        try { await startSecondaryFeed(); }
        catch (Exception error) { SecondaryStartupFailed?.Invoke(this, error); }
    }
}
