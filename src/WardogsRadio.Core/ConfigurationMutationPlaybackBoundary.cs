namespace WardogsRadio.Core;

/// <summary>
/// Separates a selected station from an actually-playing station when a package
/// import or restore replaces configuration objects. Attached playback resources
/// always need detaching; only real playback needs the explicit stop consent.
/// </summary>
public readonly record struct ConfigurationMutationPlaybackState(
    bool StationSelected,
    bool ActualPlayback,
    bool HeadsetPlayerAttached,
    bool GamePlayerAttached,
    bool YouTubeFeedAttached,
    bool TemporaryYouTubeRouteAttached,
    bool ExternalProviderAttached)
{
    public bool RequiresStopConfirmation => ActualPlayback;
    public bool RequiresDetach => HeadsetPlayerAttached || GamePlayerAttached || YouTubeFeedAttached ||
        TemporaryYouTubeRouteAttached || ExternalProviderAttached || StationSelected;
}
