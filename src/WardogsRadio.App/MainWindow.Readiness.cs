using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using WardogsRadio.Core;
using WardogsRadio.Playback;
using WardogsRadio.Voicemeeter;

namespace WardogsRadio.App;

public partial class MainWindow
{
    sealed record MacroRouteOwnership(AudioRouteLease? Lease);
    SystemReadinessSnapshot? _readiness;
    DateTime _nextReadinessRefresh;
    DateTime _nextBridgeRecovery;
    DateTime _nextGameFeedRecovery;
    bool _audioRecoveryInProgress;
    int _bridgeRecoveryAttempts;
    int _gameFeedRecoveryAttempts;
    Guid? _gameFeedRecoveryStation;
    bool _lastBridgeObservedConnected;
    DateTime _nextEndpointRefresh = DateTime.UtcNow.AddSeconds(15);
    bool _endpointRefreshInProgress;

    void QueueEndpointRefresh()
    {
        if (_closingInProgress || _endpointRefreshInProgress || DateTime.UtcNow < _nextEndpointRefresh) return;
        _nextEndpointRefresh = DateTime.UtcNow.AddSeconds(15);
        _ = RefreshEndpointsInBackgroundAsync();
    }

    async Task RefreshEndpointsInBackgroundAsync()
    {
        _endpointRefreshInProgress = true;
        try
        {
            var previousMic = MicrophoneBox.SelectedItem is WindowsAudioEndpoint;
            var previousHeadphones = MonitorBox.SelectedItem is WindowsAudioEndpoint { IsPresent: true };
            var previousGameOutput = _gameOutputEndpointId is not null;
            if (!await LoadAudioEndpointsAsync(quiet: true, reconcile: false)) return;
            var currentMic = MicrophoneBox.SelectedItem is WindowsAudioEndpoint;
            var currentHeadphones = MonitorBox.SelectedItem is WindowsAudioEndpoint { IsPresent: true };
            var currentGameOutput = _gameOutputEndpointId is not null;
            if (previousMic == currentMic && previousHeadphones == currentHeadphones &&
                previousGameOutput == currentGameOutput) return;
            if (previousHeadphones != currentHeadphones) await LoadMpvOutputsAsync();
            if ((!previousMic && currentMic) || (!previousHeadphones && currentHeadphones) ||
                (!previousGameOutput && currentGameOutput))
            {
                _gameFeedRecoveryAttempts = 0;
                _nextGameFeedRecovery = DateTime.UtcNow;
                Footer.Text = "AUDIO DEVICE RETURNED · Checking the radio and game feed again.";
            }
            else Footer.Text = "AUDIO DEVICE DISCONNECTED · Open Setup & Repair if it does not return.";
            PublishReadiness(CaptureReadiness());
            RunDiagnostics();
        }
        catch (Exception error) { Footer.Text = "AUDIO DEVICE REFRESH NEEDS ATTENTION · " + error.Message; }
        finally { _endpointRefreshInProgress = false; }
    }

    void QueueAudioRecovery(VoicemeeterStatus status)
    {
        if (_closingInProgress || _audioRecoveryInProgress) return;
        var now = DateTime.UtcNow;
        if (status.Connected && !_lastBridgeObservedConnected)
        {
            _bridgeRecoveryAttempts = 0;
            _nextBridgeRecovery = now;
        }
        _lastBridgeObservedConnected = status.Connected;
        if (_active?.Id != _gameFeedRecoveryStation)
        {
            _gameFeedRecoveryStation = _active?.Id;
            _gameFeedRecoveryAttempts = 0;
        }
        if ((!status.Connected || _bridge.Snapshot.Connection == AudioBridgeConnectionState.Recovering) &&
            _bridgeRecoveryAttempts < 3 && now >= _nextBridgeRecovery)
        {
            _nextBridgeRecovery = now.AddSeconds(8);
            _ = RecoverAudioBridgeAsync();
            return;
        }
        if (!status.Connected || now < _nextGameFeedRecovery || _gameFeedRecoveryAttempts >= 3 ||
            _active is not { Runtime.WasPlaying: true } station) return;
        if (station.ProviderId == "youtube" && (_youtubeGameFeed?.Fault is not null ||
            _youtubeGameFeed is null && _youtubePlayerReady && _youtubeGameFeedError is not null))
        {
            _nextGameFeedRecovery = now.AddSeconds(10);
            _ = RecoverGameFeedAsync(station);
        }
        else if (station.ProviderId == "mpv" && _gameMpvProvider is null && _mpvProvider is not null)
        {
            _nextGameFeedRecovery = now.AddSeconds(10);
            _ = RecoverGameFeedAsync(station);
        }
    }

