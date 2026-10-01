using System.IO;
using System.Windows;
using WardogsRadio.Core;
using WardogsRadio.Playback;
using WardogsRadio.Voicemeeter;

namespace WardogsRadio.App;

public partial class MainWindow
{
    CancellationTokenSource? _gameFeedTestCancellation;
    SetupVisitState? _setupVisit;
    bool _setupConnectInProgress;
    bool _setupVisitRecoveryBlocked;

    sealed record SetupAudioConfiguration(
        string? MicrophoneDeviceId, string? MonitorDeviceId, string? MonitorDeviceName, int? MicrophoneStripIndex,
        int? MusicStripIndex, string? MpvAudioDeviceName, string? MpvAudioDeviceEndpointId, string? GameMpvAudioDeviceName,
        int? AutoMicrophoneStrip, string? AutoMicrophonePreviousDeviceName,
        string? AutoMicrophonePreviousDriver, string? AutoMicrophoneAppliedDeviceName,
        bool? AutoMicrophonePreviousA1, bool? AutoMicrophonePreviousB1,
        int? AutoMicrophonePreviousStripIndex, int? AutoMusicRouteStrip,
        bool? AutoMusicPreviousA1, bool? AutoMusicPreviousB1,
        string? AutoMusicPreviousHeadsetDeviceName, string? AutoMusicPreviousGameDeviceName,
        int? AutoMusicPreviousStripIndex, double MicrophoneVolume, bool MicrophoneVolumeInitialized,
        SetupVerification? Verification, bool SetupComplete)
    {
        public static SetupAudioConfiguration Capture(AppConfiguration c) => new(
            c.MicrophoneDeviceId, c.MonitorDeviceId, c.MonitorDeviceName, c.MicrophoneStripIndex, c.MusicStripIndex,
            c.MpvAudioDeviceName, c.MpvAudioDeviceEndpointId, c.GameMpvAudioDeviceName, c.AutoMicrophoneStrip,
            c.AutoMicrophonePreviousDeviceName, c.AutoMicrophonePreviousDriver,
            c.AutoMicrophoneAppliedDeviceName, c.AutoMicrophonePreviousA1, c.AutoMicrophonePreviousB1,
            c.AutoMicrophonePreviousStripIndex, c.AutoMusicRouteStrip, c.AutoMusicPreviousA1,
            c.AutoMusicPreviousB1, c.AutoMusicPreviousHeadsetDeviceName,
            c.AutoMusicPreviousGameDeviceName, c.AutoMusicPreviousStripIndex,
            c.MicrophoneVolume, c.MicrophoneVolumeInitialized, c.SetupVerification, c.SetupComplete);

        public void Restore(AppConfiguration c)
        {
            c.MicrophoneDeviceId = MicrophoneDeviceId;
            c.MonitorDeviceId = MonitorDeviceId;
            c.MonitorDeviceName = MonitorDeviceName;
            c.MicrophoneStripIndex = MicrophoneStripIndex;
            c.MusicStripIndex = MusicStripIndex;
            c.MpvAudioDeviceName = MpvAudioDeviceName;
            c.MpvAudioDeviceEndpointId = MpvAudioDeviceEndpointId;
            c.GameMpvAudioDeviceName = GameMpvAudioDeviceName;
            c.AutoMicrophoneStrip = AutoMicrophoneStrip;
            c.AutoMicrophonePreviousDeviceName = AutoMicrophonePreviousDeviceName;
            c.AutoMicrophonePreviousDriver = AutoMicrophonePreviousDriver;
            c.AutoMicrophoneAppliedDeviceName = AutoMicrophoneAppliedDeviceName;
            c.AutoMicrophonePreviousA1 = AutoMicrophonePreviousA1;
            c.AutoMicrophonePreviousB1 = AutoMicrophonePreviousB1;
            c.AutoMicrophonePreviousStripIndex = AutoMicrophonePreviousStripIndex;
            c.AutoMusicRouteStrip = AutoMusicRouteStrip;
            c.AutoMusicPreviousA1 = AutoMusicPreviousA1;
            c.AutoMusicPreviousB1 = AutoMusicPreviousB1;
            c.AutoMusicPreviousHeadsetDeviceName = AutoMusicPreviousHeadsetDeviceName;
            c.AutoMusicPreviousGameDeviceName = AutoMusicPreviousGameDeviceName;
            c.AutoMusicPreviousStripIndex = AutoMusicPreviousStripIndex;
            c.MicrophoneVolume = MicrophoneVolume;
            c.MicrophoneVolumeInitialized = MicrophoneVolumeInitialized;
            c.SetupVerification = Verification;
            c.SetupComplete = SetupComplete;
        }
    }

