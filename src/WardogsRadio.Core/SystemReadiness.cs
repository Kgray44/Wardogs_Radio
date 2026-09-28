namespace WardogsRadio.Core;

public enum OverallReadiness { Ready, NeedsVerification, NeedsAttention, Unavailable }
public enum ReadinessSeverity { Ready, Info, Optional, NotTested, NeedsAction, Warning, Error, Unavailable }
public enum ReadinessCategory { Application, Playback, Voicemeeter, Microphone, Listening, GameVoice, Providers, Controls, Library, Configuration }
public enum RepairAction { ConnectVoicemeeter, RefreshDevices, ChooseMicrophone, ChooseHeadphones, FixRoute, UseBundledMpv, OpenAudioRouting, VerifyGameInput, LocateFile }

public sealed record ReadinessCheck(string Id, ReadinessCategory Category, ReadinessSeverity Severity,
    bool BlocksCoreReadiness, string Title, string Summary, string? TechnicalDetail = null,
    RepairAction? Repair = null, bool IsLiveVerification = false);

public sealed record SystemReadinessSnapshot(OverallReadiness Overall, IReadOnlyList<ReadinessCheck> Checks,
    DateTimeOffset CheckedAt)
{
    public bool MicrophonePresent { get; init; }
    public bool ListeningOutputPresent { get; init; }
    public ReadinessCheck? FirstAction => Checks.FirstOrDefault(check => check.BlocksCoreReadiness &&
        check.Severity is ReadinessSeverity.Error or ReadinessSeverity.Unavailable or ReadinessSeverity.NeedsAction or ReadinessSeverity.Warning or ReadinessSeverity.NotTested);
    public int CorePassed => Checks.Count(check => check.BlocksCoreReadiness && check.Severity == ReadinessSeverity.Ready);
    public int CoreTotal => Checks.Count(check => check.BlocksCoreReadiness);
    public int LiveVerificationPassed => Checks.Count(check => check.IsLiveVerification && check.Severity == ReadinessSeverity.Ready);
    public int LiveVerificationTotal => Checks.Count(check => check.IsLiveVerification);
}

// Each part is independently invalidated when its physical identity or mixer assignment changes.
public sealed record SetupVerification
{
    public DateTimeOffset VerifiedAt { get; init; }
    public string? MicrophoneDeviceId { get; init; }
    public string? MonitorDeviceId { get; init; }
    public int? MicrophoneStripIndex { get; init; }
    public int? MusicStripIndex { get; init; }
    public string? GameBus { get; init; }
    public string? GameOutputDeviceId { get; init; }
    public string? VoicemeeterEdition { get; init; }
    public bool MicrophoneObserved { get; init; }
    public bool ListeningConfirmed { get; init; }
    public bool MusicObserved { get; init; }
    public bool MixerBusObserved { get; init; }
    public bool WindowsBusObserved { get; init; }
    public bool GameReceiveConfirmed { get; init; }
}

public sealed record ReadinessEvidence
{
    public bool MpvAvailable { get; init; }
    public bool MpvOverrideInvalid { get; init; }
    public bool VoicemeeterInstalled { get; init; }
    public bool VoicemeeterConnected { get; init; }
    public string? VoicemeeterEdition { get; init; }
    public bool MicrophonePresent { get; init; }
    public bool HeadphonesPresent { get; init; }
    public bool MicrophoneAssigned { get; init; }
    public bool MicrophoneRoutedToGame { get; init; }
    public bool MusicRoutedToGame { get; init; }
    public bool MusicPlayerTargetsGame { get; init; }
    public bool GameEndpointPresent { get; init; }
    public string? GameEndpointId { get; init; }
    public bool MicrophoneObserved { get; init; }
    public bool ListeningObserved { get; init; }
    public bool ListeningConfirmed { get; init; }
    public bool MusicObserved { get; init; }
    public bool MixerBusObserved { get; init; }
    public bool WindowsBusObserved { get; init; }
    public bool GameReceiveConfirmed { get; init; }
    public bool TelemetryConflict { get; init; }
    public int ControllerCount { get; init; }
    public bool YoutubeAvailable { get; init; }
    public int MissingLocalSources { get; init; }
    public int BrokenLibraryReferences { get; init; }
}

