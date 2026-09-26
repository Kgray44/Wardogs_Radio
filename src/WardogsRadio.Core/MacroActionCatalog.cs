namespace WardogsRadio.Core;

public enum MacroActionParameter { None, Station, Decibels, DurationMilliseconds, Seconds, ApplicationPath, Page }
public sealed record MacroActionDefinition(ActionKind Kind, string Category, string Name, MacroActionParameter Parameter, string Description)
{
    public override string ToString() => Name;
}

public static class MacroActionCatalog
{
    public static IReadOnlyList<MacroActionDefinition> All { get; } =
    [
        new(ActionKind.ActivateStation, "Playback", "Activate Station", MacroActionParameter.Station, "Tune to a station."),
        new(ActionKind.PlayStation, "Playback", "Play Station", MacroActionParameter.Station, "Start a station's player."),
        new(ActionKind.PauseStation, "Playback", "Pause Station", MacroActionParameter.Station, "Pause a station's player."),
        new(ActionKind.ToggleStation, "Playback", "Toggle Station", MacroActionParameter.Station, "Alternate play and pause."),
        new(ActionKind.PlayCurrentStation, "Playback", "Play Current Station", MacroActionParameter.None, "Start the station already selected, without tuning to another station."),
        new(ActionKind.PauseCurrentStation, "Playback", "Pause Current Station", MacroActionParameter.None, "Pause the station already selected, without tuning to another station."),
        new(ActionKind.CycleStations, "Playback", "Cycle Stations", MacroActionParameter.None, "Tune the next enabled station in preset order; wrap to the first after the last."),
        new(ActionKind.Next, "Playback", "Next Track", MacroActionParameter.None, "Move to the next track."),
        new(ActionKind.Previous, "Playback", "Previous Track", MacroActionParameter.None, "Restart the current track after five seconds; otherwise move to the previous track."),
        new(ActionKind.Seek, "Playback", "Seek", MacroActionParameter.Seconds, "Seek to a time in the active track."),
        new(ActionKind.SetStationGain, "Audio", "Set Station Gain", MacroActionParameter.Decibels, "Change the chosen station's gain."),
        new(ActionKind.SetMasterGain, "Audio", "Set Both Master Music Levels", MacroActionParameter.Decibels, "Change headset and game master levels together; can later be restored."),
        new(ActionKind.RestorePreviousMasterGain, "Audio", "Restore Both Master Levels", MacroActionParameter.None, "Restore both levels from before this macro changed them."),
        new(ActionKind.SetHeadsetMasterGain, "Audio", "Set Headset Master Level", MacroActionParameter.Decibels, "Change only the music level you hear in your headset."),
        new(ActionKind.SetGameMasterGain, "Audio", "Set Game Master Level", MacroActionParameter.Decibels, "Change only the music level sent toward game voice."),
        new(ActionKind.RestorePreviousHeadsetMasterGain, "Audio", "Restore Headset Master Level", MacroActionParameter.None, "Restore the headset level saved by this macro."),
        new(ActionKind.RestorePreviousGameMasterGain, "Audio", "Restore Game Master Level", MacroActionParameter.None, "Restore the game level saved by this macro."),
        new(ActionKind.RestorePreviousStationGain, "Audio", "Restore Previous Station Gain", MacroActionParameter.Station, "Restore the chosen station's gain."),
        new(ActionKind.MuteAll, "Audio", "Mute Music", MacroActionParameter.None, "Mute the music output."),
        new(ActionKind.RestoreMusic, "Audio", "Restore Music", MacroActionParameter.None, "Restore music after muting."),
        new(ActionKind.ToggleGameRoute, "Audio", "Toggle Game Broadcast", MacroActionParameter.None, "Switch music on or off on the game bus."),
        new(ActionKind.EnableGameRoute, "Audio", "Enable Game Broadcast", MacroActionParameter.None, "Send music to the game bus."),
        new(ActionKind.DisableGameRoute, "Audio", "Disable Game Broadcast", MacroActionParameter.None, "Stop music on the game bus."),
        new(ActionKind.RestorePreviousBroadcastState, "Audio", "Restore Previous Broadcast State", MacroActionParameter.None, "Restore the game route's earlier state."),
        new(ActionKind.ToggleMonitorRoute, "Audio", "Toggle Monitor", MacroActionParameter.None, "Switch local monitoring on or off."),
        new(ActionKind.EnableMonitorRoute, "Audio", "Enable Monitor", MacroActionParameter.None, "Enable local music monitoring."),
        new(ActionKind.DisableMonitorRoute, "Audio", "Disable Monitor", MacroActionParameter.None, "Disable local music monitoring."),
        new(ActionKind.RestorePreviousMonitorState, "Audio", "Restore Previous Monitor State", MacroActionParameter.None, "Restore the monitor route's earlier state."),
        new(ActionKind.FadeToStation, "Transition", "Fade To Station", MacroActionParameter.Station, "Crossfade to another station."),
        new(ActionKind.SetTransitionDuration, "Transition", "Set Transition Duration", MacroActionParameter.DurationMilliseconds, "Set the next transition duration."),
        new(ActionKind.Delay, "Transition", "Delay / Wait", MacroActionParameter.DurationMilliseconds, "Wait before the next action."),
        new(ActionKind.SetPlayerMode, "Behavior", "Set Player Mode", MacroActionParameter.None, "Pause stations when tuning away."),
        new(ActionKind.SetRadioMode, "Behavior", "Set Radio Mode", MacroActionParameter.None, "Use continuous station timelines where supported."),
        new(ActionKind.Duck, "Behavior", "Activate Ducking Profile", MacroActionParameter.Decibels, "Set a temporary comms level on both music outputs; pair with Restore Both Master Levels on release."),
        new(ActionKind.LaunchApplication, "Utility", "Launch Application", MacroActionParameter.ApplicationPath, "Launch a configured program."),
        new(ActionKind.OpenVoicemeeter, "Utility", "Open Voicemeeter", MacroActionParameter.None, "Bring Voicemeeter forward."),
        new(ActionKind.OpenPage, "Utility", "Open WARDOGS Radio Page", MacroActionParameter.Page, "Navigate to a page in this app.")
    ];