    sealed record SetupFloatChange(float Prior, float Applied);
    sealed record SetupDeviceChange(string Prior, string Applied, string Driver);
    sealed record SetupVisitDiskState(int Version, SetupAudioConfiguration Original,
        Dictionary<string, SetupFloatChange> Routes, Dictionary<int, SetupDeviceChange> Devices);

    sealed class SetupVisitState(SetupAudioConfiguration original, Action save)
    {
        public SetupAudioConfiguration Original { get; } = original;
        public Dictionary<string, SetupFloatChange> Routes { get; } = [];
        public Dictionary<int, SetupDeviceChange> Devices { get; } = [];
        public void RecordFloat(string parameter, float prior, float applied)
        {
            Routes[parameter] = new(Routes.TryGetValue(parameter, out var earlier) ? earlier.Prior : prior, applied);
            save();
        }
        public void RecordRoute(int strip, string bus, bool prior, bool applied)
        {
            var key = $"Strip[{strip}].{bus}";
            RecordFloat(key, prior ? 1f : 0f, applied ? 1f : 0f);
        }
        public void RecordDevice(int strip, string prior, string applied, string driver)
        {
            Devices[strip] = Devices.TryGetValue(strip, out var earlier)
                ? new(earlier.Prior, applied, earlier.Driver) : new(prior, applied, driver);
            save();
        }
    }

    string SetupVisitPath => Path.Combine(Path.GetDirectoryName(_store.Path)!, "setup-visit-recovery.json");
    DurableJsonCheckpoint<SetupVisitDiskState> SetupVisitCheckpoint => new(SetupVisitPath);

    void SaveSetupVisit()
    {
        if (_setupVisit is not { } visit) return;
        SetupVisitCheckpoint.Save(new SetupVisitDiskState(1, visit.Original, visit.Routes, visit.Devices));
    }

    void ClearSetupVisit()
    {
        SetupVisitCheckpoint.Clear();
        _setupVisit = null;
        _setupHasUncommittedChanges = false;
    }

    void BeginSetupVisit()
    {
        _setupVisit = new(SetupAudioConfiguration.Capture(_config), SaveSetupVisit);
        SaveSetupVisit();
    }

    async Task<bool> CommitSetupLeasesAsync()
    {
        foreach (var lease in _bridge.OwnedDevices.Where(x => x.Owner == "automatic microphone setup").ToArray())
        {
            var result = await _bridge.CommitDeviceAsync(lease);
            if (!result.Success) { Footer.Text = "SETUP OWNERSHIP NEEDS ATTENTION · " + result.Detail; return false; }
        }
        foreach (var lease in _bridge.Snapshot.OwnedRoutes.Where(x =>
            x.Owner is "automatic microphone setup" or "automatic music setup" or "setup").ToArray())
        {
            var result = await _bridge.CommitRouteAsync(lease);
            if (!result.Success) { Footer.Text = "SETUP OWNERSHIP NEEDS ATTENTION · " + result.Detail; return false; }
            _setupRouteLeases.Remove(lease);
        }
        return true;
    }

    bool TryLoadSetupVisit(out string issue)
    {
        issue = "";
        if (!SetupVisitCheckpoint.Exists) return false;
        try
        {
            var saved = SetupVisitCheckpoint.Load();
            if (saved is not { Version: 1, Original: not null, Routes: not null, Devices: not null } ||
                saved.Routes.Any(x => string.IsNullOrWhiteSpace(x.Key) || x.Value is null ||
                    !float.IsFinite(x.Value.Prior) || !float.IsFinite(x.Value.Applied)) ||
                saved.Devices.Any(x => x.Key is < 0 or > 2 || x.Value is null ||
                    string.IsNullOrWhiteSpace(x.Value.Applied) || x.Value.Driver is not ("mme" or "wdm" or "ks")))
                throw new InvalidOperationException("The saved Setup checkpoint is invalid.");
            if (saved.Routes.Count == 0 && saved.Devices.Count == 0 &&
                saved.Original == SetupAudioConfiguration.Capture(_config))
            {
                SetupVisitCheckpoint.Clear();
                return false;
            }
            _setupVisit = new(saved.Original, SaveSetupVisit);
            foreach (var entry in saved.Routes) _setupVisit.Routes.Add(entry.Key, entry.Value);
            foreach (var entry in saved.Devices) _setupVisit.Devices.Add(entry.Key, entry.Value);
            _setupHasUncommittedChanges = true;
            return true;
        }
        catch (Exception error)
        {
            issue = "UNFINISHED AUDIO SETUP NEEDS ATTENTION · " + error.Message;
            return false;
        }
    }

