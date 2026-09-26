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
internal sealed class TemporaryYouTubeRoute(VoicemeeterRemote mixer) : IDisposable
{
    const int DefaultVirtualStrip = 3; // Voicemeeter Banana: first virtual input.
    readonly string _recoveryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WARDOGS Radio", "youtube-route-recovery.json");
    Snapshot? _snapshot;
    bool _auditioning;

    internal sealed record Snapshot(string PriorConsole, string PriorMultimedia, string VirtualOutput,
        float PriorA1, float PriorB1, float PriorVirtualGain, string PriorA1Device, string? PriorA1Driver,
        string ChosenHeadsetName);

    public bool IsActive => _snapshot is not null;
    public bool IsAuditioning => _auditioning;

    public bool TryCheckHealth(out string issue)
    {
        issue = "";
        if (_snapshot is not { } snapshot) return true;
        if (!WindowsDefaultRender.Current(Role.Console).Equals(snapshot.VirtualOutput, StringComparison.OrdinalIgnoreCase) ||
            !WindowsDefaultRender.Current(Role.Multimedia).Equals(snapshot.VirtualOutput, StringComparison.OrdinalIgnoreCase))
            issue = "Windows playback route changed.";
        else if (!mixer.TryGetParameterString("Bus[0].device.name", out var device) ||
                 !device.Equals(snapshot.ChosenHeadsetName, StringComparison.OrdinalIgnoreCase))
            issue = "Voicemeeter's headset output changed.";
        else if (!mixer.TryGetParameterFloat($"Strip[{DefaultVirtualStrip}].A1", out var a1) ||
                 Math.Abs(a1 - (_auditioning ? 0 : 1)) > .1f)
            issue = "Voicemeeter stopped sending Windows audio to your headset.";
        else if (!mixer.TryGetParameterFloat($"Strip[{DefaultVirtualStrip}].B1", out var b1) ||
                 Math.Abs(b1) > .1f)
            issue = "Windows audio is no longer isolated from the game bus.";
        return issue.Length == 0;
    }

    public void Begin(string headsetEndpointId, double headsetGain)
    {
        if (IsActive) return;
        if (mixer.Probe() is not { Connected: true, Edition: "Banana" })
            throw new InvalidOperationException("Isolated YouTube B1 testing requires connected Voicemeeter Banana.");
        if (!mixer.TryGetParameterFloat($"Strip[{DefaultVirtualStrip}].A1", out var priorA1) ||
            !mixer.TryGetParameterFloat($"Strip[{DefaultVirtualStrip}].B1", out var priorB1) ||
            !mixer.TryGetParameterFloat($"Strip[{DefaultVirtualStrip}].Gain", out var priorVirtualGain) ||
            !mixer.TryGetParameterString("Bus[0].device.name", out var priorDevice))
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
        var headsetChoice = VoicemeeterSharedOutputSelector.Find(mixer.ListAudioDevices(false), headset.FriendlyName)
            ?? throw new InvalidOperationException("No shared MME route matches the selected headphones. Your Windows output was not changed.");
        var priorDriver = mixer.ListAudioDevices(false)
            .FirstOrDefault(x => x.Name.Equals(priorDevice, StringComparison.OrdinalIgnoreCase))?.InterfaceName.ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(priorDevice) && string.IsNullOrWhiteSpace(priorDriver))
            throw new InvalidOperationException("Voicemeeter's current A1 device cannot be identified for safe restoration; no output changed.");
        var snapshot = new Snapshot(WindowsDefaultRender.Current(Role.Console),
            WindowsDefaultRender.Current(Role.Multimedia), virtualOutput.ID,
            priorA1, priorB1, priorVirtualGain, priorDevice, priorDriver, headsetChoice.Name);

