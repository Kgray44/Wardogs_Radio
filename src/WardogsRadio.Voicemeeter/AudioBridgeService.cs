using System.Text.Json;

namespace WardogsRadio.Voicemeeter;

public sealed record VoicemeeterTopology(string Edition, IReadOnlyList<int> PhysicalInputStrips,
    int PrimaryVaioStrip, int PreferredMusicStrip, string ListeningBus, string GameBus)
{
    public static VoicemeeterTopology Banana { get; } = new("Banana", [0, 1, 2], 3, 4, "A1", "B1");
}

public enum AudioBridgeConnectionState { NotInstalled, EngineStopped, Starting, Connecting, Connected, Recovering, Faulted, UnsupportedEdition }
public enum AudioLeaseReleaseState { Restored, ExternallyChanged, Failed }
public sealed record AudioRouteLease(string Owner, string Resource, float PriorState, float AppliedState,
    DateTimeOffset CreatedAt);
public sealed record AudioDeviceLease(string Owner, string Resource, string ReadbackResource,
    string PriorName, string AppliedName, string RestoreParameter, DateTimeOffset CreatedAt);
public sealed record AudioBridgeFault(DateTimeOffset At, string Subsystem, string Fault, string Action, string Result);
public sealed record AudioBridgeTelemetry(VoicemeeterStatus Status, int? MicrophoneStrip, int? MusicStrip,
    string? MicrophoneDevice, bool? MicrophoneGameRoute, bool? MusicGameRoute,
    SignalLevel Microphone, SignalLevel Music, SignalLevel GameBus, DateTimeOffset CapturedAt);
public sealed record AudioBridgeSnapshot(AudioBridgeConnectionState Connection, string? Edition,
    IReadOnlyList<AudioRouteLease> OwnedRoutes, IReadOnlyList<AudioBridgeFault> Faults,
    DateTimeOffset CapturedAt, string Detail)
{
    public AudioBridgeTelemetry? Telemetry { get; init; }
    public IReadOnlyList<AudioDeviceLease> OwnedDevices { get; init; } = [];
}
public sealed record AudioOperationResult(bool Success, string Detail, AudioRouteLease? Lease = null,
    AudioDeviceLease? DeviceLease = null);

/// <summary>Owns the supported Banana connection and verifies route mutations before recording ownership.</summary>
public sealed class AudioBridgeService(IVoicemeeterRemote remote, TimeProvider? clock = null, string? recoveryPath = null)
{
    public static readonly TimeSpan FastTelemetryInterval = TimeSpan.FromMilliseconds(40);
    public static readonly TimeSpan LifecycleTelemetryInterval = TimeSpan.FromSeconds(1);
    readonly TimeProvider _clock = clock ?? TimeProvider.System;
    readonly SemaphoreSlim _operations = new(1, 1);
    readonly List<AudioRouteLease> _leases = [];
    readonly List<AudioDeviceLease> _deviceLeases = [];
    // Writers own the lists under _operations; readers use immutable published
    // copies so a 40 ms UI snapshot never waits for a slow verified mixer write.
    volatile IReadOnlyList<AudioRouteLease> _publishedRoutes = Array.AsReadOnly(Array.Empty<AudioRouteLease>());
    volatile IReadOnlyList<AudioDeviceLease> _publishedDevices = Array.AsReadOnly(Array.Empty<AudioDeviceLease>());
    readonly Queue<AudioBridgeFault> _faults = new();
    CancellationTokenSource? _telemetryCancellation;
    Task? _telemetryTask;
    int? _telemetryMicrophoneStrip;
    int? _telemetryMusicStrip;
    readonly string? _recoveryPath = recoveryPath;
    bool _recoveryChecked = recoveryPath is null ||
        !File.Exists(recoveryPath) && !File.Exists(recoveryPath + ".devices");
    volatile AudioBridgeConnectionState _state = AudioBridgeConnectionState.EngineStopped;
    volatile string _detail = "Audio Bridge has not connected yet.";
    volatile AudioBridgeTelemetry? _telemetry;
    public VoicemeeterTopology Topology => VoicemeeterTopology.Banana;
    public VoicemeeterStripLimiter Limiter { get; } = new(remote);
    public bool RecoveryReady => _recoveryChecked;
    public AudioBridgeSnapshot Snapshot
    {
        get
        {
            var telemetry = _telemetry;
            return new(_state, telemetry?.Status.Edition ?? remote.ProbeFast().Edition,
                _publishedRoutes, ReadFaults(), _clock.GetUtcNow(), _detail)
                { Telemetry = telemetry, OwnedDevices = _publishedDevices };
        }
    }
    public IReadOnlyList<AudioDeviceLease> OwnedDevices => _publishedDevices;

    AudioBridgeFault[] ReadFaults() { lock (_faults) return _faults.ToArray(); }

