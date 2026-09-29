using System.Windows.Controls;
using WardogsRadio.Playback;

namespace WardogsRadio.App;

public partial class MainWindow
{
    IReadOnlyList<MpvAudioDevice> _listeningPlayerDevices = [];
    ListeningOutputResolver.Result? _listeningResolution;
    bool _listeningPlayerSwitched;
    bool _listeningPlaybackVerified;
    string? _listeningConnectionError;

    WindowsAudioEndpoint? ConfiguredListeningEndpoint() =>
        ListeningOutputInventory.Selected(
            (MonitorBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.ToArray() ?? [], _config.MonitorDeviceId);

    string RequireListeningPlayerDevice()
    {
        if (string.IsNullOrWhiteSpace(_config.MonitorDeviceId))
            throw new InvalidOperationException("Choose a listening output before playing a station.");
        if (ConfiguredListeningEndpoint() is not { IsPresent: true })
            throw new InvalidOperationException("The selected listening output is disconnected. Reconnect it or choose another output.");
        if (_listeningResolution?.Device is not { } player ||
            _listeningConnectionError is not null ||
            !string.Equals(player.Name, _config.MpvAudioDeviceName, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(_config.MpvAudioDeviceEndpointId, _config.MonitorDeviceId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The selected listening output needs player repair. Refresh device lists.");
        return player.Name;
    }

    void SyncListeningSelectors()
    {
        var selected = ConfiguredListeningEndpoint();
        _loadingAudioRouteControls = true;
        _loadingSetupControls = true;
        try
        {
            MonitorBox.SelectedItem = selected;
            SetupMonitorBox.SelectedItem = ListeningOutputInventory.Selected(
                (SetupMonitorBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.ToArray() ?? [], _config.MonitorDeviceId);
            MpvOutputBox.SelectedItem = _listeningPlayerDevices.FirstOrDefault(device =>
                string.Equals(device.Name, _listeningResolution?.Device?.Name, StringComparison.OrdinalIgnoreCase));
            SetupMpvOutputBox.SelectedItem = MpvOutputBox.SelectedItem;
        }
        finally { _loadingSetupControls = false; _loadingAudioRouteControls = false; }
    }

    void ShowListeningOutputState()
    {
        var endpoint = ConfiguredListeningEndpoint();
        var name = endpoint?.Name ?? _config.MonitorDeviceName ?? "Listening output";
        var state = string.IsNullOrWhiteSpace(_config.MonitorDeviceId) ? "Choose headphones or speakers." :
            endpoint is not { IsPresent: true } ? $"{name} · Selected, but disconnected" :
            _listeningResolution?.Resolved != true || _listeningConnectionError is not null
                ? $"{name} · Selected, but playback connection failed" :
            _listeningPlaybackVerified ? $"{name} · Playback path verified" :
            $"{name} · Selected; run the playback test to verify sound";
        MonitorRouteText.Text = state;
        SetupDeviceState.Text = state;
        AudioGraphListeningState.Text = state;
        MpvOutputState.Text = state;
        SetupMpvOutputState.Text = state;
    }

    async Task SilenceUnresolvedListeningAsync()
    {
        if (_mpvProvider is { } player) try { await player.PauseAsync(); } catch { }
        if (_active?.ProviderId == "youtube") try { await YouTubeCommandAsync("pause()"); } catch { }
    }

    async Task<bool> ReconcileListeningOutputAsync()
    {
        _listeningPlayerSwitched = false;
        _listeningPlaybackVerified = false;
        _listeningConnectionError = null;
        var endpoint = ConfiguredListeningEndpoint();
        if (endpoint is not { IsPresent: true })
        {
            _listeningResolution = null;
            await SilenceUnresolvedListeningAsync();
            ShowListeningOutputState();
            PublishReadiness(CaptureReadiness());
            return false;
        }
        var windows = (MonitorBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.ToArray() ?? [];
        var resolved = ListeningOutputResolver.Resolve(endpoint, windows, _listeningPlayerDevices,
            _config.MpvAudioDeviceName,
            savedIdBelongsToEndpoint: string.Equals(_config.MpvAudioDeviceEndpointId, endpoint.Id, StringComparison.OrdinalIgnoreCase));
        _listeningResolution = resolved;
        if (resolved.Device is null)
        {
            _listeningConnectionError = resolved.Detail;
            await SilenceUnresolvedListeningAsync();
            ShowListeningOutputState();
            PublishReadiness(CaptureReadiness());
            return false;
        }
        try
        {
            if (resolved.Device.Name == _config.GameMpvAudioDeviceName)
                throw new InvalidOperationException("Listening and game feed must use different output devices.");
            if (_b1Audition is not null)
                throw new InvalidOperationException("Release the game voice preview before changing listening output.");
            if (_active?.ProviderId == "youtube" && _youtubeListeningRoute.IsActive)
            {
                await _youtubeListeningGate.WaitAsync();
                try { await _youtubeListeningRoute.SwitchAsync(endpoint.Id); }
                finally { _youtubeListeningGate.Release(); }
                _listeningPlayerSwitched = true;
            }
            if (_mpvProvider is { } player)
            {
                await player.SetAudioDeviceAsync(resolved.Device.Name);
                if (!string.Equals(await player.ReadAudioDeviceAsync(), resolved.Device.Name, StringComparison.Ordinal))
                    throw new InvalidOperationException("The local player did not confirm the requested output.");
                if (player.Snapshot.IsPlaying)
                {
                    var opened = false;
                    for (var attempt = 0; attempt < 20; attempt++)
                    {
                        opened = (await player.ReadCurrentAudioOutputAsync())?.StartsWith("wasapi", StringComparison.OrdinalIgnoreCase) == true;
                        if (opened) break;
                        await Task.Delay(50);
                    }
                    if (!opened) throw new InvalidOperationException("The local player did not open a Windows audio output.");
                }
                _listeningPlayerSwitched = true;
            }
            if (_config.MpvAudioDeviceName != resolved.Device.Name || _config.MpvAudioDeviceEndpointId != endpoint.Id)
            {
                _config.MpvAudioDeviceName = resolved.Device.Name;
                _config.MpvAudioDeviceEndpointId = endpoint.Id;
                await _store.SaveAsync(_config);
            }
            SyncListeningSelectors();
            ShowListeningOutputState();
            PublishReadiness(CaptureReadiness());
            return true;
        }
        catch (Exception error)
        {
            _listeningConnectionError = error.Message;
            await SilenceUnresolvedListeningAsync();
            ShowListeningOutputState();
            PublishReadiness(CaptureReadiness());
            return false;
        }
    }

    async Task RefreshListeningOutputDevicesAsync()
    {
        if (!await LoadAudioEndpointsAsync(reconcile: false)) return;
        await LoadMpvOutputsAsync();
    }
}