    async Task<bool> UndoSetupVisitAsync(bool preserveListeningSelection = false)
    {
        if (_setupVisit is not { } visit) return true;
        var requestedListening = (_config.MonitorDeviceId, _config.MonitorDeviceName,
            _config.MpvAudioDeviceName, _config.MpvAudioDeviceEndpointId);
        foreach (var lease in _setupRouteLeases.AsEnumerable().Reverse().ToArray())
        {
            var result = await _bridge.ReleaseRouteAsync(lease);
            if (result != AudioLeaseReleaseState.Restored)
            {
                Footer.Text = $"SETUP UNDO NEEDS ATTENTION · {lease.Resource}: {result}. Outside changes were preserved.";
                return false;
            }
            _setupRouteLeases.Remove(lease);
        }
        foreach (var lease in _bridge.Snapshot.OwnedRoutes.Where(lease =>
            lease.Owner is "automatic microphone setup" or "automatic music setup").Reverse().ToArray())
        {
            var result = await _bridge.ReleaseRouteAsync(lease);
            if (result != AudioLeaseReleaseState.Restored)
            { Footer.Text = $"SETUP UNDO NEEDS ATTENTION · {lease.Resource}: {result}."; return false; }
        }
        foreach (var lease in _bridge.OwnedDevices.Where(lease =>
            lease.Owner == "automatic microphone setup").Reverse().ToArray())
        {
            var result = await _bridge.ReleaseDeviceAsync(lease);
            if (result != AudioLeaseReleaseState.Restored)
            { Footer.Text = $"SETUP UNDO NEEDS ATTENTION · {lease.ReadbackResource}: {result}."; return false; }
        }
        foreach (var (resource, states) in visit.Routes.Reverse())
        {
            if (Math.Abs(states.Prior - states.Applied) < .01f) continue;
            var restored = await _bridge.RestoreConfiguredFloatAsync("setup visit", resource, states.Applied, states.Prior);
            if (!restored.Success) { Footer.Text = "SETUP UNDO NEEDS ATTENTION · " + restored.Detail; return false; }
        }
        foreach (var (strip, device) in visit.Devices.Reverse())
        {
            if (!_bridge.TryReadDevice(strip, out var current))
            { Footer.Text = "SETUP UNDO NEEDS ATTENTION · Could not read microphone assignment."; return false; }
            if (VoicemeeterDeviceIdentity.MatchesAssignment(current, device.Prior, device.Driver)) continue;
            if (!VoicemeeterDeviceIdentity.MatchesAssignment(current, device.Applied, "wdm"))
            { Footer.Text = "SETUP UNDO NEEDS ATTENTION · Microphone changed outside WARDOGS; it was left untouched."; return false; }
            var restored = await _bridge.SetDeviceAsync("setup visit undo", $"Strip[{strip}].device.{device.Driver}",
                $"Strip[{strip}].device.name", device.Prior);
            if (!restored.Success) { Footer.Text = "SETUP UNDO NEEDS ATTENTION · " + restored.Detail; return false; }
        }
        if (!preserveListeningSelection && _config.MpvAudioDeviceName != visit.Original.MpvAudioDeviceName)
        {
            await StopGameOutputAsync();
            if (_mpvProvider is not null)
                try { await _mpvProvider.SetAudioDeviceAsync(visit.Original.MpvAudioDeviceName ?? "auto"); }
                catch (Exception error) { Footer.Text = "SETUP UNDO NEEDS ATTENTION · Headphone output: " + error.Message; return false; }
        }
        visit.Original.Restore(_config);
        if (preserveListeningSelection)
        {
            _config.MonitorDeviceId = requestedListening.MonitorDeviceId;
            _config.MonitorDeviceName = requestedListening.MonitorDeviceName;
            _config.MpvAudioDeviceName = requestedListening.MpvAudioDeviceName;
            _config.MpvAudioDeviceEndpointId = requestedListening.MpvAudioDeviceEndpointId;
        }
        ShowMicrophoneVolume(_config.MicrophoneVolume);
        _sawMicSignal = _sawMusicSignal = _sawGameSignal = _sawGameEndpointSignal = _sawMonitorSignal = false;
        SetupHeardMusic.IsChecked = false;
        SetupGameHeard.IsChecked = false;
        try { await _store.SaveAsync(_config); }
        catch (Exception error) { Footer.Text = "SETUP UNDO NEEDS ATTENTION · Configuration save: " + error.Message; return false; }
        await LoadMpvOutputsAsync();
        await LoadAudioEndpointsAsync();
        RefreshSetupWizard();
        ClearSetupVisit();
        if (SetupView.Visibility == Visibility.Visible) BeginSetupVisit();
        return true;
    }