    async Task RecoverAudioBridgeAsync()
    {
        _audioRecoveryInProgress = true;
        try
        {
            _bridgeRecoveryAttempts++;
            var ready = await _bridge.EnsureReadyAsync();
            if (!ready.Success)
            {
                Footer.Text = "AUDIO BRIDGE NEEDS ATTENTION · " + ready.Detail;
                return;
            }
            var recovery = await _bridge.RecoverOwnedRoutesAsync();
            if (!recovery.Success)
            {
                Footer.Text = "AUDIO BRIDGE RECOVERY NEEDS ATTENTION · " + recovery.Detail;
                return;
            }
            _bridgeRecoveryAttempts = 0;
            _signalStatus = null;
            if (!EnsureYouTubeRouteHealthy(out var routeDetail))
                Footer.Text = "YOUTUBE ROUTE NEEDS ATTENTION · " + routeDetail;
            else
            {
                Footer.Text = "AUDIO BRIDGE RECONNECTED · Verify the live headset and game voice signals.";
                if (_active is { ProviderId: "youtube", Runtime.WasPlaying: true } station &&
                    _youtubeGameFeed is null && _youtubePlayerReady)
                    await RecoverGameFeedAsync(station);
            }
            RefreshSetupWizard();
            RunDiagnostics();
        }
        catch (Exception error) { Footer.Text = "AUDIO BRIDGE RECOVERY FAILED · " + error.Message; }
        finally { _audioRecoveryInProgress = false; }
    }

    async Task RecoverGameFeedAsync(Station station)
    {
        _audioRecoveryInProgress = true;
        try
        {
            _gameFeedRecoveryAttempts++;
            await _activationGate.WaitAsync();
            try
            {
                if (!ReferenceEquals(_active, station) || !station.Runtime.WasPlaying) return;
                if (station.ProviderId == "youtube")
                {
                    await StopGameOutputAsync();
                    if (!EnsureYouTubeRouteHealthy(out var routeDetail))
                    {
                        Footer.Text = "YOUTUBE GAME FEED NEEDS ATTENTION · " + routeDetail;
                        return;
                    }
                    await StartYouTubeGameFeedAsync(station);
                    if (_youtubeGameFeed is { Fault: null }) _gameFeedRecoveryAttempts = 0;
                }
                else if (station.ProviderId == "mpv" && _mpvProvider is { } headset)
                {
                    await LoadGameOutputAsync(station, headset);
                    if (_gameMpvProvider is { } game)
                    {
                        await game.PlayAsync();
                        await SyncGameToHeadsetAsync(headset);
                        if (_gameMpvProvider is not null) _gameFeedRecoveryAttempts = 0;
                    }
                }
            }
            finally { _activationGate.Release(); }
            RunDiagnostics();
        }
        catch (Exception error) { Footer.Text = "GAME FEED RECOVERY NEEDS ATTENTION · " + error.Message; }
        finally { _audioRecoveryInProgress = false; }
    }

