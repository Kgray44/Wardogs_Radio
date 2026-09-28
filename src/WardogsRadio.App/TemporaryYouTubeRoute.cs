using System.Diagnostics;
using System.IO;
using System.Text.Json;
using NAudio.CoreAudioApi;
using WardogsRadio.Voicemeeter;

namespace WardogsRadio.App;

/// <summary>
/// Temporarily sends Windows-default playback into Voicemeeter's VAIO strip.
/// The VAIO strip goes to A1/headphones, never directly to B1; WARDOGS's
/// process-specific copy separately feeds AUX/B1. Holding the B1 test turns
/// only VAIO/A1 off, leaving the B1 copy audible through the preview player.
/// </summary>
internal sealed class TemporaryYouTubeRoute(AudioBridgeService bridge) : IDisposable
{
    const int DefaultVirtualStrip = 3; // Voicemeeter Banana: first virtual input.
    readonly string _recoveryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WARDOGS Radio", "youtube-route-recovery.json");
    Snapshot? _snapshot;
    bool _auditioning;

    // Keep timing visible for station-startup diagnostics.
    public TimeSpan LastBeginDuration { get; private set; }
    public TimeSpan LastRestoreDuration { get; private set; }

    internal sealed record Snapshot(string PriorConsole, string PriorMultimedia, string VirtualOutput,
        float PriorA1, float PriorB1, float PriorVirtualGain, string PriorA1Device, string? PriorA1Driver,
        string ChosenHeadsetName, float? AppliedA1 = null, float? AppliedVirtualGain = null,
        string? PendingHeadsetName = null);

    public bool IsActive => _snapshot is not null;
    public bool IsAuditioning => _auditioning;
    public string? CurrentHeadsetName => _snapshot?.ChosenHeadsetName;

    public void SwitchHeadset(string endpointId)
    {
        var current = _snapshot ?? throw new InvalidOperationException("The YouTube listening route is not active.");
        if (WindowsDefaultWasOverridden)
            throw new InvalidOperationException("Windows output was changed manually; WARDOGS will not reclaim it.");
        using var endpoints = new MMDeviceEnumerator();
        using var endpoint = endpoints.GetDevice(endpointId.Split('\\').Last());
        if (endpoint.DataFlow != DataFlow.Render || endpoint.State != DeviceState.Active)
            throw new InvalidOperationException("The requested listening output is unavailable.");
        var choice = VoicemeeterSharedOutputSelector.Find(bridge.ListAudioDevices(false), endpoint.FriendlyName)
            ?? throw new InvalidOperationException("A shared Voicemeeter output could not be matched to this device.");
        if (choice.Name.Equals(current.ChosenHeadsetName, StringComparison.OrdinalIgnoreCase)) return;
        SaveSnapshot(current with { PendingHeadsetName = choice.Name });
        try
        {
            SetVerifiedDevice("Bus[0].device." + choice.InterfaceName.ToLowerInvariant(), choice.Name,
                "Could not switch YouTube listening output.");
            SaveSnapshot(current with { ChosenHeadsetName = choice.Name, PendingHeadsetName = null });
        }
        catch
        {
            var former = bridge.ListAudioDevices(false).FirstOrDefault(x =>
                x.Name.Equals(current.ChosenHeadsetName, StringComparison.OrdinalIgnoreCase));
            if (former is not null && bridge.TrySetDeviceVerified("Bus[0].device." + former.InterfaceName.ToLowerInvariant(),
                    "Bus[0].device.name", current.ChosenHeadsetName))
                SaveSnapshot(current);
            throw;
        }
    }

    public bool WindowsDefaultWasOverridden => _snapshot is { } snapshot &&
        (!WindowsDefaultRender.Current(Role.Console).Equals(snapshot.VirtualOutput, StringComparison.OrdinalIgnoreCase) ||
         !WindowsDefaultRender.Current(Role.Multimedia).Equals(snapshot.VirtualOutput, StringComparison.OrdinalIgnoreCase));