    async Task ResolveInterruptedSetupAsync()
    {
        if (_setupVisit is null) return;
        var choice = RadioDialogWindow.ChooseThree(this, "Unfinished audio setup",
            "WARDOGS found audio changes from an interrupted Setup visit. Continue Setup to check the route again, keep the saved choices, or undo the changes it can still identify. Your current audio state will be checked before any restoration.",
            "CONTINUE SETUP", "KEEP CHANGES", "UNDO CHANGES");
        if (choice == 0)
        {
            Setup_Click(this, new RoutedEventArgs());
            Footer.Text = "UNFINISHED SETUP · Check your devices and press Connect again if a route was restored after the interruption.";
        }
        else if (choice == 1)
        {
            if (!_bridge.RecoveryReady)
            {
                Footer.Text = "UNFINISHED SETUP NEEDS ATTENTION · Audio Bridge recovery is unresolved. Open Diagnostics before keeping these choices.";
                return;
            }
            ClearSetupVisit();
            PublishReadiness(CaptureReadiness());
            Footer.Text = "SETUP CHOICES KEPT · Check the current route before marking Setup verified.";
        }
        else if (choice == 2)
        {
            if (!await UndoSetupVisitAsync()) return;
            Footer.Text = "UNFINISHED SETUP UNDONE · Prior saved choices restored where WARDOGS still owned them.";
        }
        else Footer.Text = "UNFINISHED SETUP · Open Setup & Repair to continue, keep, or undo the saved changes.";
    }

    async void UndoWizard_Click(object sender, RoutedEventArgs e) => await UndoSetupVisitAsync();

    async void SetupAutoConfigure_Click(object sender, RoutedEventArgs e)
    {
        if (_setupConnectInProgress) return;
        _setupConnectInProgress = true;
        SetupConnectButton.IsEnabled = false;
        SetupConnectButton.Content = "CONNECTING…";
        SetupConnectFeedback.Text = "Connecting your microphone, listening output, and game feed…";
        try
        {
        if (SetupMicrophoneBox.SelectedItem is not WindowsAudioEndpoint microphone ||
            SetupMonitorBox.SelectedItem is not WindowsAudioEndpoint headphones)
        {
            SetupConnectFeedback.Text = "Choose a microphone and listening output to continue.";
            Footer.Text = "CHOOSE YOUR MICROPHONE AND HEADPHONES FIRST.";
            return;
        }
        var bridge = await _bridge.EnsureReadyAsync();
        if (!bridge.Success) { SetupConnectFeedback.Text = "Audio Bridge needs attention: " + bridge.Detail; Footer.Text = "AUDIO BRIDGE NEEDS ATTENTION · " + bridge.Detail; return; }
        var summary = $"WARDOGS will connect {microphone.Name} and radio music to game voice. " +
            $"You will hear the radio through {headphones.Name}. " +
            $"In your game, select Voicemeeter Out {_config.GameBus} as the microphone. Apply these changes?";
        if (!RadioDialogWindow.Confirm(this, "Connect your radio to game voice?", summary, "APPLY ROUTING"))
        { SetupConnectFeedback.Text = "No audio changes were applied."; return; }
        var priorMonitor = _config.MonitorDeviceId;
        if (!await ConfigureAutomaticMicrophoneAsync(microphone))
        {
            SetupConnectFeedback.Text = "Microphone connection failed. Previous changes are being restored.";
            if (_setupHasUncommittedChanges && !await UndoSetupVisitAsync(preserveListeningSelection: true))
                Footer.Text = "AUDIO SETUP NEEDS ATTENTION · Microphone setup failed and prior changes need review.";
            return;
        }
        if (!await ConfigureAutomaticMusicAsync())
        {
            SetupConnectFeedback.Text = "Radio game feed connection failed. Previous changes are being restored.";
            if (!await UndoSetupVisitAsync(preserveListeningSelection: true))
                Footer.Text = "AUDIO SETUP NEEDS ATTENTION · Music setup failed and the setup visit could not be fully undone. Open Diagnostics.";
            return;
        }
        try
        {
            if (!await SetListeningOutputAsync(headphones))
            {
                SetupConnectFeedback.Text = "Radio routing was applied, but the selected listening output needs playback repair. Your choice is saved.";
                PublishReadiness(CaptureReadiness());
                return;
            }
            if (priorMonitor != headphones.Id) _setupHasUncommittedChanges = true;
            _loadingAudioRouteControls = true;
            MicrophoneBox.SelectedItem = (MicrophoneBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == microphone.Id);
            _loadingAudioRouteControls = false;
            Footer.Text = "RECOMMENDED AUDIO ROUTING APPLIED · Run the live checks to verify it.";
            SetupConnectFeedback.Text = "✓ Radio connected. Continue to test your sound.";
            RefreshSetupWizard();
            PublishReadiness(CaptureReadiness());
        }
        catch (Exception error)
        {
            SetupConnectFeedback.Text = "Audio setup did not finish: " + error.Message;
            if (!await UndoSetupVisitAsync(preserveListeningSelection: true))
                Footer.Text = "AUDIO SETUP NEEDS ATTENTION · Save failed and route restoration needs review: " + error.Message;
            else Footer.Text = "AUDIO SETUP DID NOT SAVE · This setup visit was restored: " + error.Message;
        }
        finally { _loadingAudioRouteControls = false; }
        }
        finally
        {
            _setupConnectInProgress = false;
            SetupConnectButton.Content = "CONNECT MY RADIO";
            SetupConnectButton.IsEnabled = true;
        }
    }