    SystemReadinessSnapshot CaptureReadiness(VoicemeeterStatus? vmStatus = null)
    {
        var audio = _bridge.Snapshot.Telemetry;
        if (audio is null || audio.MicrophoneStrip != _config.MicrophoneStripIndex ||
            audio.MusicStrip != _config.MusicStripIndex ||
            DateTimeOffset.UtcNow - audio.CapturedAt > TimeSpan.FromSeconds(2))
            audio = _bridge.SampleTelemetry(_config.MicrophoneStripIndex, _config.MusicStripIndex, vmStatus);
        var vm = audio.Status;
        var microphone = MicrophoneBox.SelectedItem as WindowsAudioEndpoint;
        var headphones = ConfiguredListeningEndpoint();
        var library = _config.MusicLibrary ?? new MusicLibrary();
        var sourceIds = library.Sources.Select(source => source.Id).ToHashSet();
        var songIds = library.Songs.Select(song => song.Id).ToHashSet();
        var broken = library.Songs.Count(song => !sourceIds.Contains(song.SourceId)) +
            _config.Profile.Stations.Sum(station => station.PlaylistEntries.Count(entry => !songIds.Contains(entry.SongId)));
        var missing = library.Sources.Count(source => source.ProviderId == "mpv" &&
            !Uri.TryCreate(source.Source, UriKind.Absolute, out _) && !File.Exists(source.Source));
        var micAssigned = microphone is not null && audio.MicrophoneDevice is { } assignedName &&
            VoicemeeterDeviceIdentity.SameName(assignedName, microphone.Name);
        var evidence = new ReadinessEvidence
        {
            MpvAvailable = new MpvLocator().Find(_config.MpvPath) is not null,
            MpvOverrideInvalid = !string.IsNullOrWhiteSpace(_config.MpvPath) && !File.Exists(_config.MpvPath),
            VoicemeeterInstalled = vm.Installed,
            VoicemeeterConnected = vm.Connected && _bridge.Snapshot.Connection == AudioBridgeConnectionState.Connected &&
                _bridge.RecoveryReady,
            VoicemeeterEdition = vm.Edition,
            MicrophonePresent = microphone is not null && microphone.Id == _config.MicrophoneDeviceId,
            HeadphonesPresent = headphones is { IsPresent: true } && headphones.Id == _config.MonitorDeviceId,
            PlayerOutputResolved = _listeningResolution?.Device is { } resolved && resolved.Name == _config.MpvAudioDeviceName &&
                _config.MpvAudioDeviceEndpointId == _config.MonitorDeviceId,
            PlayerOutputSwitched = _listeningPlayerSwitched,
            PlayerOutputSwitchFailed = _listeningConnectionError is not null,
            PlaybackPathVerified = _listeningPlaybackVerified,
            MicrophoneAssigned = micAssigned,
            MicrophoneRoutedToGame = audio.MicrophoneGameRoute == true,
            MusicRoutedToGame = audio.MusicGameRoute == true,
            MusicPlayerTargetsGame = _config.GameMpvAudioDeviceName is not null &&
                (GameMpvOutputBox.ItemsSource as IEnumerable<MpvAudioDevice>)?.Any(device =>
                    device.Name == _config.GameMpvAudioDeviceName && device.Description.Contains("Voicemeeter AUX Input", StringComparison.OrdinalIgnoreCase)) == true,
            GameEndpointPresent = !string.IsNullOrWhiteSpace(_gameOutputEndpointId),
            GameEndpointId = _gameOutputEndpointId,
            MicrophoneObserved = _sawMicSignal,
            ListeningObserved = _sawMonitorSignal,
            ListeningConfirmed = SetupHeardMusic.IsChecked == true,
            MusicObserved = _sawMusicSignal,
            MixerBusObserved = _sawGameSignal,
            WindowsBusObserved = _sawGameEndpointSignal,
            GameReceiveConfirmed = SetupGameHeard.IsChecked == true,
            TelemetryConflict = _outputTelemetryReadiness.HasSustainedConflict,
            ControllerCount = _controllers.Enumerate().Count(),
            YoutubeAvailable = _youtubeReady,
            BrokenLibraryReferences = broken,
            MissingLocalSources = missing
        };
        var snapshot = SystemReadinessService.Evaluate(_config, evidence);
        var attention = new[] {
            (_startupMicrophoneAttention, ReadinessCategory.Microphone, RepairAction.ChooseMicrophone),
            (_startupListeningAttention, ReadinessCategory.Listening, RepairAction.ChooseHeadphones) };
        var checks = attention.Where(item => item.Item1 is not null).Select(item =>
            new ReadinessCheck("startup-" + item.Item2, item.Item2, ReadinessSeverity.NeedsAction, true,
                "Startup audio devices", item.Item1!, Repair: item.Item3)).ToArray();
        return checks.Length == 0 ? snapshot : snapshot with { Overall = OverallReadiness.NeedsAttention,
            Checks = snapshot.Checks.Concat(checks).ToArray() };
    }

