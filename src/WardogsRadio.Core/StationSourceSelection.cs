namespace WardogsRadio.Core;

/// <summary>Maps the editor's two MPV presentations to one persisted playback provider.</summary>
public static class StationSourceSelection
{
    public const string LocalMpv = "mpv-local";
    public const string LinkMpv = "mpv-link";

    public static string ProviderId(string selection) => selection is LocalMpv or LinkMpv ? "mpv" : selection;
    public static bool IsLocal(string selection) => selection == LocalMpv;
    public static string ForSourceType(bool local, string selectedProvider) => local ? LocalMpv : selectedProvider == LocalMpv ? LinkMpv : selectedProvider;
    public static string ForStation(string providerId, bool hasLocalSource) => providerId == "mpv" ? hasLocalSource ? LocalMpv : LinkMpv : providerId;
}