    public static MacroActionDefinition Get(ActionKind kind) => All.First(x => x.Kind == kind);
    public static bool RequiresStation(ActionKind kind) => Get(kind).Parameter == MacroActionParameter.Station || kind == ActionKind.SetStationGain;
    public static bool RequiresGain(ActionKind kind) => Get(kind).Parameter == MacroActionParameter.Decibels;
    public static bool HasDuration(ActionKind kind) => Get(kind).Parameter == MacroActionParameter.DurationMilliseconds || kind == ActionKind.FadeToStation;
    public static double DecibelsToGain(double decibels) => Math.Clamp(Math.Pow(10, decibels / 20), 0, 1);
    public static string Summarize(RadioAction action, IReadOnlyList<Station> stations)
    {
        var definition = Get(action.Kind);
        var target = stations.FirstOrDefault(x => x.Id == action.StationId)?.Name ?? "Choose station";
        if (action.Kind == ActionKind.SetStationGain)
            return $"{definition.Name} · {target} · {(action.Value is { } stationDb ? $"{stationDb:0.#} dB" : "Choose gain")}";
        if (action.Kind == ActionKind.FadeToStation)
            return $"{definition.Name} · {target} · {(action.DelayMilliseconds == 0 ? "normal duration" : $"{action.DelayMilliseconds} ms")} · {action.Curve switch { TransitionCurve.EqualPower => "Equal power", TransitionCurve.Smoothstep => "Smoothstep", _ => "Linear" }}";
        return definition.Parameter switch
        {
            MacroActionParameter.Station => $"{definition.Name} · {target}",
            MacroActionParameter.Decibels => $"{definition.Name} · {(action.Value is { } db ? $"{db:0.#} dB" : "Choose gain")}",
            MacroActionParameter.DurationMilliseconds => $"{definition.Name} · {action.DelayMilliseconds} ms",
            MacroActionParameter.Seconds => $"{definition.Name} · {action.Value?.ToString("0.#") ?? "Choose time"} s",
            MacroActionParameter.Page or MacroActionParameter.ApplicationPath => $"{definition.Name} · {action.Argument ?? "Choose target"}",
            _ => definition.Name
        };
    }
}