    void PublishReadiness(SystemReadinessSnapshot snapshot)
    {
        _readiness = snapshot;
        var label = snapshot.Overall switch
        {
            OverallReadiness.Ready => "READY",
            OverallReadiness.NeedsVerification => "NEEDS VERIFICATION",
            OverallReadiness.NeedsAttention => "NEEDS ATTENTION",
            _ => "OFFLINE"
        };
        HealthText.Text = $"● {label}";
        var quietMicrophone = snapshot.Checks.FirstOrDefault(check => check.Id == "microphone-observation" &&
            check.Severity == ReadinessSeverity.Info)?.Summary;
        HealthText.ToolTip = snapshot.FirstAction?.Summary ?? quietMicrophone ?? "Core audio path is ready.";
        RouteStatus.Text = snapshot.Overall == OverallReadiness.Ready
            ? "GAME VOICE: READY" : "GAME VOICE: " + label;
        RouteStatus.ToolTip = snapshot.FirstAction?.Summary ?? "Core audio path is ready.";
        VmStatus.Text = snapshot.Checks.First(check => check.Id == "voicemeeter").Severity == ReadinessSeverity.Ready
            ? "AUDIO BRIDGE: READY" : "AUDIO BRIDGE: " + label;
        SetMissingDeviceAlert(MissingMicrophoneAlert, !snapshot.MicrophonePresent,
            string.IsNullOrWhiteSpace(_config.MicrophoneDeviceId)
                ? "⚠ MICROPHONE NOT SELECTED · FIX" : "⚠ MICROPHONE DISCONNECTED · FIX");
        SetMissingDeviceAlert(MissingListeningAlert,
            !snapshot.ListeningOutputPresent || !snapshot.ListeningPlayerResolved || snapshot.ListeningPlayerConnectionFailed,
            string.IsNullOrWhiteSpace(_config.MonitorDeviceId) ? "⚠ LISTENING OUTPUT NOT SELECTED · FIX" :
            !snapshot.ListeningOutputPresent ? "⚠ LISTENING OUTPUT DISCONNECTED · FIX" :
            "⚠ LISTENING OUTPUT NEEDS REPAIR · FIX");
        OperationalStatus.Text = snapshot.FirstAction?.Summary ?? quietMicrophone ?? "Core audio path is ready.";
    }

    static void SetMissingDeviceAlert(Button alert, bool missing, string label)
    {
        if (!missing)
        {
            alert.BeginAnimation(UIElement.OpacityProperty, null);
            alert.Opacity = 1;
            alert.Visibility = Visibility.Collapsed;
            return;
        }
        alert.Content = label;
        if (alert.Visibility == Visibility.Visible) return;
        alert.Visibility = Visibility.Visible;
        if (SystemParameters.ClientAreaAnimation)
            alert.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(.68, 1, TimeSpan.FromSeconds(1.2))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            });
    }

    void MissingDeviceAlert_Click(object sender, RoutedEventArgs e) => Setup_Click(sender, e);
}