public static class SystemReadinessService
{
    public static SystemReadinessSnapshot Evaluate(AppConfiguration config, ReadinessEvidence evidence, DateTimeOffset? checkedAt = null)
    {
        var saved = config.SetupVerification;
        var micVerified = saved is not null && saved.MicrophoneDeviceId == config.MicrophoneDeviceId &&
            saved.MicrophoneStripIndex == config.MicrophoneStripIndex && saved.VoicemeeterEdition == evidence.VoicemeeterEdition;
        var listenVerified = saved is not null && saved.MonitorDeviceId == config.MonitorDeviceId;
        var gameVerified = saved is not null && saved.MusicStripIndex == config.MusicStripIndex &&
            saved.GameBus == config.GameBus && saved.GameOutputDeviceId == evidence.GameEndpointId &&
            saved.VoicemeeterEdition == evidence.VoicemeeterEdition;
        var checks = new List<ReadinessCheck>();
        void Add(string id, ReadinessCategory category, ReadinessSeverity severity, bool core, string title, string summary,
            RepairAction? repair = null, string? technical = null, bool live = false) =>
            checks.Add(new(id, category, severity, core, title, summary, technical, repair, live));

        Add("application", ReadinessCategory.Application, ReadinessSeverity.Ready, true, "WARDOGS Radio", "Application is running.");
        Add("playback", ReadinessCategory.Playback, evidence.MpvAvailable ? ReadinessSeverity.Ready : ReadinessSeverity.Error,
            true, "Playback engine", evidence.MpvAvailable ? "MPV is available." : "The local playback engine could not be found.",
            evidence.MpvOverrideInvalid ? RepairAction.UseBundledMpv : null);
        Add("voicemeeter", ReadinessCategory.Voicemeeter,
            !evidence.VoicemeeterInstalled ? ReadinessSeverity.Unavailable : !evidence.VoicemeeterConnected ? ReadinessSeverity.Error :
            evidence.VoicemeeterEdition != "Banana" ? ReadinessSeverity.Error : ReadinessSeverity.Ready,
            true, "Voicemeeter audio engine", !evidence.VoicemeeterInstalled ? "Voicemeeter Banana is required." :
            !evidence.VoicemeeterConnected ? "Voicemeeter is installed but its audio engine is disconnected." :
            evidence.VoicemeeterEdition != "Banana" ? "Automatic setup currently supports Voicemeeter Banana." : "Voicemeeter Banana is connected.",
            evidence.VoicemeeterInstalled && !evidence.VoicemeeterConnected ? RepairAction.ConnectVoicemeeter : null);
        Add("supported-topology", ReadinessCategory.Configuration,
            config.GameBus == "B1" ? ReadinessSeverity.Ready : ReadinessSeverity.Error,
            true, "Supported game voice bus", config.GameBus == "B1" ?
                "WARDOGS is configured for the supported Banana B1 path." :
                "This configuration requests an unsupported game bus; automatic routing requires B1.",
            config.GameBus == "B1" ? null : RepairAction.OpenAudioRouting);
        Add("microphone", ReadinessCategory.Microphone,
            !evidence.MicrophonePresent || !evidence.MicrophoneAssigned || !evidence.MicrophoneRoutedToGame ? ReadinessSeverity.NeedsAction :
            ReadinessSeverity.Ready,
            true, "Microphone to game voice", !evidence.MicrophonePresent ? "Choose a microphone that is connected now." :
            !evidence.MicrophoneAssigned ? "The microphone is not assigned to the selected audio input." :
            !evidence.MicrophoneRoutedToGame ? "The microphone is not routed to game voice." :
            "Microphone is connected to game voice.",
            !evidence.MicrophonePresent ? RepairAction.ChooseMicrophone : RepairAction.OpenAudioRouting);
        Add("microphone-observation", ReadinessCategory.Microphone,
            evidence.MicrophoneObserved ? ReadinessSeverity.Ready : ReadinessSeverity.Info,
            false, "Microphone signal", evidence.MicrophoneObserved ? "Microphone audio was observed this session." :
            micVerified && saved!.MicrophoneObserved ? "Microphone audio was verified previously; none has been observed this session. Speak to test it if desired." :
            "No microphone audio has been observed this session. Speak to test it if desired.", live: true);
        Add("listening", ReadinessCategory.Listening,
            !evidence.HeadphonesPresent ? ReadinessSeverity.NeedsAction : ReadinessSeverity.Ready,
            true, "Listening output", !evidence.HeadphonesPresent ? "Choose headphones or speakers that are connected now." :
            "The selected listening output is present.",
            !evidence.HeadphonesPresent ? RepairAction.ChooseHeadphones : null);
        Add("listening-confirmation", ReadinessCategory.Listening,
            evidence.ListeningConfirmed ? ReadinessSeverity.Ready : ReadinessSeverity.Info,
            false, "Listening test", evidence.ListeningConfirmed ? "You confirmed hearing the selected output this session." :
            listenVerified && saved!.ListeningConfirmed ? "Listening was confirmed previously. Run the test sound again if desired." :
            "Play the test sound and confirm that you heard it if desired.", live: true);
        Add("game-route", ReadinessCategory.GameVoice,
            !evidence.MusicPlayerTargetsGame || !evidence.MusicRoutedToGame ? ReadinessSeverity.NeedsAction : ReadinessSeverity.Ready,
            true, "Radio feed to game voice", !evidence.MusicPlayerTargetsGame ? "The game music player is not targeting Voicemeeter AUX." :
            !evidence.MusicRoutedToGame ? "Game music is not routed to B1." : "Radio feed is configured for B1.", RepairAction.FixRoute);
        Add("game-music-observed", ReadinessCategory.GameVoice,
            evidence.MusicObserved ? ReadinessSeverity.Ready : ReadinessSeverity.Info,
            false, "Radio feed signal", evidence.MusicObserved ? "Live radio feed signal was detected at the music input." :
            gameVerified && saved!.MusicObserved ? "Radio feed was verified previously; no station is playing now." :
            "No radio feed signal has been observed this session. The built-in test needs no station.", live: true);
        Add("game-output", ReadinessCategory.GameVoice,
            !evidence.GameEndpointPresent ? ReadinessSeverity.NeedsAction : evidence.TelemetryConflict ? ReadinessSeverity.Warning : ReadinessSeverity.Ready,
            true, "Windows B1 output", !evidence.GameEndpointPresent ? "Voicemeeter Out B1 is missing from Windows recording devices." :
            evidence.TelemetryConflict ? "Voicemeeter and Windows B1 meters disagree. Output is not verified." :
            "The game voice output is present and no telemetry conflict is known.",
            !evidence.GameEndpointPresent ? RepairAction.RefreshDevices : null);
        Add("game-output-observation", ReadinessCategory.GameVoice,
            evidence.WindowsBusObserved && evidence.MixerBusObserved ? ReadinessSeverity.Ready : ReadinessSeverity.Info,
            false, "Game voice signal", evidence.WindowsBusObserved && evidence.MixerBusObserved ?
            "Signal reached the mixer and Windows game voice output this session." :
            gameVerified && saved!.WindowsBusObserved && saved.MixerBusObserved ? "Game voice was verified previously; no fresh signal is present." :
            "Run the built-in game feed test to observe game voice without a station.", live: true);
        Add("game-confirmation", ReadinessCategory.GameVoice,
            evidence.GameReceiveConfirmed ? ReadinessSeverity.Ready : ReadinessSeverity.Info,
            false, "Game or voice app reception", evidence.GameReceiveConfirmed ? "You confirmed that your game receives the B1 mix this session." :
            gameVerified && saved!.GameReceiveConfirmed ? "Game reception was confirmed previously." :
            "Select Voicemeeter Out B1 in your game, then confirm reception when convenient.", RepairAction.VerifyGameInput, live: true);
        Add("youtube", ReadinessCategory.Providers, evidence.YoutubeAvailable ? ReadinessSeverity.Info : ReadinessSeverity.Optional,
            false, "YouTube", evidence.YoutubeAvailable ? "Player is available; individual videos still need testing." : "Optional player is unavailable.");
        Add("soundcloud", ReadinessCategory.Providers, ReadinessSeverity.Optional, false, "SoundCloud", "Optional account connection is not configured.");
        Add("apple-music", ReadinessCategory.Providers, ReadinessSeverity.Optional, false, "Apple Music", "Optional account connection is not configured.");
        Add("controllers", ReadinessCategory.Controls, evidence.ControllerCount > 0 ? ReadinessSeverity.Info : ReadinessSeverity.Optional,
            false, "Controllers", evidence.ControllerCount > 0 ? $"{evidence.ControllerCount} controller(s) available." : "No controller is connected; keyboard and normal radio controls remain available.");
        Add("library", ReadinessCategory.Library, evidence.BrokenLibraryReferences + evidence.MissingLocalSources > 0 ? ReadinessSeverity.Warning : ReadinessSeverity.Info,
            false, "Music Library", $"{evidence.BrokenLibraryReferences} broken references; {evidence.MissingLocalSources} missing local files.",
            evidence.MissingLocalSources > 0 ? RepairAction.LocateFile : null);

        var core = checks.Where(check => check.BlocksCoreReadiness).ToList();
        var overall = !evidence.VoicemeeterInstalled ? OverallReadiness.Unavailable :
            core.Any(check => check.Severity is ReadinessSeverity.Error or ReadinessSeverity.NeedsAction or ReadinessSeverity.Warning or ReadinessSeverity.Unavailable) ? OverallReadiness.NeedsAttention :
            core.Any(check => check.Severity == ReadinessSeverity.NotTested) ? OverallReadiness.NeedsVerification : OverallReadiness.Ready;
        return new(overall, checks, checkedAt ?? DateTimeOffset.Now)
        {
            MicrophonePresent = evidence.MicrophonePresent,
            ListeningOutputPresent = evidence.HeadphonesPresent
        };
    }
}
