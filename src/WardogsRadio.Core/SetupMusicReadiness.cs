namespace WardogsRadio.Core;

public static class SetupMusicReadiness
{
    public static IReadOnlyList<string> Missing(Station? station, bool localPlayerReady,
        bool youtubePlayerReady, bool youtubeGameFeedReady, bool gameOutputSelected,
        bool localGamePlayerReady)
    {
        if (station is null) return ["tune a station"];
        return station.ProviderId switch
        {
            "mpv" when !localPlayerReady => ["start the local music player"],
            "mpv" when gameOutputSelected && !localGamePlayerReady => ["connect game music player"],
            "mpv" => [],
            "youtube" when !youtubePlayerReady && !youtubeGameFeedReady =>
                ["start the YouTube player", "connect YouTube music to the game feed"],
            "youtube" when !youtubePlayerReady => ["start the YouTube player"],
            "youtube" when !youtubeGameFeedReady => ["connect YouTube music to the game feed"],
            "youtube" => [],
            _ => ["tune a local or YouTube station with a game feed"]
        };
    }
}
