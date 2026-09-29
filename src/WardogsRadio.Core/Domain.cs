using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WardogsRadio.Core;

public enum PlaybackMode { Player, Radio, RestartTrack }
public enum StationRepeatMode { Off, Playlist, Track }
public enum TransitionCurve { Linear, EqualPower, Smoothstep }
public enum ProviderHealth { Unknown, Ready, Warning, Failed, Unavailable }
public enum ActionKind { ActivateStation, PlayStation, PauseStation, ToggleStation, Next, Previous, FadeToStation, SetStationGain, SetMasterGain, ToggleGameRoute, ToggleMonitorRoute, MuteAll, RestoreMusic, SetPlayerMode, SetRadioMode, Duck, Delay, LaunchApplication, Seek, RestorePreviousMasterGain, RestorePreviousStationGain, RestorePreviousMonitorState, RestorePreviousBroadcastState, EnableGameRoute, DisableGameRoute, EnableMonitorRoute, DisableMonitorRoute, SetTransitionDuration, OpenVoicemeeter, OpenPage, SetHeadsetMasterGain, SetGameMasterGain, RestorePreviousHeadsetMasterGain, RestorePreviousGameMasterGain, CycleStations, PlayCurrentStation, PauseCurrentStation }
public enum MacroActivation { Press, Hold, Toggle, Momentary }
public enum MacroFailurePolicy { StopOnFailure, Continue }
public sealed class Station
{
    public override string ToString() => Name;
    public Guid Id { get; set; } = Guid.NewGuid(); public string Name { get; set; } = "New Station"; public string? Description { get; set; }
    public string Glyph { get; set; } = "◈"; public string IconId { get; set; } = "patrol"; public string AccentColor { get; set; } = "#9FB672"; public bool Enabled { get; set; } = true; public string ProviderId { get; set; } = "mpv";
    public string Source { get; set; } = "";
    // Schema 9 and earlier persisted these station-owned lists. Schema 10 keeps them only as
    // short-lived provider projections; legacy JSON still deserializes through the named fields.
    [JsonIgnore] public List<string> PlaylistFiles { get; set; } = [];
    [JsonIgnore] public List<StationSong> PlaylistSongs { get; set; } = [];
    [JsonPropertyName("PlaylistFiles"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? LegacyPlaylistFiles { get; set; }
    [JsonPropertyName("PlaylistSongs"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<StationSong>? LegacyPlaylistSongs { get; set; }
    public List<StationPlaylistEntry> PlaylistEntries { get; set; } = []; public double Volume { get; set; } = .72; public double GameVolume { get; set; } = .72; public bool Shuffle { get; set; } public int? ShuffleSeed { get; set; } public bool Loop { get; set; } = true;
    public StationRepeatMode? RepeatMode { get; set; }
    [JsonIgnore] public StationRepeatMode EffectiveRepeatMode => RepeatMode ?? (Loop ? StationRepeatMode.Playlist : StationRepeatMode.Off);
    public PlaybackMode? ModeOverride { get; set; } public TimeSpan FadeIn { get; set; } = TimeSpan.FromSeconds(.35); public TimeSpan FadeOut { get; set; } = TimeSpan.FromSeconds(.35);
    public string Tags { get; set; } = ""; public string? Hotkey { get; set; } public string? ControllerBinding { get; set; } public int Order { get; set; }
    public StationRuntimeState Runtime { get; set; } = new();
}
public sealed class StationSong
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Source { get; set; } = "";
    public string Name { get; set; } = "";
    public double StartSeconds { get; set; }
    public double? EndSeconds { get; set; }
}
public sealed class StationRuntimeState
{
    public List<string> Sequence { get; set; } = []; public int SequenceIndex { get; set; } public string? VideoId { get; set; } public double PositionSeconds { get; set; } public double DurationSeconds { get; set; }
    public bool WasPlaying { get; set; } public bool IsOnAir { get; set; } public bool VirtualRunning { get; set; } public DateTimeOffset? VirtualStartUtc { get; set; } public double PlaybackRate { get; set; } = 1;
}
public sealed class RadioMacro : INotifyPropertyChanged
{
    string _name = "New Macro";
    public event PropertyChangedEventHandler? PropertyChanged;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get => _name; set { if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); } }
    public string? Description { get; set; }
    public string Glyph { get; set; } = "◆"; public string IconId { get; set; } = "patrol"; public string AccentColor { get; set; } = "#9FB672";
    public string? Hotkey { get; set; } public List<string> KeyboardBindings { get; set; } = []; public List<string> ControllerBindings { get; set; } = [];
    public bool Enabled { get; set; } = true; public bool ShowOnDashboard { get; set; } = true; public MacroActivation Activation { get; set; } = MacroActivation.Press; public MacroFailurePolicy FailurePolicy { get; set; } = MacroFailurePolicy.StopOnFailure;
    public List<RadioAction> Actions { get; set; } = []; public List<RadioAction> ReleaseActions { get; set; } = []; public List<RadioAction> OffActions { get; set; } = []; public bool ToggleState { get; set; } public int Order { get; set; }
}
public sealed class RadioAction { public ActionKind Kind { get; set; } public Guid? StationId { get; set; } public double? Value { get; set; } public int DelayMilliseconds { get; set; } public TransitionCurve Curve { get; set; } = TransitionCurve.EqualPower; public string? Argument { get; set; } }
public sealed class YouTubeTrackMetadata { public string VideoId { get; set; } = ""; public double DurationSeconds { get; set; } public DateTimeOffset LastConfirmedUtc { get; set; } }
public sealed class RadioProfile { public Guid Id { get; set; } = Guid.NewGuid(); public string Name { get; set; } = "WARDOGS"; public List<Station> Stations { get; set; } = []; public List<RadioMacro> Macros { get; set; } = []; }
public sealed class AppConfiguration
{
    public int SchemaVersion { get; set; } = MusicLibraryService.CurrentSchemaVersion; public bool SetupComplete { get; set; } public SetupVerification? SetupVerification { get; set; } public RadioProfile Profile { get; set; } = Defaults.Profile(); public MusicLibrary MusicLibrary { get; set; } = new();
    public ClipGuardSettings ClipGuard { get; set; } = new();
    public PlaybackMode DefaultPlaybackMode { get; set; } = PlaybackMode.Player; public bool CrossfadeEnabled { get; set; } = true; public double CrossfadeSeconds { get; set; } = .65; public TransitionCurve Curve { get; set; } = TransitionCurve.EqualPower;
    public double MasterVolume { get; set; } = .8; public double GameMasterVolume { get; set; } = .8; public double MicrophoneVolume { get; set; } = 1; public bool MicrophoneVolumeInitialized { get; set; }
    /// <summary>Suppresses decorative Now Playing transitions while retaining all controls and state updates.</summary>
    public bool ReduceMotion { get; set; }
    /// <summary>Local listening history is opt-out and is never sent to a service.</summary>
    public bool ListeningHistoryEnabled { get; set; } = true;
    public Dictionary<string, YouTubeTrackMetadata> YouTubeDurationCache { get; set; } = [];
    public Dictionary<string, double> LocalDurationCache { get; set; } = [];
    public string? MpvPath { get; set; } public string? MpvAudioDeviceName { get; set; } public string? GameMpvAudioDeviceName { get; set; } public string? YtDlpPath { get; set; } public string? MonitorDeviceId { get; set; } public string? MicrophoneDeviceId { get; set; } public string GameBus { get; set; } = "B1";
    public int? MusicStripIndex { get; set; } public int? MicrophoneStripIndex { get; set; }
    public int? AutoMusicRouteStrip { get; set; } public bool? AutoMusicPreviousA1 { get; set; } public bool? AutoMusicPreviousB1 { get; set; }
    public string? AutoMusicPreviousHeadsetDeviceName { get; set; } public string? AutoMusicPreviousGameDeviceName { get; set; } public int? AutoMusicPreviousStripIndex { get; set; }
    public int? AutoMicrophoneStrip { get; set; } public string? AutoMicrophonePreviousDeviceName { get; set; }
    public string? AutoMicrophonePreviousDriver { get; set; } public string? AutoMicrophoneAppliedDeviceName { get; set; }
    public bool? AutoMicrophonePreviousA1 { get; set; } public bool? AutoMicrophonePreviousB1 { get; set; } public int? AutoMicrophonePreviousStripIndex { get; set; }
}
public static class Defaults
{
    public static RadioProfile Profile()
    {
        var cruise = new Station { Name = "Cruise", Description = "Background patrol radio", Glyph = "◌", IconId="patrol", AccentColor="#9FB672", Order = 0, Volume = .68 };
        var combat = new Station { Name = "Combat", Description = "High-intensity channel", Glyph = "▲", IconId="combat", AccentColor="#D68A65", Order = 1, Volume = .80 };
        return new RadioProfile { Stations = [cruise, combat], Macros = [
          new() { Name="Cruise", Glyph="◌", IconId="patrol", Hotkey="F9", KeyboardBindings=["F9"], Order=0, Actions=[new(){Kind=ActionKind.ActivateStation, StationId=cruise.Id}] },
          new() { Name="Combat", Glyph="▲", IconId="combat", AccentColor="#D68A65", Hotkey="F10", KeyboardBindings=["F10"], Order=1, Actions=[new(){Kind=ActionKind.ActivateStation, StationId=combat.Id}] },
          new() { Name="Comms", Glyph="⌁", IconId="antenna", Hotkey="F11", KeyboardBindings=["F11"], Activation=MacroActivation.Hold, Order=2, Actions=[new(){Kind=ActionKind.SetMasterGain, Value=-24}], ReleaseActions=[new(){Kind=ActionKind.RestorePreviousMasterGain}] },
          new() { Name="Emergency", Glyph="!", IconId="combat", AccentColor="#E6B65A", Hotkey="F12", KeyboardBindings=["F12"], Order=3, Actions=[new(){Kind=ActionKind.ActivateStation, StationId=combat.Id}, new(){Kind=ActionKind.SetMasterGain, Value=-6}] }
        ]};
    }
}
public sealed class ConfigurationStore(string root)
{
    public event Action<ConfigurationSaveState>? SaveStateChanged;
    readonly JsonSerializerOptions _json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    readonly SemaphoreSlim _saveGate = new(1, 1);
    public string Path => System.IO.Path.Combine(root, "config.json"); public string BackupPath => System.IO.Path.Combine(root, "config.last-good.json");
    public async Task<AppConfiguration> LoadAsync(CancellationToken ct = default) { Directory.CreateDirectory(root); try { if (!File.Exists(Path)) return new(); await using var s=File.OpenRead(Path); return Normalize(await JsonSerializer.DeserializeAsync<AppConfiguration>(s,_json,ct) ?? new()); } catch { if(File.Exists(BackupPath)){ await using var s=File.OpenRead(BackupPath); return Normalize(await JsonSerializer.DeserializeAsync<AppConfiguration>(s,_json,ct) ?? new()); } return new(); } }
    public async Task SaveAsync(AppConfiguration config, CancellationToken ct = default)
    {
        SaveStateChanged?.Invoke(ConfigurationSaveState.Saving);
        try { await _saveGate.WaitAsync(ct); }
        catch { SaveStateChanged?.Invoke(ConfigurationSaveState.Failed); throw; }
        try
        {
            Directory.CreateDirectory(root);
            // Only callers deliberately writing a historical schema need the old JSON shape.
            // Normal schema-11 saves leave both fields null, so canonical library data is the
            // sole persisted owner of song timing and playlist membership.
            if (config.SchemaVersion < MusicLibraryService.CurrentSchemaVersion)
                foreach (var station in config.Profile.Stations)
                {
                    station.LegacyPlaylistFiles = station.PlaylistFiles;
                    station.LegacyPlaylistSongs = station.PlaylistSongs;
                }
            else
                foreach (var station in config.Profile.Stations)
                {
                    // Accept in-memory station lists from older callers during the transition,
                    // but convert them before their first schema-10 save. Empty stations stay
                    // empty; this never recreates a cue removed from the library.
                    if (station.PlaylistEntries.Count == 0 &&
                        (station.PlaylistSongs.Count > 0 || station.PlaylistFiles.Count > 0))
                        MusicLibraryService.EnsureStationLibrary(config, station);
                    MusicLibraryService.MaterializeStationPlaylist(config, station);
                }
            var tmp = Path + ".new";
            await using (var stream = File.Create(tmp)) await JsonSerializer.SerializeAsync(stream, config, _json, ct);
            if (File.Exists(Path)) File.Copy(Path, BackupPath, true);
            File.Move(tmp, Path, true);
            SaveStateChanged?.Invoke(ConfigurationSaveState.Saved);
        }
        catch
        {
            SaveStateChanged?.Invoke(ConfigurationSaveState.Failed);
            throw;
        }
        finally { _saveGate.Release(); }
    }
    static AppConfiguration Normalize(AppConfiguration config)
    {
        config.Profile ??= Defaults.Profile();
        config.ClipGuard ??= new ClipGuardSettings();
        config.ClipGuard.Normalize();
        config.LocalDurationCache ??= [];
        config.Profile.Stations ??= [];
        foreach (var station in config.Profile.Stations)
        {
            station.PlaylistFiles = station.LegacyPlaylistFiles ?? [];
            station.PlaylistSongs = station.LegacyPlaylistSongs ?? [];
            station.PlaylistEntries ??= [];
            station.RepeatMode ??= station.Loop ? StationRepeatMode.Playlist : StationRepeatMode.Off;
        }
        config.Profile.Macros ??= [];
        if (config.SchemaVersion < 4)
        {
            foreach (var station in config.Profile.Stations)
                if (string.IsNullOrWhiteSpace(station.IconId) || station.IconId == "patrol")
                    station.IconId = LegacyIcon(station.Glyph);
            foreach (var macro in config.Profile.Macros)
                if (string.IsNullOrWhiteSpace(macro.IconId) || macro.IconId == "patrol")
                    macro.IconId = LegacyIcon(macro.Glyph);
        }
        if (config.SchemaVersion < 5)
        {
            foreach (var (stationName, key) in new[] { ("Cruise", "F9"), ("Combat", "F10") })
            {
                var station = config.Profile.Stations.FirstOrDefault(x => x.Name == stationName && x.Hotkey == key);
                var macro = config.Profile.Macros.FirstOrDefault(x => x.Name == stationName &&
                    ((x.KeyboardBindings?.Contains(key, StringComparer.OrdinalIgnoreCase) ?? false) || x.Hotkey == key));
                if (station is not null && macro is not null) station.Hotkey = null;
            }
            foreach (var macro in config.Profile.Macros.Where(x => x.Name == "Comms" && x.Hotkey == "F11" &&
                         x.Actions.Count == 1 && x.Actions[0].Kind == ActionKind.Duck &&
                         (x.ReleaseActions.Count == 0 || x.ReleaseActions.Count == 1 && x.ReleaseActions[0].Kind == ActionKind.RestoreMusic)))
            {
                macro.Activation = MacroActivation.Hold;
                macro.Actions[0] = new RadioAction { Kind = ActionKind.SetMasterGain, Value = -18 };
                macro.ReleaseActions = [new RadioAction { Kind = ActionKind.RestorePreviousMasterGain }];
            }
        }
        foreach (var macro in config.Profile.Macros)
        {
            macro.KeyboardBindings ??= [];
            macro.ControllerBindings ??= [];
            macro.Actions ??= [];
            macro.ReleaseActions ??= [];
            macro.OffActions ??= [];
            if (macro.KeyboardBindings.Count == 0 && !string.IsNullOrWhiteSpace(macro.Hotkey))
                macro.KeyboardBindings.Add(macro.Hotkey);
            if (string.IsNullOrWhiteSpace(macro.AccentColor)) macro.AccentColor = "#9FB672";
            if (string.IsNullOrWhiteSpace(macro.IconId)) macro.IconId = "patrol";
            if (config.SchemaVersion < 6)
                foreach (var action in macro.Actions.Concat(macro.ReleaseActions).Concat(macro.OffActions).Where(x => x.Kind == ActionKind.FadeToStation && x.DelayMilliseconds == 0 && x.Value is > 0 and <= 600))
                {
                    action.DelayMilliseconds = (int)Math.Round(action.Value!.Value * 1000);
                    action.Value = null;
                }
            macro.ToggleState = false;
        }
        // Earlier versions marked a viewed slideshow complete without verifying any signal path.
        if (config.SchemaVersion < 8) config.SetupComplete = false;
        if (config.SchemaVersion < 9)
        {
            config.GameMasterVolume = config.MasterVolume;
            foreach (var station in config.Profile.Stations) station.GameVolume = station.Volume;
        }
        // Clip Guard previously displayed only an informational limiter capability. It never
        // owned a Voicemeeter limiter setting, so migration must not begin changing a user's
        // mixer just because a newer WARDOGS build is launched.
        if (config.SchemaVersion < 12) config.ClipGuard.LimiterEnabled = false;
        MusicLibraryService.NormalizeAndMigrate(config);
        foreach (var station in config.Profile.Stations)
        {
            MusicLibraryService.MaterializeStationPlaylist(config, station);
            station.LegacyPlaylistFiles = null;
            station.LegacyPlaylistSongs = null;
        }
        return config;
    }

    static string LegacyIcon(string? glyph) => glyph switch
    {
        "▲" => "combat",
        "◐" => "night",
        "♫" => "music",
        "⌁" => "antenna",
        "✈" => "aircraft",
        "!" => "lightning",
        _ => "patrol"
    };
}
public enum ConfigurationSaveState { Saving, Saved, Failed }