        Directory.CreateDirectory(Path.GetDirectoryName(_recoveryPath)!);
        var tempPath = _recoveryPath + ".pending";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot));
        File.Move(tempPath, _recoveryPath, true);
        _snapshot = snapshot;
        try
        {
            // Never expose general Windows-default playback to the game bus.
            Require(mixer.TrySetParameterFloat($"Strip[{DefaultVirtualStrip}].B1", 0), "Could not isolate VAIO from B1.");
            WaitFloat($"Strip[{DefaultVirtualStrip}].B1", 0);
            Require(mixer.TrySetParameterString("Bus[0].device." + headsetChoice.InterfaceName.ToLowerInvariant(), headsetChoice.Name),
                "Could not choose your headphones as Voicemeeter A1.");
            WaitString("Bus[0].device.name", headsetChoice.Name);
            Require(mixer.TrySetParameterFloat($"Strip[{DefaultVirtualStrip}].A1", 1), "Could not send VAIO to the headset bus.");
            WaitFloat($"Strip[{DefaultVirtualStrip}].A1", 1);
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

    public void SetAuditioning(bool enabled)
    {
        if (_snapshot is null) throw new InvalidOperationException("The temporary YouTube route is not active.");
        if (_auditioning == enabled) return;
        Require(mixer.TrySetParameterFloat($"Strip[{DefaultVirtualStrip}].A1", enabled ? 0 : 1),
            enabled ? "Could not mute direct YouTube listening." : "Could not restore direct YouTube listening.");
        WaitFloat($"Strip[{DefaultVirtualStrip}].A1", enabled ? 0 : 1);
        _auditioning = enabled;
    }

    public void SetHeadsetGain(double gain)
    {
        if (_snapshot is null) throw new InvalidOperationException("The temporary YouTube route is not active.");
        if (!double.IsFinite(gain)) throw new ArgumentOutOfRangeException(nameof(gain));
        var normalized = Math.Clamp(gain, 0, 1);
        var db = normalized == 0 ? -60f : (float)(20 * Math.Log10(normalized));
        Require(mixer.TrySetParameterFloat($"Strip[{DefaultVirtualStrip}].Gain", db), "Could not set the YouTube headset level in Voicemeeter.");
        WaitFloat($"Strip[{DefaultVirtualStrip}].Gain", db);
    }

    public void End()
    {
        var snapshot = _snapshot;
        if (snapshot is null) return;
        // Respect a manual default-output change made by the owner meanwhile.
        if (WindowsDefaultRender.Current(Role.Console).Equals(snapshot.VirtualOutput, StringComparison.OrdinalIgnoreCase))
            WindowsDefaultRender.Set(snapshot.PriorConsole, Role.Console);
        if (WindowsDefaultRender.Current(Role.Multimedia).Equals(snapshot.VirtualOutput, StringComparison.OrdinalIgnoreCase))
            WindowsDefaultRender.Set(snapshot.PriorMultimedia, Role.Multimedia);
        RestoreMixer(snapshot);
        _snapshot = null;
        _auditioning = false;
        if (File.Exists(_recoveryPath)) File.Delete(_recoveryPath);
    }

    void RestoreMixer(Snapshot snapshot)
    {
        Require(mixer.TrySetParameterFloat($"Strip[{DefaultVirtualStrip}].A1", snapshot.PriorA1), "Could not restore VAIO A1 route.");
        WaitFloat($"Strip[{DefaultVirtualStrip}].A1", snapshot.PriorA1);
        Require(mixer.TrySetParameterFloat($"Strip[{DefaultVirtualStrip}].B1", snapshot.PriorB1), "Could not restore VAIO B1 route.");
        WaitFloat($"Strip[{DefaultVirtualStrip}].B1", snapshot.PriorB1);
        Require(mixer.TrySetParameterFloat($"Strip[{DefaultVirtualStrip}].Gain", snapshot.PriorVirtualGain), "Could not restore VAIO gain.");
        WaitFloat($"Strip[{DefaultVirtualStrip}].Gain", snapshot.PriorVirtualGain);
        if (string.IsNullOrWhiteSpace(snapshot.PriorA1Device))
        {
            // Clear the interface we selected first. Some Voicemeeter builds
            // only clear A1 when the matching driver property is addressed.
            foreach (var driver in new[] { "mme", "wdm", "ks" })
            {
                mixer.TrySetParameterString("Bus[0].device." + driver, "");
                if (mixer.TryGetParameterString("Bus[0].device.name", out var current) &&
                    string.IsNullOrWhiteSpace(current)) return;
            }
            throw new InvalidOperationException("Could not clear the temporary A1 headphones.");
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.PriorA1Driver))
        {
            Require(mixer.TrySetParameterString("Bus[0].device." + snapshot.PriorA1Driver, snapshot.PriorA1Device),
                "Could not restore the prior A1 output device.");
            WaitString("Bus[0].device.name", snapshot.PriorA1Device);
        }
    }

    public bool TryRecover(out string message)
    {
        message = "No temporary YouTube route to recover.";
        if (!File.Exists(_recoveryPath)) return true;
        try
        {
            _snapshot = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_recoveryPath))
                ?? throw new InvalidOperationException("Recovery record is empty.");
            End();
            message = "Restored the Windows and Voicemeeter outputs after an interrupted YouTube session.";
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

    void WaitFloat(string parameter, float target)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(4))
        {
            if (mixer.TryGetParameterFloat(parameter, out var actual) && Math.Abs(actual - target) < .1f) return;
            Thread.Sleep(50);
        }
        throw new InvalidOperationException($"Voicemeeter did not confirm {parameter} = {target}.");
    }

    void WaitString(string parameter, string target)
    {
        var started = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(4))
        {
            if (mixer.TryGetParameterString(parameter, out var actual) &&
                actual.Equals(target, StringComparison.OrdinalIgnoreCase)) return;
            Thread.Sleep(50);
        }
        throw new InvalidOperationException($"Voicemeeter did not confirm {parameter} = '{target}'.");
    }

    public void Dispose() => End();
}