    void SetupCopyGameOutputName_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(_gameOutputEndpointName ?? "Voicemeeter Out B1");
        SetupConnectFeedback.Text = "✓ Game voice input name copied.";
    }

    async void SetupPlayGameFeedTest_Click(object sender, RoutedEventArgs e)
    {
        if (_gameFeedTestCancellation is not null) return;
        if (_active?.Runtime.WasPlaying == true)
        {
            SetupGameFeedTestState.Text = "Pause the current station before running the built-in test. Your station and levels will remain unchanged.";
            return;
        }
        if (_config.GameMpvAudioDeviceName is not { } configuredOutput || _config.MusicStripIndex is not { } strip ||
            string.IsNullOrWhiteSpace(_gameOutputEndpointId))
        {
            SetupGameFeedTestState.Text = "Connect the game music output and refresh Windows audio devices first.";
            return;
        }
        var endpoints = await _audioEndpoints.DiscoverAsync();
        var output = endpoints.SingleOrDefault(device => !device.IsInput &&
            AudioDeviceIdentity.SameEndpoint(device.Id, configuredOutput));
        if (output is null)
        {
            SetupGameFeedTestState.Text = "The configured game feed output is missing. Refresh devices or repair the audio route.";
            return;
        }
        using var cancellation = new CancellationTokenSource();
        _gameFeedTestCancellation = cancellation;
        try
        {
            _sawMusicSignal = _sawGameSignal = _sawGameEndpointSignal = false;
            SetupGameFeedTestState.Text = "Playing a quiet test signal through the real game feed…";
            var tone = DeviceTestSound.PlayAsync(output.Id, cancellation.Token);
            var vm = _bridge.Probe();
            var musicSeen = false;
            var mixerSeen = false;
            var windowsSeen = false;
            while (!tone.IsCompleted)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var music = _bridge.ReadStripSignal(strip, vm.Edition);
                var bus = _bridge.ReadBusSignal(_bridge.Topology.GameBus, vm.Edition);
                musicSeen |= music.Available && music.Peak > .005f;
                mixerSeen |= bus.Available && bus.Peak > .005f;
                windowsSeen |= _gameBusEndpointPeakMeter.TryRead(_gameOutputEndpointId, out var peak) && peak > .005f;
                await Task.Delay(40, cancellation.Token);
            }
            await tone;
            _sawMusicSignal |= musicSeen;
            _sawGameSignal |= mixerSeen;
            _sawGameEndpointSignal |= windowsSeen;
            SetupGameFeedTestState.Text = $"Music input {(musicSeen ? "observed" : "not observed")} · B1 mixer {(mixerSeen ? "observed" : "not observed")} · Windows B1 {(windowsSeen ? "observed" : "not observed")}.";
            RefreshSetupWizard();
            RunDiagnostics();
        }
        catch (OperationCanceledException) { SetupGameFeedTestState.Text = "Game feed test stopped."; }
        catch (Exception error) { SetupGameFeedTestState.Text = "Game feed test failed: " + error.Message; }
        finally { _gameFeedTestCancellation = null; }
    }
}
