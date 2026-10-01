using System.Windows;
using System.Windows.Controls;
using WardogsRadio.Core;
using WardogsRadio.Playback;
using WardogsRadio.Voicemeeter;

namespace WardogsRadio.App;

public partial class MainWindow
{
    string? _microphoneIdentityDetail;
    bool _microphoneChangeInProgress;

    async Task<bool> ConfigureAutomaticMicrophoneAsync(WindowsAudioEndpoint microphone, bool startup = false)
    {
        if (_microphoneChangeInProgress) return false;
        _microphoneChangeInProgress = true;
        MicrophoneBox.IsEnabled = SetupMicrophoneBox.IsEnabled = false;
        try { return await ConfigureAutomaticMicrophoneCoreAsync(microphone, startup); }
        finally
        {
            MicrophoneBox.IsEnabled = SetupMicrophoneBox.IsEnabled = true;
            _microphoneChangeInProgress = false;
        }
    }

    async Task<bool> ConfigureAutomaticMicrophoneCoreAsync(WindowsAudioEndpoint microphone, bool startup)
    {
        var status = _bridge.Probe();
        if (!status.Connected || status.Edition != "Banana")
        {
            Footer.Text = "AUTO MICROPHONE SETUP NEEDS A CONNECTED VOICEMEETER BANANA ENGINE.";
            return false;
        }
        if (!microphone.IsInput || !microphone.IsPresent || StartupAudioDevicePolicy.IsVirtualMicrophone(microphone))
        {
            Footer.Text = "CHOOSE AN AVAILABLE PHYSICAL MICROPHONE. Virtual audio routes cannot be assigned as physical inputs.";
            return false;
        }
        var devices = _bridge.ListAudioDevices(true);
        var selected = VoicemeeterDeviceIdentity.Resolve(microphone.Name, devices, "wdm");
        _microphoneIdentityDetail = $"Windows endpoint: {microphone.Name} / {microphone.Id}; {selected.Detail}";
        var device = selected.Device;
        if (device is null)
        {
            Footer.Text = "VOICEMEETER CANNOT MATCH THAT MICROPHONE TO AN AVAILABLE WDM INPUT.";
            return false;
        }
        if (device.HardwareId.StartsWith("BTHHFENUM", StringComparison.OrdinalIgnoreCase) &&
            (startup || !RadioDialogWindow.Confirm(this, "Bluetooth microphone changes headphone audio",
                "Opening this Bluetooth headset microphone can switch the same headset from high-quality stereo music to lower-quality call audio. A separate microphone keeps stereo playback available.\n\nConnect this Bluetooth mic anyway?",
                "USE BLUETOOTH MIC")))
        {
            Footer.Text = "MIC ROUTE UNCHANGED · Choose a separate microphone on Step 2 to keep stereo headset music.";
            return false;
        }
        var ownedStrip = _config.AutoMicrophoneStrip;
        var strip = ownedStrip ?? Enumerable.Range(0, 3).FirstOrDefault(index =>
            _bridge.TryReadDevice(index, out var name) && string.IsNullOrWhiteSpace(name), -1);
        if (strip < 0)
        {
            Footer.Text = "ALL PHYSICAL VOICEMEETER INPUTS ARE ASSIGNED. AUTO SETUP WILL NOT REPLACE AN EXISTING DEVICE.";
            return false;
        }
        if (!_bridge.TryReadDevice(strip, out var priorDevice))
        {
            Footer.Text = "CANNOT READ THE CURRENT MICROPHONE ASSIGNMENT. No mixer state was changed.";
            return false;
        }
        if (ownedStrip is not null && !VoicemeeterDeviceIdentity.CanReuseOwnedStrip(priorDevice,
            _config.AutoMicrophoneAppliedDeviceName, _config.AutoMicrophonePreviousDeviceName))
        {
            Footer.Text = "MICROPHONE ROUTE CHANGED OUTSIDE WARDOGS. Review Audio & Routing. No mixer state was changed.";
            return false;
        }
        var previous = VoicemeeterDeviceIdentity.Resolve(priorDevice, devices);
        var priorDriver = string.IsNullOrWhiteSpace(priorDevice) ? "wdm" : previous.Device?.InterfaceName.ToLowerInvariant();
        _microphoneIdentityDetail += $"; current strip {strip}: {priorDevice}; prior {previous.Detail}";
        if (priorDriver is not ("wdm" or "mme" or "ks"))
        {
            Footer.Text = "MICROPHONE CHANGE WAS NOT APPLIED. WARDOGS could not safely identify the current hardware input. No mixer state was changed. Review Advanced Diagnostics.";
            return false;
        }
        priorDevice = previous.Device is { } previousDevice ? VoicemeeterDeviceIdentity.Parse(previousDevice.Name).Name : "";
        var expectedReadback = priorDevice.Length == 0 ? "" : priorDriver.ToUpperInvariant() + ": " + priorDevice;
        var meter = _bridge.ReadStripSignal(strip, status.Edition);
        if (!meter.Available || ownedStrip is null && meter.Peak > .005f)
        {
            Footer.Text = "THE FREE PHYSICAL INPUT HAS LIVE SIGNAL OR NO READABLE METER; AUTO SETUP WILL NOT TAKE IT OVER.";
            return false;
        }
        if (!_bridge.TryReadRoute(strip, _bridge.Topology.ListeningBus, out var priorA1) ||
            !_bridge.TryReadRoute(strip, _bridge.Topology.GameBus, out var priorB1) ||
            !_bridge.TryReadGain(strip, out var priorGain))
        {
            Footer.Text = "CANNOT READ MICROPHONE ROUTES; NOTHING CHANGED.";
            return false;
        }
        var priorStrip = _config.MicrophoneStripIndex;
        var driver = device.InterfaceName.ToLowerInvariant();
        var routeLeases = new List<AudioRouteLease>();
        AudioDeviceLease? deviceLease = null;
        var formerConfig = (_config.MicrophoneStripIndex, _config.MicrophoneDeviceId, _config.AutoMicrophoneStrip,
            _config.AutoMicrophonePreviousDeviceName, _config.AutoMicrophonePreviousDriver,
            _config.AutoMicrophoneAppliedDeviceName, _config.AutoMicrophonePreviousA1,
            _config.AutoMicrophonePreviousB1, _config.AutoMicrophonePreviousStripIndex, _config.SetupComplete,
            _config.MicrophoneVolume, _config.MicrophoneVolumeInitialized, _config.SetupVerification);
        try
        {
            var assignment = await _bridge.SetDeviceAsync("automatic microphone setup", $"Strip[{strip}].device.{driver}",
                $"Strip[{strip}].device.name", device.Name,
                persistLease: true, expectedPriorReadback: expectedReadback);
            deviceLease = assignment.DeviceLease ?? _bridge.OwnedDevices.FirstOrDefault(lease =>
                lease.Owner == "automatic microphone setup" && lease.ReadbackResource == $"Strip[{strip}].device.name");
            if (!assignment.Success)
            {
                throw new InvalidOperationException(assignment.Detail);
            }
            foreach (var (bus, enabled) in new[] { (_bridge.Topology.ListeningBus, false), (_bridge.Topology.GameBus, true) })
            {
                var route = await _bridge.ApplyRouteAsync("automatic microphone setup", strip, bus, enabled);
                if (!route.Success) throw new InvalidOperationException(route.Detail);
                if (route.Lease is { } lease) routeLeases.Add(lease);
            }
            if (ownedStrip is null)
            {
                _config.AutoMicrophoneStrip = strip;
                _config.AutoMicrophonePreviousDeviceName = priorDevice;
                _config.AutoMicrophonePreviousDriver = priorDriver;
                _config.AutoMicrophonePreviousA1 = priorA1;
                _config.AutoMicrophonePreviousB1 = priorB1;
                _config.AutoMicrophonePreviousStripIndex = priorStrip;
            }
            _config.AutoMicrophoneAppliedDeviceName = device.Name;
            _config.MicrophoneStripIndex = strip;
            _config.MicrophoneDeviceId = microphone.Id;
            if (formerConfig.MicrophoneDeviceId != microphone.Id || formerConfig.MicrophoneStripIndex != strip || routeLeases.Count > 0)
                InvalidateMicrophoneVerification();
            if (SetupView.Visibility == Visibility.Visible && _setupVisit is { } visit)
            {
                visit.RecordRoute(strip, _bridge.Topology.ListeningBus, priorA1, false);
                visit.RecordRoute(strip, _bridge.Topology.GameBus, priorB1, true);
                if (!VoicemeeterDeviceIdentity.SameName(priorDevice, device.Name))
                    visit.RecordDevice(strip, priorDevice ?? "", device.Name, priorDriver);
                _setupHasUncommittedChanges = true;
            }
            PopulateMusicStrips();
            if (!_config.MicrophoneVolumeInitialized)
            {
                _config.MicrophoneVolume = Math.Clamp(Math.Pow(10, priorGain / 20), 0, MicrophoneLevel.Maximum);
                _config.MicrophoneVolumeInitialized = true;
            }
            var gain = await _bridge.SetFloatAsync("automatic microphone setup", $"Strip[{strip}].Gain",
                MicrophoneLevel.GainDb(_config.MicrophoneVolume));
            if (!gain.Success) throw new InvalidOperationException(gain.Detail);
            if (gain.Lease is { } gainLease) routeLeases.Add(gainLease);
            ShowMicrophoneVolume(_config.MicrophoneVolume);
            if (!_bridge.TryReadGain(strip, out var confirmedGain) ||
                Math.Abs(confirmedGain - MicrophoneLevel.GainDb(_config.MicrophoneVolume)) >= .1f)
                throw new InvalidOperationException("Microphone gain could not be confirmed after connecting.");
            if (SetupView.Visibility == Visibility.Visible && _setupVisit is { } gainVisit)
                gainVisit.RecordFloat($"Strip[{strip}].Gain", priorGain, confirmedGain);
            await _store.SaveAsync(_config);
            if (SetupView.Visibility != Visibility.Visible)
            {
                var committed = await _bridge.CommitConfigurationAsync(deviceLease, routeLeases);
                if (!committed.Success) throw new InvalidOperationException(committed.Detail);
                deviceLease = null;
            }
            RefreshSignalMeters();
            if (SetupView.Visibility == Visibility.Visible) _setupHasUncommittedChanges = true;
            Footer.Text = $"MICROPHONE ASSIGNED TO VOICEMEETER HARDWARE INPUT {strip + 1} → {_config.GameBus}; A1 SELF-MONITORING OFF. SPEAK TO VERIFY ITS LIVE METER.";
            RefreshSetupWizard();
            _startupMicrophoneAttention = null;
            return true;
        }
        catch (Exception error)
        {
            var restoreErrors = new List<string>();
            foreach (var lease in routeLeases.AsEnumerable().Reverse())
            {
                var state = await _bridge.ReleaseRouteAsync(lease);
                if (state != AudioLeaseReleaseState.Restored) restoreErrors.Add($"{lease.Resource}: {state}");
            }
            if (deviceLease is { } pendingDevice)
            {
                var restored = await _bridge.ReleaseDeviceAsync(pendingDevice);
                if (restored != AudioLeaseReleaseState.Restored)
                    restoreErrors.Add($"{pendingDevice.ReadbackResource}: {restored}");
            }
            {
                (_config.MicrophoneStripIndex, _config.MicrophoneDeviceId, _config.AutoMicrophoneStrip,
                    _config.AutoMicrophonePreviousDeviceName, _config.AutoMicrophonePreviousDriver,
                    _config.AutoMicrophoneAppliedDeviceName, _config.AutoMicrophonePreviousA1,
                    _config.AutoMicrophonePreviousB1, _config.AutoMicrophonePreviousStripIndex, _config.SetupComplete,
                    _config.MicrophoneVolume, _config.MicrophoneVolumeInitialized, _config.SetupVerification) = formerConfig;
            }
            try { await _store.SaveAsync(_config); }
            catch (Exception saveError) { restoreErrors.Add("Configuration save failed: " + saveError.Message); }
            Footer.Text = "AUTO MICROPHONE SETUP FAILED · " + error.Message +
                (restoreErrors.Count == 0 ? " · Prior mixer state restored." : " · Restore needs attention: " + string.Join("; ", restoreErrors));
            return false;
        }
    }

    async void MicrophoneBox_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (_loadingAudioRouteControls || !IsLoaded || MicrophoneBox.SelectedItem is not WindowsAudioEndpoint microphone) return;
        if (!await ConfigureAutomaticMicrophoneAsync(microphone))
        {
            _loadingAudioRouteControls = true;
            MicrophoneBox.SelectedItem = (MicrophoneBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == _config.MicrophoneDeviceId);
            _loadingAudioRouteControls = false;
            return;
        }
        MicrophoneRouteText.Text = microphone.Name + " · connected to Voicemeeter B1";
        _loadingSetupControls = true;
        SetupMicrophoneBox.SelectedItem = (SetupMicrophoneBox.ItemsSource as IEnumerable<WindowsAudioEndpoint>)?.FirstOrDefault(x => x.Id == microphone.Id);
        _loadingSetupControls = false;
        RefreshSetupWizard();
    }

}
