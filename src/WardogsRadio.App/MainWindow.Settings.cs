using System.Windows;
using WardogsRadio.Core;
using WardogsRadio.Playback;

namespace WardogsRadio.App;

public partial class MainWindow
{
    WindowsDefaultAudioSnapshot? _startupDefaults;
    string? _startupMicrophoneAttention;
    string? _startupListeningAttention;

    void LoadStartupAudioDeviceControls()
    {
        _loadingPlaybackSettings = true;
        StartupLastUsedMode.IsChecked = _config.StartupAudioDeviceMode == StartupAudioDeviceMode.LastUsed;
        StartupWindowsDefaultsMode.IsChecked = _config.StartupAudioDeviceMode == StartupAudioDeviceMode.WindowsDefaults;
        _loadingPlaybackSettings = false;
    }

    async void StartupAudioDeviceMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingPlaybackSettings || !IsLoaded) return;
        var previous = _config.StartupAudioDeviceMode;
        _config.StartupAudioDeviceMode = StartupWindowsDefaultsMode.IsChecked == true
            ? StartupAudioDeviceMode.WindowsDefaults : StartupAudioDeviceMode.LastUsed;
        try
        {
            await _store.SaveAsync(_config);
            Footer.Text = "STARTUP AUDIO PREFERENCE SAVED · Applies the next time WARDOGS starts.";
        }
        catch (Exception error)
        {
            _config.StartupAudioDeviceMode = previous;
            LoadStartupAudioDeviceControls();
            Footer.Text = "STARTUP AUDIO PREFERENCE COULD NOT BE SAVED · " + error.Message;
        }
    }

    async Task ApplyStartupAudioDevicesAsync()
    {
        if (_config.StartupAudioDeviceMode != StartupAudioDeviceMode.WindowsDefaults || _startupDefaults is null) return;
        var inventory = (MicrophoneBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.ToList() ?? [];
        inventory.AddRange((MonitorBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>) ?? []);
        var plan = StartupAudioDevicePolicy.Plan(_config.StartupAudioDeviceMode, _config.MicrophoneDeviceId,
            _config.MonitorDeviceId, _startupDefaults, inventory);
        _startupMicrophoneAttention = plan.MicrophoneAttention;
        _startupListeningAttention = plan.ListeningAttention;
        if (_setupVisit is not null || _setupVisitRecoveryBlocked || !_bridge.RecoveryReady || _youtubeRouteRecoveryBlocked)
        {
            _startupMicrophoneAttention = "STARTUP AUDIO DEVICES NEED ATTENTION · Repair the interrupted audio transaction first. Previous selections retained.";
            return;
        }
        if (plan.Listening is { } listening)
        {
            var previous = (_config.MonitorDeviceId, _config.MonitorDeviceName, _config.MpvAudioDeviceName,
                _config.MpvAudioDeviceEndpointId, _config.SetupVerification, _config.SetupComplete);
            var resolution = ListeningOutputResolver.Resolve(listening, inventory, _listeningPlayerDevices);
            if (!resolution.Resolved || resolution.Device?.Name == _config.GameMpvAudioDeviceName ||
                !await SetListeningOutputAsync(listening))
            {
                (_config.MonitorDeviceId, _config.MonitorDeviceName, _config.MpvAudioDeviceName,
                    _config.MpvAudioDeviceEndpointId, _config.SetupVerification, _config.SetupComplete) = previous;
                await _store.SaveAsync(_config);
                SyncListeningSelectors();
                await ReconcileListeningOutputAsync();
                _startupListeningAttention = "WINDOWS DEFAULT LISTENING OUTPUT COULD NOT BE CONNECTED · Previous WARDOGS selection retained. Review Audio & Routing.";
            }
        }
        if (plan.Microphone is { } microphone)
        {
            if (!await ConfigureAutomaticMicrophoneAsync(microphone, startup: true))
                _startupMicrophoneAttention = "WINDOWS DEFAULT MICROPHONE COULD NOT BE CONNECTED · Previous WARDOGS selection retained. " + Footer.Text;
            else
            {
                _loadingAudioRouteControls = _loadingSetupControls = true;
                MicrophoneBox.SelectedItem = microphone;
                SetupMicrophoneBox.SelectedItem = microphone;
                _loadingAudioRouteControls = _loadingSetupControls = false;
            }
        }
        if (_startupMicrophoneAttention is not null || _startupListeningAttention is not null)
            Footer.Text = string.Join(" · ", new[] { _startupMicrophoneAttention, _startupListeningAttention }.Where(item => item is not null));
    }

    void InvalidateMicrophoneVerification()
    {
        _sawMicSignal = false;
        _config.SetupVerification = AudioDeviceVerification.InvalidateMicrophone(_config.SetupVerification);
        _config.SetupComplete = false;
    }

    void InvalidateListeningVerification()
    {
        _sawMonitorSignal = _listeningPlaybackVerified = false;
        _config.SetupVerification = AudioDeviceVerification.InvalidateListening(_config.SetupVerification);
        _config.SetupComplete = false;
        SetupHeardMusic.IsChecked = false;
    }
}