    public bool TryCheckHealth(out string issue)
    {
        issue = "";
        if (_snapshot is not { } snapshot) return true;
        if (WindowsDefaultWasOverridden)
            issue = "Windows playback output was changed by the owner.";
        else if (!bridge.TryReadString("Bus[0].device.name", out var device) ||
                 !device.Equals(snapshot.ChosenHeadsetName, StringComparison.OrdinalIgnoreCase))
            issue = "Voicemeeter's headset output changed.";
        else if (!bridge.TryReadFloat($"Strip[{DefaultVirtualStrip}].A1", out var a1) ||
                 Math.Abs(a1 - (_auditioning ? 0 : 1)) > .1f)
            issue = "Voicemeeter stopped sending Windows audio to your headset.";
        else if (!bridge.TryReadFloat($"Strip[{DefaultVirtualStrip}].B1", out var b1) ||
                 Math.Abs(b1) > .1f)
            issue = "Windows audio is no longer isolated from the game bus.";
        return issue.Length == 0;
    }

    public void Begin(string headsetEndpointId, double headsetGain)
    {
        if (IsActive) return;
        if (!bridge.RecoveryReady)
            throw new InvalidOperationException("Audio Bridge recovery must be resolved before opening a new YouTube route.");
        var stopwatch = Stopwatch.StartNew();
        try
        {
        if (bridge.Probe() is not { Connected: true, Edition: "Banana" })
            throw new InvalidOperationException("Isolated YouTube B1 testing requires connected Voicemeeter Banana.");
        if (!bridge.TryReadFloat($"Strip[{DefaultVirtualStrip}].A1", out var priorA1) ||
            !bridge.TryReadFloat($"Strip[{DefaultVirtualStrip}].B1", out var priorB1) ||
            !bridge.TryReadFloat($"Strip[{DefaultVirtualStrip}].Gain", out var priorVirtualGain) ||
            !bridge.TryReadString("Bus[0].device.name", out var priorDevice))
            throw new InvalidOperationException("Cannot read the existing Voicemeeter listening route; no output changed.");

        using var endpoints = new MMDeviceEnumerator();
        using var headset = endpoints.GetDevice(headsetEndpointId.Split('\\').Last());
        if (headset.DataFlow != DataFlow.Render || headset.State != DeviceState.Active)
            throw new InvalidOperationException("The chosen headphones are not an active playback output.");
        var virtualOutput = endpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .SingleOrDefault(x => x.FriendlyName.StartsWith("Voicemeeter Input (", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Voicemeeter VAIO Windows playback endpoint is unavailable.");
        // Voicemeeter truncates long MME device names. Match that prefix, but
        // never fall back to WDM/KS: they may seize the physical output.
        var headsetChoice = VoicemeeterSharedOutputSelector.Find(bridge.ListAudioDevices(false), headset.FriendlyName)
            ?? throw new InvalidOperationException("No shared MME route matches the selected headphones. Your Windows output was not changed.");
        var priorDriver = bridge.ListAudioDevices(false)
            .FirstOrDefault(x => x.Name.Equals(priorDevice, StringComparison.OrdinalIgnoreCase))?.InterfaceName.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(priorDevice) && priorDriver is not ("mme" or "wdm"))
            throw new InvalidOperationException("Voicemeeter's current A1 device cannot be identified for safe restoration; no output changed.");
        var snapshot = new Snapshot(WindowsDefaultRender.Current(Role.Console),
            WindowsDefaultRender.Current(Role.Multimedia), virtualOutput.ID,
            priorA1, priorB1, priorVirtualGain, priorDevice, priorDriver, headsetChoice.Name);

        SaveSnapshot(snapshot);
        try
        {
            // Never expose general Windows-default playback to the game bus.
            SetVerifiedFloat($"Strip[{DefaultVirtualStrip}].B1", 0, "Could not isolate VAIO from B1.");
            SetVerifiedDevice("Bus[0].device." + headsetChoice.InterfaceName.ToLowerInvariant(), headsetChoice.Name,
                "Could not choose your headphones as Voicemeeter A1.");
            SetVerifiedFloat($"Strip[{DefaultVirtualStrip}].A1", 1, "Could not send VAIO to the headset bus.");
            SetHeadsetGain(headsetGain);
            WindowsDefaultRender.Set(virtualOutput.ID, Role.Console);
            WindowsDefaultRender.Set(virtualOutput.ID, Role.Multimedia);
        }
        catch
        {
            try { End(); } catch { /* Recovery file remains for next startup. */ }
            throw;
        }
        }
        finally { LastBeginDuration = stopwatch.Elapsed; }
    }

    public void SetAuditioning(bool enabled)
    {
        if (_snapshot is null) throw new InvalidOperationException("The temporary YouTube route is not active.");
        if (_auditioning == enabled) return;
        SaveSnapshot(_snapshot with { AppliedA1 = enabled ? 0 : 1 });
        SetVerifiedFloat($"Strip[{DefaultVirtualStrip}].A1", enabled ? 0 : 1,
            enabled ? "Could not mute direct YouTube listening." : "Could not restore direct YouTube listening.");
        _auditioning = enabled;
    }

    public void SetHeadsetGain(double gain)
    {
        if (_snapshot is null) throw new InvalidOperationException("The temporary YouTube route is not active.");
        if (!double.IsFinite(gain)) throw new ArgumentOutOfRangeException(nameof(gain));
        var normalized = Math.Clamp(gain, 0, 1);
        var db = normalized == 0 ? -60f : (float)(20 * Math.Log10(normalized));
        SaveSnapshot(_snapshot with { AppliedVirtualGain = db });
        SetVerifiedFloat($"Strip[{DefaultVirtualStrip}].Gain", db, "Could not set the YouTube headset level in Voicemeeter.");
    }

    /// <summary>
    /// Attempts to restore every output changed by <see cref="Begin"/>. A failed
    /// restoration deliberately leaves the recovery record in place so the next
    /// launch can retry it; callers can keep the radio running and show the
    /// owner exactly what still needs attention.
    /// </summary>
    public bool TryEnd(out string message)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            message = "No temporary YouTube route is active.";
            return true;
        }

