using System.Windows;
using WardogsRadio.Core;
using WardogsRadio.Playback;
using WardogsRadio.Voicemeeter;

namespace WardogsRadio.App;

public partial class MainWindow
{
    CancellationTokenSource? _gameFeedTestCancellation;
    SetupVisitState? _setupVisit;

    sealed record SetupAudioConfiguration(
        string? MicrophoneDeviceId, string? MonitorDeviceId, int? MicrophoneStripIndex,
        int? MusicStripIndex, string? MpvAudioDeviceName, string? GameMpvAudioDeviceName,
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
            c.MicrophoneDeviceId, c.MonitorDeviceId, c.MicrophoneStripIndex, c.MusicStripIndex,
            c.MpvAudioDeviceName, c.GameMpvAudioDeviceName, c.AutoMicrophoneStrip,
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
            c.MicrophoneStripIndex = MicrophoneStripIndex;
            c.MusicStripIndex = MusicStripIndex;
            c.MpvAudioDeviceName = MpvAudioDeviceName;
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

    sealed class SetupVisitState(SetupAudioConfiguration original)
    {
        public SetupAudioConfiguration Original { get; } = original;
        public Dictionary<string, (float Prior, float Applied)> Routes { get; } = [];
        public Dictionary<int, (string Prior, string Applied, string Driver)> Devices { get; } = [];
        public void RecordFloat(string parameter, float prior, float applied) =>
            Routes[parameter] = (Routes.TryGetValue(parameter, out var earlier) ? earlier.Prior : prior, applied);
        public void RecordRoute(int strip, string bus, bool prior, bool applied)
        {
            var key = $"Strip[{strip}].{bus}";
            RecordFloat(key, prior ? 1f : 0f, applied ? 1f : 0f);
        }
        public void RecordDevice(int strip, string prior, string applied, string driver)
        {
            Devices[strip] = Devices.TryGetValue(strip, out var earlier)
                ? (earlier.Prior, applied, earlier.Driver) : (prior, applied, driver);
        }
    }

    void BeginSetupVisit() => _setupVisit = new(SetupAudioConfiguration.Capture(_config));

    async Task<bool> UndoSetupVisitAsync()
    {
        if (_setupVisit is not { } visit) return true;
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
            if (current.Equals(device.Prior, StringComparison.OrdinalIgnoreCase)) continue;
            if (!AudioBridgeService.MatchesDeviceName(current, device.Applied))
            { Footer.Text = "SETUP UNDO NEEDS ATTENTION · Microphone changed outside WARDOGS; it was left untouched."; return false; }
            var restored = await _bridge.SetDeviceAsync("setup visit undo", $"Strip[{strip}].device.{device.Driver}",
                $"Strip[{strip}].device.name", device.Prior);
            if (!restored.Success) { Footer.Text = "SETUP UNDO NEEDS ATTENTION · " + restored.Detail; return false; }
        }
        if (_config.MpvAudioDeviceName != visit.Original.MpvAudioDeviceName)
        {
            await StopGameOutputAsync();
            if (_mpvProvider is not null)
                try { await _mpvProvider.SetAudioDeviceAsync(visit.Original.MpvAudioDeviceName ?? "auto"); }
                catch (Exception error) { Footer.Text = "SETUP UNDO NEEDS ATTENTION · Headphone output: " + error.Message; return false; }
        }
        visit.Original.Restore(_config);
        ShowMicrophoneVolume(_config.MicrophoneVolume);
        _sawMicSignal = _sawMusicSignal = _sawGameSignal = _sawGameEndpointSignal = _sawMonitorSignal = false;
        SetupHeardMusic.IsChecked = false;
        SetupGameHeard.IsChecked = false;
        try { await _store.SaveAsync(_config); }
        catch (Exception error) { Footer.Text = "SETUP UNDO NEEDS ATTENTION · Configuration save: " + error.Message; return false; }
        _setupHasUncommittedChanges = false;
        _setupVisit = null;
        await LoadMpvOutputsAsync();
        await LoadAudioEndpointsAsync();
        RefreshSetupWizard();
        if (SetupView.Visibility == Visibility.Visible) BeginSetupVisit();
        return true;
    }

    async void UndoWizard_Click(object sender, RoutedEventArgs e) => await UndoSetupVisitAsync();

    async void SetupAutoConfigure_Click(object sender, RoutedEventArgs e)
    {
        if (SetupMicrophoneBox.SelectedItem is not WindowsAudioEndpoint microphone ||
            SetupMonitorBox.SelectedItem is not WindowsAudioEndpoint headphones)
        {
            Footer.Text = "CHOOSE YOUR MICROPHONE AND HEADPHONES FIRST.";
            return;
        }
        var bridge = await _bridge.EnsureReadyAsync();
        if (!bridge.Success) { Footer.Text = "AUDIO BRIDGE NEEDS ATTENTION · " + bridge.Detail; return; }
        var summary = $"WARDOGS will assign {microphone.Name} to an available physical input, send it to game voice, and turn off microphone self-monitoring. " +
            $"Radio music will use Voicemeeter AUX and B1, with duplicate A1 music off. Local listening will use {headphones.Name}. " +
            "Existing unrelated Voicemeeter routes will be left alone. Apply these changes?";
        if (!RadioDialogWindow.Confirm(this, "Connect your radio to game voice?", summary, "APPLY ROUTING")) return;
        var priorMonitor = _config.MonitorDeviceId;
        if (!await ConfigureAutomaticMicrophoneAsync(microphone))
        {
            if (_setupHasUncommittedChanges && !await UndoSetupVisitAsync())
                Footer.Text = "AUDIO SETUP NEEDS ATTENTION · Microphone setup failed and prior changes need review.";
            return;
        }
        if (!await ConfigureAutomaticMusicAsync())
        {
            if (!await UndoSetupVisitAsync())
                Footer.Text = "AUDIO SETUP NEEDS ATTENTION · Music setup failed and the setup visit could not be fully undone. Open Diagnostics.";
            return;
        }
        try
        {
            _config.MonitorDeviceId = headphones.Id;
            await _store.SaveAsync(_config);
            if (priorMonitor != headphones.Id) _setupHasUncommittedChanges = true;
            _loadingAudioRouteControls = true;
            MicrophoneBox.SelectedItem = (MicrophoneBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == microphone.Id);
            MonitorBox.SelectedItem = (MonitorBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == headphones.Id);
            _loadingAudioRouteControls = false;
            Footer.Text = "RECOMMENDED AUDIO ROUTING APPLIED · Run the live checks to verify it.";
            RefreshSetupWizard();
            PublishReadiness(CaptureReadiness());
        }
        catch (Exception error)
        {
            _config.MonitorDeviceId = priorMonitor;
            if (!await UndoSetupVisitAsync())
                Footer.Text = "AUDIO SETUP NEEDS ATTENTION · Save failed and route restoration needs review: " + error.Message;
            else Footer.Text = "AUDIO SETUP DID NOT SAVE · This setup visit was restored: " + error.Message;
        }
        finally { _loadingAudioRouteControls = false; }
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