    public void StartTelemetry(int? microphoneStrip, int? musicStrip)
    {
        UpdateTelemetryTargets(microphoneStrip, musicStrip);
        if (_telemetryTask is { IsCompleted: false }) return;
        _telemetryCancellation?.Dispose();
        _telemetryCancellation = new CancellationTokenSource();
        var token = _telemetryCancellation.Token;
        _telemetryTask = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(FastTelemetryInterval);
            var ticks = 0;
            try
            {
                do
                {
                    await _operations.WaitAsync(token);
                    try
                    {
                        if (ticks++ % 25 == 0) SampleTelemetry(_telemetryMicrophoneStrip, _telemetryMusicStrip);
                        else SampleFastTelemetry(_telemetryMicrophoneStrip, _telemetryMusicStrip);
                    }
                    finally { _operations.Release(); }
                } while (await timer.WaitForNextTickAsync(token));
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { Failure("Telemetry", error.Message, "sample mixer signal"); }
        }, token);
    }

    public void UpdateTelemetryTargets(int? microphoneStrip, int? musicStrip)
    {
        _telemetryMicrophoneStrip = microphoneStrip;
        _telemetryMusicStrip = musicStrip;
    }

    public async Task StopTelemetryAsync()
    {
        if (_telemetryCancellation is null) return;
        await _telemetryCancellation.CancelAsync();
        if (_telemetryTask is not null) await _telemetryTask;
        _telemetryTask = null;
        _telemetryCancellation.Dispose();
        _telemetryCancellation = null;
    }

    public AudioBridgeTelemetry SampleTelemetry(int? microphoneStrip, int? musicStrip, VoicemeeterStatus? knownStatus = null)
    {
        var status = knownStatus ?? remote.Probe();
        var connected = status is { Connected: true, Edition: "Banana" };
        UpdateConnectionFromTelemetry(connected);
        var monitor = new VoicemeeterSignalMonitor(remote);
        SignalLevel ReadStrip(int? strip) => connected && strip is >= 0 and <= 4
            ? monitor.ReadStrip(status.Edition, strip) : new(false, 0);
        bool? ReadRoute(int? strip) => connected && strip is { } index && TryReadRoute(index, Topology.GameBus, out var enabled)
            ? enabled : null;
        var device = connected && microphoneStrip is { } mic && TryReadDevice(mic, out var name) ? name : null;
        _telemetry = new(status, microphoneStrip, musicStrip, device, ReadRoute(microphoneStrip),
            ReadRoute(musicStrip), ReadStrip(microphoneStrip), ReadStrip(musicStrip),
            connected ? monitor.ReadBus(status.Edition, Topology.GameBus) : new(false, 0), _clock.GetUtcNow());
        return _telemetry;
    }

    public AudioBridgeTelemetry SampleFastTelemetry(int? microphoneStrip, int? musicStrip)
    {
        var status = remote.ProbeFast();
        var connected = status is { Connected: true, Edition: "Banana" };
        UpdateConnectionFromTelemetry(connected);
        var previous = _telemetry;
        var monitor = new VoicemeeterSignalMonitor(remote);
        SignalLevel ReadStrip(int? strip) => connected && strip is >= 0 and <= 4
            ? monitor.ReadStrip(status.Edition, strip) : new(false, 0);
        var sameTargets = previous?.MicrophoneStrip == microphoneStrip && previous?.MusicStrip == musicStrip;
        _telemetry = new(status, microphoneStrip, musicStrip,
            connected && sameTargets ? previous?.MicrophoneDevice : null,
            connected && sameTargets ? previous?.MicrophoneGameRoute : null,
            connected && sameTargets ? previous?.MusicGameRoute : null,
            ReadStrip(microphoneStrip), ReadStrip(musicStrip),
            connected ? monitor.ReadBus(status.Edition, Topology.GameBus) : new(false, 0), _clock.GetUtcNow());
        return _telemetry;
    }

    void UpdateConnectionFromTelemetry(bool connected)
    {
        if (!connected && _state == AudioBridgeConnectionState.Connected)
        {
            _state = AudioBridgeConnectionState.Recovering;
            _detail = "Audio Bridge lost its Voicemeeter Banana connection; reconnecting.";
        }
        else if (connected && _recoveryChecked && _state is
                 AudioBridgeConnectionState.Faulted or AudioBridgeConnectionState.Starting or AudioBridgeConnectionState.Recovering or AudioBridgeConnectionState.Connecting)
        {
            _state = AudioBridgeConnectionState.Connected;
            _detail = "Audio Bridge connected to Voicemeeter Banana after engine startup.";
        }
    }
    public bool TryReadRoute(int strip, string bus, out bool enabled)
    {
        enabled = false;
        if (strip is < 0 or > 4 || bus is not ("A1" or "B1") ||
            !remote.TryGetParameterFloat($"Strip[{strip}].{bus}", out var value)) return false;
        enabled = value >= .5f;
        return true;
    }

    public bool TryReadDevice(int strip, out string name) =>
        remote.TryGetParameterString($"Strip[{strip}].device.name", out name);

    public bool TryReadFloat(string parameter, out float value) => remote.TryGetParameterFloat(parameter, out value);
    public bool TryReadString(string parameter, out string value) => remote.TryGetParameterString(parameter, out value);

    // The temporary YouTube listening route is synchronous because Windows endpoint
    // selection and station teardown are synchronous. These narrow verified primitives
    // keep its mixer writes inside the Audio Bridge boundary.
    public bool TrySetFloatVerified(string parameter, float desired)
    {
        _operations.Wait();
        try
        {
            if (!IsBananaConnected() || !IsSupportedFloatParameter(parameter) ||
                !remote.TrySetParameterFloat(parameter, desired)) return false;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (remote.TryGetParameterFloat(parameter, out var actual) && Math.Abs(actual - desired) < .1f) return true;
                Thread.Sleep(30);
            }
            return false;
        }
        finally { _operations.Release(); }
    }

    public bool TrySetDeviceVerified(string parameter, string readbackParameter, string desired)
    {
        _operations.Wait();
        try
        {
            if (!IsBananaConnected() || !IsSupportedDeviceParameter(parameter, readbackParameter) ||
                !remote.TrySetParameterString(parameter, desired)) return false;
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (remote.TryGetParameterString(readbackParameter, out var actual) &&
                    VoicemeeterDeviceIdentity.MatchesAssignment(actual, desired, parameter.Split('.').Last())) return true;
                Thread.Sleep(30);
            }
            return false;
        }
        finally { _operations.Release(); }
    }

    public bool TryClearA1()
    {
        _operations.Wait();
        try { return IsBananaConnected() && VoicemeeterDeviceRestorer.TryClearA1(remote); }
        finally { _operations.Release(); }
    }

    public bool TryReadGain(int strip, out float gain)
    {
        gain = 0;
        return strip is >= 0 and <= 4 && remote.TryGetParameterFloat($"Strip[{strip}].Gain", out gain);
    }

    public async Task<AudioOperationResult> SetGainAsync(int strip, float gain, CancellationToken cancellationToken = default)
    {
        if (strip is < 0 or > 4 || !float.IsFinite(gain)) return new(false, "Unsupported microphone gain.");
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (!_recoveryChecked) return new(false, "Unresolved Audio Bridge recovery state must be repaired before a new mixer change.");
            if (!IsBananaConnected()) return new(false, "Audio Bridge is not connected to Voicemeeter Banana.");
            var parameter = $"Strip[{strip}].Gain";
            if (remote.TryGetParameterFloat(parameter, out var current) && Math.Abs(current - gain) < .1f)
                return new(true, "Microphone gain already has the requested value.");
            if (!remote.TrySetParameterFloat(parameter, gain) || !await VerifyFloatAsync(parameter, gain, cancellationToken))
                return Failure("Gain", $"Voicemeeter did not confirm {parameter} at {gain:0.0} dB.", "set microphone gain");
            return new(true, $"Microphone gain verified at {gain:0.0} dB.");
        }
        finally { _operations.Release(); }
    }

    public IReadOnlyList<VoicemeeterAudioDevice> ListAudioDevices(bool inputs) => remote.ListAudioDevices(inputs);

    public VoicemeeterStatus Probe() => remote.Probe();

    public SignalLevel ReadStripSignal(int strip, string? edition = null) =>
        new VoicemeeterSignalMonitor(remote).ReadStrip(edition ?? remote.Probe().Edition, strip);

    public SignalLevel ReadBusSignal(string bus, string? edition = null) =>
        new VoicemeeterSignalMonitor(remote).ReadBus(edition ?? remote.Probe().Edition, bus);

    public VoicemeeterMeterForensics CaptureMeterForensics(string? edition = null) =>
        new VoicemeeterSignalMonitor(remote).CaptureForensics(edition ?? remote.Probe().Edition);

    public async Task<AudioOperationResult> SetFloatAsync(string owner, string parameter, float desired,
        CancellationToken cancellationToken = default)
    {
        if (!float.IsFinite(desired) || !IsSupportedFloatParameter(parameter))
            return new(false, "Unsupported Banana parameter.");
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (!_recoveryChecked) return new(false, "Unresolved Audio Bridge recovery state must be repaired before a new mixer change.");
            if (!IsBananaConnected()) return new(false, "Audio Bridge is not connected to Voicemeeter Banana.");
            if (!remote.TryGetParameterFloat(parameter, out var prior))
                return Failure("Route", $"Could not read {parameter}; no change was made.", "read prior state");
            if (Math.Abs(prior - desired) < .01f) return new(true, $"{parameter} already has the requested value.");
            if (_leases.Any(lease => lease.Resource == parameter))
                return new(false, $"{parameter} already has an Audio Bridge owner.");
            var lease = new AudioRouteLease(owner, parameter, prior, desired, _clock.GetUtcNow());
            _leases.Add(lease);
            try { SaveRecovery(); }
            catch (Exception error)
            {
                _leases.Remove(lease);
                return Failure("Recovery", "Could not save prior audio state: " + error.Message, "write recovery record");
            }
            if (!remote.TrySetParameterFloat(parameter, desired))
            {
                await ResolveFailedWriteAsync(lease, cancellationToken);
                return Failure("Route", $"Voicemeeter refused {parameter}.", "write route");
            }
            if (await VerifyFloatAsync(parameter, desired, cancellationToken))
                return new(true, $"{parameter} verified.", lease);
            await ResolveFailedWriteAsync(lease, cancellationToken);
            return Failure("Route", $"{parameter} did not confirm the requested state; recovery ownership was retained if restoration was uncertain.", "verify route");
        }
        finally { _operations.Release(); }
    }

    public async Task<AudioOperationResult> SetDeviceAsync(string owner, string parameter, string readbackParameter,
        string desired, Func<string, bool>? matches = null, bool persistLease = false,
        CancellationToken cancellationToken = default, string? expectedPriorReadback = null)
    {
        if (!IsSupportedDeviceParameter(parameter, readbackParameter) || persistLease && string.IsNullOrWhiteSpace(desired))
            return new(false, "Unsupported Banana device parameter.");
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (!_recoveryChecked) return new(false, "Unresolved Audio Bridge recovery state must be repaired before a new mixer change.");
            if (!IsBananaConnected()) return new(false, "Audio Bridge is not connected to Voicemeeter Banana.");
            if (!remote.TryGetParameterString(readbackParameter, out var prior))
                return Failure("Device", $"Could not read {readbackParameter}; no change was made.", "read prior device");
            var requestedDriver = parameter.Split('.').Last();
            if (expectedPriorReadback is not null && !VoicemeeterDeviceIdentity.SameName(prior, expectedPriorReadback))
                return Failure("Device", "MICROPHONE ROUTE CHANGED OUTSIDE WARDOGS · Review Audio & Routing. No mixer state was changed.", "verify strip ownership");
            var devices = remote.ListAudioDevices(readbackParameter.StartsWith("Strip[", StringComparison.Ordinal));
            var requested = VoicemeeterDeviceIdentity.Resolve(desired, devices, requestedDriver);
            if (persistLease && requested.Device is null)
                return Failure("Device", "MICROPHONE CHANGE WAS NOT APPLIED · The requested input cannot be identified safely. " + requested.Detail, "resolve requested device");
            // The optional matcher is retained for the shared MME output's documented truncated-name contract.
            // Transactional physical inputs always use the canonical identity rules.
            if (persistLease || matches is null)
                matches = current => VoicemeeterDeviceIdentity.MatchesAssignment(current, desired, requestedDriver);
            if (!persistLease && matches(prior)) return new(true, $"{readbackParameter} already has the requested device.");
            AudioDeviceLease? lease = null;
            if (persistLease)
            {
                if (_deviceLeases.Any(existing => existing.ReadbackResource == readbackParameter))
                    return new(false, $"{readbackParameter} already has an Audio Bridge owner.");
                var previous = VoicemeeterDeviceIdentity.Resolve(prior, devices);
                var driver = string.IsNullOrWhiteSpace(prior) ? requestedDriver : previous.Device?.InterfaceName.ToLowerInvariant();
                if (driver is not ("mme" or "wdm" or "ks"))
                    return Failure("Device", "MICROPHONE CHANGE WAS NOT APPLIED · WARDOGS could not safely identify the device currently assigned to this hardware input. No mixer state was changed. " + previous.Detail, "prepare device recovery");
                if (driver == "ks" && !readbackParameter.StartsWith("Strip[", StringComparison.Ordinal))
                    return Failure("Device", "KS output recovery is outside the supported shared-listening contract. No mixer state was changed.", "prepare device recovery");
                if (driver == requestedDriver && matches(prior))
                    return new(true, $"{readbackParameter} already has the requested device.");
                var restoreParameter = readbackParameter[..readbackParameter.LastIndexOf('.')] + "." + driver;
                // Remote API writes require the undecorated public name and its actual driver.
                var priorName = previous.Device is { } resolvedPrior ? VoicemeeterDeviceIdentity.Parse(resolvedPrior.Name).Name : "";
                lease = new(owner, parameter, readbackParameter, priorName, desired, restoreParameter, _clock.GetUtcNow());
                _deviceLeases.Add(lease);
                try { SaveDeviceRecovery(); }
                catch (Exception error)
                {
                    _deviceLeases.Remove(lease);
                    return Failure("Recovery", "Could not save prior device state: " + error.Message, "write device recovery");
                }
            }
            if (!remote.TrySetParameterString(parameter, desired))
            {
                if (lease is not null) await ResolveFailedDeviceWriteAsync(lease, cancellationToken);
                return Failure("Device", $"Voicemeeter refused {parameter}.", "assign device");
            }
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(100, cancellationToken);
                if (remote.TryGetParameterString(readbackParameter, out var current) && matches(current))
                    return new(true, $"{readbackParameter} assignment verified.", DeviceLease: lease);
            }
            if (lease is not null) await ResolveFailedDeviceWriteAsync(lease, cancellationToken);
            return Failure("Device", $"Voicemeeter did not confirm {readbackParameter}; prior device was {prior}.", "verify device");
        }
        finally { _operations.Release(); }
    }

    public async Task<AudioOperationResult> RecoverOwnedRoutesAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (_recoveryPath is null || !File.Exists(_recoveryPath) && !File.Exists(_recoveryPath + ".devices"))
            {
                _recoveryChecked = true;
                return new(true, "No Audio Bridge state needs recovery.");
            }
            if (!IsBananaConnected()) return new(false, "Connect Voicemeeter Banana before recovering owned state.");
            AudioRouteLease[] saved = [];
            if (File.Exists(_recoveryPath))
            {
                try { saved = JsonSerializer.Deserialize<AudioRouteLease[]>(File.ReadAllText(_recoveryPath)) ??
                    throw new InvalidDataException("Route journal has no lease array."); }
                catch (Exception error) { return Failure("Recovery", "Audio recovery record cannot be read: " + error.Message, "read recovery record"); }
                if (saved.Any(lease => lease is null || !IsSupportedFloatParameter(lease.Resource) ||
                    !float.IsFinite(lease.PriorState) || !float.IsFinite(lease.AppliedState)) ||
                    saved.Select(lease => lease.Resource).Distinct(StringComparer.Ordinal).Count() != saved.Length)
                    return Failure("Recovery", "Audio recovery record contains an unsupported or duplicate route; record retained.", "validate recovery record");
            }
            _leases.Clear();
            _leases.AddRange(saved);
            _publishedRoutes = Array.AsReadOnly(_leases.ToArray());
            if (saved.Length == 0) SaveRecovery();
            foreach (var lease in saved.Reverse())
            {
                if (!remote.TryGetParameterFloat(lease.Resource, out var current))
                    return Failure("Recovery", $"Could not read {lease.Resource}; recovery record retained.", "recover route");
                if (Math.Abs(current - lease.PriorState) < .1f || Math.Abs(current - lease.AppliedState) >= .1f)
                {
                    if (Math.Abs(current - lease.AppliedState) >= .1f && Math.Abs(current - lease.PriorState) >= .1f)
                        Failure("Recovery", $"External change detected on {lease.Resource}; it was left untouched.", "recover route");
                    _leases.Remove(lease);
                    SaveRecovery();
                    continue;
                }
                if (!remote.TrySetParameterFloat(lease.Resource, lease.PriorState) ||
                    !await VerifyFloatAsync(lease.Resource, lease.PriorState, cancellationToken))
                    return Failure("Recovery", $"Could not restore {lease.Resource}; recovery record retained.", "recover route");
                _leases.Remove(lease);
                SaveRecovery();
            }
            var devices = await RecoverDevicesCoreAsync(cancellationToken);
            if (devices.Success) _recoveryChecked = true;
            return devices;
        }
        finally { _operations.Release(); }
    }

    public async Task<AudioOperationResult> EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            var status = remote.Probe();
            if (!status.Installed) return SetState(AudioBridgeConnectionState.NotInstalled, "Voicemeeter Banana is not installed.", false);
            if (status.Connected)
                return status.Edition == Topology.Edition
                    ? SetState(AudioBridgeConnectionState.Connected, "Audio Bridge connected to Voicemeeter Banana.", true)
                    : SetState(AudioBridgeConnectionState.UnsupportedEdition, $"{status.Edition ?? "Unknown"} is running; automatic routing requires Banana.", false);

            _state = AudioBridgeConnectionState.Connecting;
            remote.TryLogin(out var loginDetail);
            status = remote.Probe();
            if (status.Connected)
                return status.Edition == Topology.Edition
                    ? SetState(AudioBridgeConnectionState.Connected, "Audio Bridge connected to Voicemeeter Banana.", true)
                    : SetState(AudioBridgeConnectionState.UnsupportedEdition, $"{status.Edition ?? "Unknown"} is running; automatic routing requires Banana.", false);
            // An unidentified running mixer may be Standard or Potato. Never launch a second
            // edition over an existing user's audio engine.
            if (status.Running)
                return SetState(AudioBridgeConnectionState.Faulted, "A Voicemeeter engine is running but the Remote API cannot identify it. Close or repair that engine before starting Banana.", false);
            if (!remote.TryRunVoicemeeter(2, out var launchDetail))
                return SetState(AudioBridgeConnectionState.Faulted, launchDetail + " " + loginDetail, false);
            _state = AudioBridgeConnectionState.Starting;
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(200, cancellationToken);
                status = remote.Probe();
                if (!status.Connected) continue;
                return status.Edition == Topology.Edition
                    ? SetState(AudioBridgeConnectionState.Connected, "Audio Bridge started and connected to Voicemeeter Banana.", true)
                    : SetState(AudioBridgeConnectionState.UnsupportedEdition, $"Voicemeeter {status.Edition} started instead of Banana.", false);
            }
            return SetState(AudioBridgeConnectionState.Faulted, "Voicemeeter Banana did not become ready within four seconds.", false);
        }
        finally { _operations.Release(); }
    }

    public async Task<AudioOperationResult> ApplyRouteAsync(string owner, int strip, string bus, bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (strip is < 0 or > 4 || bus is not ("A1" or "B1"))
            return new(false, "Unsupported Banana route.");
        return await SetFloatAsync(owner, $"Strip[{strip}].{bus}", enabled ? 1f : 0f, cancellationToken);
    }

    public async Task<AudioOperationResult> RepairConfiguredRouteAsync(int strip, string bus, bool enabled,
        CancellationToken cancellationToken = default)
    {
        var result = await ApplyRouteAsync("configured route repair", strip, bus, enabled, cancellationToken);
        if (!result.Success || result.Lease is null) return result;
        return await CommitRouteAsync(result.Lease, cancellationToken);
    }

    public async Task<AudioLeaseReleaseState> ReleaseRouteAsync(AudioRouteLease lease, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (!_leases.Contains(lease)) return AudioLeaseReleaseState.Failed;
            if (!remote.TryGetParameterFloat(lease.Resource, out var current)) return AudioLeaseReleaseState.Failed;
            if (Math.Abs(current - lease.AppliedState) >= .1f)
            {
                _leases.Remove(lease);
                SaveRecovery();
                Failure("Route", $"External change detected on {lease.Resource}; it was left untouched.", "release lease");
                return AudioLeaseReleaseState.ExternallyChanged;
            }
            if (!remote.TrySetParameterFloat(lease.Resource, lease.PriorState)) return AudioLeaseReleaseState.Failed;
            if (await VerifyFloatAsync(lease.Resource, lease.PriorState, cancellationToken))
            {
                _leases.Remove(lease);
                SaveRecovery();
                return AudioLeaseReleaseState.Restored;
            }
            return AudioLeaseReleaseState.Failed;
        }
        finally { _operations.Release(); }
    }

    public async Task<AudioOperationResult> CommitRouteAsync(AudioRouteLease lease, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (!_leases.Contains(lease)) return new(false, "Audio Bridge no longer owns this route.");
            if (!remote.TryGetParameterFloat(lease.Resource, out var current) ||
                Math.Abs(current - lease.AppliedState) >= .1f)
                return Failure("Route", $"{lease.Resource} changed before configuration was saved.", "commit route");
            _leases.Remove(lease);
            SaveRecovery();
            return new(true, $"{lease.Resource} is now owned by saved configuration.");
        }
        finally { _operations.Release(); }
    }

    public async Task<AudioOperationResult> CommitDeviceAsync(AudioDeviceLease lease, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (!_deviceLeases.Contains(lease)) return new(false, "Audio Bridge no longer owns this device assignment.");
            if (!remote.TryGetParameterString(lease.ReadbackResource, out var current) ||
                !VoicemeeterDeviceIdentity.MatchesAssignment(current, lease.AppliedName, lease.Resource.Split('.').Last()))
                return Failure("Device", $"{lease.ReadbackResource} changed before configuration was saved.", "commit device");
            _deviceLeases.Remove(lease);
            SaveDeviceRecovery();
            return new(true, $"{lease.ReadbackResource} is now owned by saved configuration.");
        }
        finally { _operations.Release(); }
    }

    /// <summary>Validate a microphone transaction together before handing its leases to saved configuration.</summary>
    public async Task<AudioOperationResult> CommitConfigurationAsync(AudioDeviceLease? device,
        IReadOnlyList<AudioRouteLease> routes, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (device is not null && (!_deviceLeases.Contains(device) ||
                !remote.TryGetParameterString(device.ReadbackResource, out var currentDevice) ||
                !VoicemeeterDeviceIdentity.MatchesAssignment(currentDevice, device.AppliedName, device.Resource.Split('.').Last())))
                return Failure("Device", "Microphone assignment changed before ownership could be committed.", "commit microphone configuration");
            foreach (var route in routes)
                if (!_leases.Contains(route) || !remote.TryGetParameterFloat(route.Resource, out var current) ||
                    Math.Abs(current - route.AppliedState) >= .1f)
                    return Failure("Route", $"{route.Resource} changed before ownership could be committed.", "commit microphone configuration");
            if (device is not null) _deviceLeases.Remove(device);
            foreach (var route in routes) _leases.Remove(route);
            try { SaveRecovery(); SaveDeviceRecovery(); }
            catch (Exception error)
            {
                if (device is not null) _deviceLeases.Add(device);
                _leases.AddRange(routes);
                _publishedDevices = Array.AsReadOnly(_deviceLeases.ToArray());
                _publishedRoutes = Array.AsReadOnly(_leases.ToArray());
                // Restore durable ownership too if the failure was transient. Never hide the failure.
                try { SaveRecovery(); SaveDeviceRecovery(); } catch { }
                return Failure("Recovery", "Microphone recovery ownership could not be committed: " + error.Message, "commit microphone configuration");
            }
            return new(true, "Microphone device, routes and gain are now owned by saved configuration.");
        }
        finally { _operations.Release(); }
    }

    public async Task<AudioLeaseReleaseState> ReleaseDeviceAsync(AudioDeviceLease lease, CancellationToken cancellationToken = default)
    {
        await _operations.WaitAsync(cancellationToken);
        try { return await ReleaseDeviceCoreAsync(lease, cancellationToken); }
        finally { _operations.Release(); }
    }

    async Task<AudioLeaseReleaseState> ReleaseDeviceCoreAsync(AudioDeviceLease lease, CancellationToken token)
    {
        if (!_deviceLeases.Contains(lease) || !remote.TryGetParameterString(lease.ReadbackResource, out var current))
            return AudioLeaseReleaseState.Failed;
        if (VoicemeeterDeviceIdentity.MatchesAssignment(current, lease.PriorName, lease.RestoreParameter.Split('.').Last()))
        {
            _deviceLeases.Remove(lease);
            SaveDeviceRecovery();
            return AudioLeaseReleaseState.Restored;
        }
        if (!VoicemeeterDeviceIdentity.MatchesAssignment(current, lease.AppliedName, lease.Resource.Split('.').Last()))
        {
            _deviceLeases.Remove(lease);
            SaveDeviceRecovery();
            Failure("Device", $"External change detected on {lease.ReadbackResource}; it was left untouched.", "release device");
            return AudioLeaseReleaseState.ExternallyChanged;
        }
        if (!remote.TrySetParameterString(lease.RestoreParameter, lease.PriorName) ||
            !await VerifyDeviceAsync(lease.ReadbackResource, lease.PriorName, lease.RestoreParameter.Split('.').Last(), token))
            return AudioLeaseReleaseState.Failed;
        _deviceLeases.Remove(lease);
        SaveDeviceRecovery();
        return AudioLeaseReleaseState.Restored;
    }

    async Task<AudioOperationResult> RecoverDevicesCoreAsync(CancellationToken token)
    {
        var path = _recoveryPath + ".devices";
        if (!File.Exists(path)) return new(true, "Owned Voicemeeter state recovered.");
        AudioDeviceLease[] saved;
        try { saved = JsonSerializer.Deserialize<AudioDeviceLease[]>(File.ReadAllText(path)) ??
            throw new InvalidDataException("Device journal has no lease array."); }
        catch (Exception error) { return Failure("Recovery", "Device recovery record cannot be read: " + error.Message, "read device recovery"); }
        if (saved.Any(lease => lease is null ||
            !IsSupportedDeviceParameter(lease.Resource, lease.ReadbackResource) ||
            !IsSupportedDeviceParameter(lease.RestoreParameter, lease.ReadbackResource) ||
            lease.PriorName is null || string.IsNullOrWhiteSpace(lease.AppliedName)) ||
            saved.Select(lease => lease.ReadbackResource).Distinct(StringComparer.Ordinal).Count() != saved.Length)
            return Failure("Recovery", "Device recovery record contains an unsupported or duplicate assignment; record retained.", "validate device recovery");
        _deviceLeases.Clear();
        _deviceLeases.AddRange(saved);
        _publishedDevices = Array.AsReadOnly(_deviceLeases.ToArray());
        if (saved.Length == 0) SaveDeviceRecovery();
        foreach (var lease in saved.Reverse())
        {
            var state = await ReleaseDeviceCoreAsync(lease, token);
            if (state == AudioLeaseReleaseState.Failed)
                return Failure("Recovery", $"Could not restore {lease.ReadbackResource}; recovery record retained.", "recover device");
        }
        return new(true, "Owned Voicemeeter state recovered.");
    }

    async Task ResolveFailedDeviceWriteAsync(AudioDeviceLease lease, CancellationToken token)
    {
        await ReleaseDeviceCoreAsync(lease, token);
    }

    async Task<bool> VerifyDeviceAsync(string readback, string expected, string driver, CancellationToken token)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(100, token);
            if (remote.TryGetParameterString(readback, out var current) &&
                VoicemeeterDeviceIdentity.MatchesAssignment(current, expected, driver)) return true;
        }
        return false;
    }

    public static bool MatchesDeviceName(string? current, string? expected) =>
        VoicemeeterDeviceIdentity.SameName(current, expected);

    void SaveDeviceRecovery()
    {
        _publishedDevices = Array.AsReadOnly(_deviceLeases.ToArray());
        if (_recoveryPath is null) return;
        var path = _recoveryPath + ".devices";
        if (_deviceLeases.Count == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = path + ".pending";
        File.WriteAllText(pending, JsonSerializer.Serialize(_deviceLeases));
        File.Move(pending, path, true);
    }

    public async Task<AudioOperationResult> RestoreConfiguredFloatAsync(string owner, string parameter,
        float expectedApplied, float prior, CancellationToken cancellationToken = default)
    {
        if (!IsSupportedFloatParameter(parameter)) return new(false, "Unsupported Banana parameter.");
        await _operations.WaitAsync(cancellationToken);
        try
        {
            if (!IsBananaConnected() || !remote.TryGetParameterFloat(parameter, out var current))
                return Failure("Restore", $"Could not read {parameter}; saved state retained.", "read current route");
            if (Math.Abs(current - prior) < .1f) return new(true, $"{parameter} was already restored.");
            if (Math.Abs(current - expectedApplied) >= .1f)
                return Failure("Restore", $"External change detected on {parameter}; it was left untouched.", "restore configured route");
            if (!remote.TrySetParameterFloat(parameter, prior) ||
                !await VerifyFloatAsync(parameter, prior, cancellationToken))
                return Failure("Restore", $"Could not verify restoration of {parameter}; saved state retained.", "restore configured route");
            return new(true, $"{parameter} restored for {owner}.");
        }
        finally { _operations.Release(); }
    }

    bool IsBananaConnected() => remote.Probe() is { Connected: true, Edition: "Banana" };

    static bool IsSupportedFloatParameter(string parameter) =>
        Enumerable.Range(0, 5).Any(strip => parameter is var p &&
            (p == $"Strip[{strip}].A1" || p == $"Strip[{strip}].B1" || p == $"Strip[{strip}].Gain"));

    static bool IsSupportedDeviceParameter(string parameter, string readback) =>
        Enumerable.Range(0, 3).Any(strip => readback == $"Strip[{strip}].device.name" &&
            (parameter == $"Strip[{strip}].device.wdm" || parameter == $"Strip[{strip}].device.mme" || parameter == $"Strip[{strip}].device.ks")) ||
        readback == "Bus[0].device.name" && (parameter == "Bus[0].device.mme" || parameter == "Bus[0].device.wdm");

    async Task<bool> VerifyFloatAsync(string parameter, float expected, CancellationToken token)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(50, token);
            if (remote.TryGetParameterFloat(parameter, out var actual) && Math.Abs(actual - expected) < .1f)
                return true;
        }
        return false;
    }

    async Task ResolveFailedWriteAsync(AudioRouteLease lease, CancellationToken token)
    {
        if (!remote.TryGetParameterFloat(lease.Resource, out var current)) return;
        if (Math.Abs(current - lease.PriorState) < .1f)
        {
            _leases.Remove(lease);
            SaveRecovery();
        }
        else if (Math.Abs(current - lease.AppliedState) < .1f &&
                 remote.TrySetParameterFloat(lease.Resource, lease.PriorState) &&
                 await VerifyFloatAsync(lease.Resource, lease.PriorState, token))
        {
            _leases.Remove(lease);
            SaveRecovery();
        }
    }

    void SaveRecovery()
    {
        _publishedRoutes = Array.AsReadOnly(_leases.ToArray());
        if (_recoveryPath is null) return;
        if (_leases.Count == 0)
        {
            if (File.Exists(_recoveryPath)) File.Delete(_recoveryPath);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_recoveryPath)!);
        var pending = _recoveryPath + ".pending";
        File.WriteAllText(pending, JsonSerializer.Serialize(_leases));
        File.Move(pending, _recoveryPath, true);
    }

    AudioOperationResult SetState(AudioBridgeConnectionState state, string detail, bool success)
    {
        if (_state != state && state is AudioBridgeConnectionState.Faulted or AudioBridgeConnectionState.UnsupportedEdition)
            Failure("Connection", detail, "establish Banana engine");
        _state = state;
        _detail = detail;
        return new(success, detail);
    }

    AudioOperationResult Failure(string subsystem, string detail, string action)
    {
        lock (_faults)
        {
            _faults.Enqueue(new(_clock.GetUtcNow(), subsystem, detail, action, "Needs attention"));
            while (_faults.Count > 32) _faults.Dequeue();
        }
        return new(false, detail);
    }
}