        try
        {
            // Respect a manual default-output change made by the owner meanwhile.
            var consoleOwned = WindowsDefaultRender.Current(Role.Console).Equals(snapshot.VirtualOutput, StringComparison.OrdinalIgnoreCase);
            var multimediaOwned = WindowsDefaultRender.Current(Role.Multimedia).Equals(snapshot.VirtualOutput, StringComparison.OrdinalIgnoreCase);
            if (consoleOwned)
                WindowsDefaultRender.Set(snapshot.PriorConsole, Role.Console);
            if (multimediaOwned)
                WindowsDefaultRender.Set(snapshot.PriorMultimedia, Role.Multimedia);
            RestoreMixer(snapshot);
            message = consoleOwned && multimediaOwned ? "Temporary YouTube route restored." :
                "Voicemeeter route restored; Windows default output changed outside WARDOGS and was left untouched.";
        }
        catch (Exception error)
        {
            _auditioning = false;
            message = error.Message;
            return false;
        }

        _snapshot = null;
        _auditioning = false;
        if (File.Exists(_recoveryPath)) File.Delete(_recoveryPath);
        return true;
        }
        finally { LastRestoreDuration = stopwatch.Elapsed; }
    }

    // Keep cleanup callers exception-safe; detailed handling should use TryEnd.
    public void End() => TryEnd(out _);

    void RestoreMixer(Snapshot snapshot)
    {
        RestoreOwnedFloat($"Strip[{DefaultVirtualStrip}].A1", snapshot.AppliedA1 ?? 1, snapshot.PriorA1);
        RestoreOwnedFloat($"Strip[{DefaultVirtualStrip}].B1", 0, snapshot.PriorB1);
        if (snapshot.AppliedVirtualGain is { } appliedGain)
            RestoreOwnedFloat($"Strip[{DefaultVirtualStrip}].Gain", appliedGain, snapshot.PriorVirtualGain);
        else if (!bridge.TryReadFloat($"Strip[{DefaultVirtualStrip}].Gain", out var currentGain) ||
                 Math.Abs(currentGain - snapshot.PriorVirtualGain) > .1f)
            throw new InvalidOperationException("VAIO gain changed without a recorded owned value; recovery needs review.");
        if (!bridge.TryReadString("Bus[0].device.name", out var currentDevice))
            throw new InvalidOperationException("Could not read current A1 output; recovery record retained.");
        if (currentDevice.Equals(snapshot.PriorA1Device, StringComparison.OrdinalIgnoreCase)) return;
        if (!currentDevice.Equals(snapshot.ChosenHeadsetName, StringComparison.OrdinalIgnoreCase) &&
            !currentDevice.Equals(snapshot.PendingHeadsetName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("A1 output changed outside WARDOGS; it was left untouched. Recovery record retained for review.");
        if (string.IsNullOrWhiteSpace(snapshot.PriorA1Device))
        {
            // Clear the interface we selected first. Some Voicemeeter builds
            // only clear A1 when the matching driver property is addressed.
            Require(bridge.TryClearA1(), "Could not clear the temporary A1 headphones.");
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.PriorA1Driver))
        {
            SetVerifiedDevice("Bus[0].device." + snapshot.PriorA1Driver, snapshot.PriorA1Device,
                "Could not restore the prior A1 output device.");
        }
    }

    void RestoreOwnedFloat(string parameter, float applied, float prior)
    {
        if (!bridge.TryReadFloat(parameter, out var current))
            throw new InvalidOperationException($"Could not read {parameter}; recovery record retained.");
        if (Math.Abs(current - prior) < .1f) return;
        if (Math.Abs(current - applied) >= .1f)
            throw new InvalidOperationException($"{parameter} changed outside WARDOGS; it was left untouched. Recovery record retained for review.");
        SetVerifiedFloat(parameter, prior, $"Could not restore {parameter}.");
    }

    void SetVerifiedFloat(string parameter, float desired, string error)
    {
        Require(bridge.TrySetFloatVerified(parameter, desired),
            error + " Voicemeeter readback did not match; recovery record retained.");
    }

    void SetVerifiedDevice(string parameter, string desired, string error)
    {
        Require(bridge.TrySetDeviceVerified(parameter, "Bus[0].device.name", desired),
            error + " Voicemeeter readback did not match; recovery record retained.");
    }

    void SaveSnapshot(Snapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_recoveryPath)!);
        var pending = _recoveryPath + ".pending";
        File.WriteAllText(pending, JsonSerializer.Serialize(snapshot));
        File.Move(pending, _recoveryPath, true);
        _snapshot = snapshot;
    }

    public bool TryRecover(out string message)
    {
        message = "No temporary YouTube route to recover.";
        if (!File.Exists(_recoveryPath)) return true;
        try
        {
            var saved = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_recoveryPath))
                ?? throw new InvalidOperationException("Recovery record is empty.");
            if (string.IsNullOrWhiteSpace(saved.PriorConsole) ||
                string.IsNullOrWhiteSpace(saved.PriorMultimedia) ||
                string.IsNullOrWhiteSpace(saved.VirtualOutput) ||
                saved.PriorA1Device is null || string.IsNullOrWhiteSpace(saved.ChosenHeadsetName) ||
                saved.PriorA1Driver is not (null or "mme" or "wdm") ||
                !float.IsFinite(saved.PriorA1) || !float.IsFinite(saved.PriorB1) ||
                !float.IsFinite(saved.PriorVirtualGain) ||
                saved.AppliedA1 is { } a1 && !float.IsFinite(a1) ||
                saved.AppliedVirtualGain is { } gain && !float.IsFinite(gain))
                throw new InvalidOperationException("Recovery record contains invalid route values; it was retained without changing audio.");
            _snapshot = saved;
            if (!TryEnd(out var error))
            {
                message = "TEMPORARY AUDIO ROUTE NEEDS RESTORATION · " + error;
                return false;
            }
            message = error == "Temporary YouTube route restored."
                ? "Restored the Windows and Voicemeeter outputs after an interrupted YouTube session."
                : error;
            return true;
        }
        catch (Exception error)
        {
            message = "TEMPORARY AUDIO ROUTE NEEDS RESTORATION · " + error.Message;
            return false;
        }
    }

    static void Require(bool success, string error)
    {
        if (!success) throw new InvalidOperationException(error);
    }

    public void Dispose() => End();
}
