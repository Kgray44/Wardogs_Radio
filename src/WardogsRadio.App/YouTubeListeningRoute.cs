using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using NAudio.CoreAudioApi;
using WardogsRadio.Playback;

namespace WardogsRadio.App;

/// <summary>
/// Routes only this WebView2 audio session to the selected Windows endpoint.
/// Source volume stays at unity; Windows session volume controls listening,
/// leaving process-loopback game-feed gain independent. No system default changes.
/// </summary>
internal sealed class YouTubeListeningRoute(string configurationRoot)
{
    readonly string _recoveryPath = Path.Combine(configurationRoot, "youtube-listening-policy-recovery.json");
    Snapshot? _snapshot;

    sealed record Snapshot(uint ProcessId, long ProcessStartTicks, string? PriorConsole,
        string? PriorMultimedia, string AppliedEndpoint, string RawEndpoint,
        float PriorVolume, float AppliedVolume, string? PreviousAppliedEndpoint = null);

    public bool IsActive => _snapshot is not null;
    public string? CurrentEndpointId => _snapshot?.RawEndpoint;

    public bool TryCheckHealth(out string issue)
    {
        issue = "";
        if (_snapshot is not { } snapshot) return true;
        try
        {
            using var policy = new WindowsPerAppAudioPolicy();
            RequirePolicy(policy, snapshot);
            if (!TrySession(snapshot.ProcessId, snapshot.RawEndpoint, out _))
                throw new InvalidOperationException("YouTube audio is no longer on the selected output.");
            return true;
        }
        catch (Exception error)
        {
            issue = error.Message;
            return false;
        }
    }

    public async Task StartAsync(CoreWebView2 webView, string endpointId, double listeningGain)
    {
        if (_snapshot is not null)
        {
            if (SameEndpoint(_snapshot.RawEndpoint, endpointId))
            {
                SetVolume(listeningGain);
                return;
            }
            throw new InvalidOperationException("The previous YouTube listening output must be released first.");
        }
        if (!TryRecover(out var recovery)) throw new InvalidOperationException(recovery);
        var rawEndpoint = ActiveRenderEndpoint(endpointId);
        var deadline = DateTime.UtcNow.AddSeconds(3);
        uint? pid = null;
        while (DateTime.UtcNow < deadline)
        {
            var webViewPids = webView.Environment.GetProcessInfos()
                .Select(info => (uint)info.ProcessId).ToHashSet();
            var sessionPids = AudioSessions(webViewPids).Select(session => session.Pid).Distinct().ToArray();
            if (sessionPids.Length == 1) { pid = sessionPids[0]; break; }
            if (sessionPids.Length > 1)
                throw new InvalidOperationException("More than one WebView audio process is active; WARDOGS cannot safely choose one.");
            await Task.Delay(100);
        }
        if (pid is null)
            throw new InvalidOperationException("The WebView audio session did not open before playback. The player remains silent; press Play to retry.");
        using var process = Process.GetProcessById((int)pid.Value);
        var processStart = process.StartTime.ToUniversalTime().Ticks;
        var session = AudioSessions([pid.Value]).First();
        using var policy = new WindowsPerAppAudioPolicy();
        var target = WindowsPerAppAudioPolicy.FormatEndpoint(rawEndpoint);
        var snapshot = new Snapshot(pid.Value, processStart, policy.Read(pid.Value, 0),
            policy.Read(pid.Value, 1), target, rawEndpoint, session.Volume, session.Volume);
        Save(snapshot);
        try
        {
            policy.Set(pid.Value, 0, target);
            policy.Set(pid.Value, 1, target);
            RequirePolicy(policy, snapshot);
            if (!await WaitForSessionEndpointAsync(pid.Value, rawEndpoint))
                throw new InvalidOperationException("Windows did not move the YouTube audio session to the selected output.");
            SetVolume(listeningGain);
        }
        catch
        {
            TryEnd(out _);
            throw;
        }
    }

    public async Task SwitchAsync(string endpointId)
    {
        var prior = _snapshot ?? throw new InvalidOperationException("YouTube listening is not connected yet.");
        var rawEndpoint = ActiveRenderEndpoint(endpointId);
        if (SameEndpoint(rawEndpoint, prior.RawEndpoint)) return;
        using var policy = new WindowsPerAppAudioPolicy();
        RequirePolicy(policy, prior);
        var next = prior with
        {
            AppliedEndpoint = WindowsPerAppAudioPolicy.FormatEndpoint(rawEndpoint),
            RawEndpoint = rawEndpoint,
            PreviousAppliedEndpoint = prior.AppliedEndpoint
        };
        // Save before changing Windows so a crash still has the original policy.
        Save(next);
        try
        {
            policy.Set(prior.ProcessId, 0, next.AppliedEndpoint);
            policy.Set(prior.ProcessId, 1, next.AppliedEndpoint);
            RequirePolicy(policy, next);
            if (!await WaitForSessionEndpointAsync(prior.ProcessId, rawEndpoint))
                throw new InvalidOperationException("Windows did not confirm the new YouTube listening output.");
            SetVolume(next.AppliedVolume);
            Save(next with { PreviousAppliedEndpoint = null });
        }
        catch
        {
            policy.Set(prior.ProcessId, 0, prior.AppliedEndpoint);
            policy.Set(prior.ProcessId, 1, prior.AppliedEndpoint);
            Save(prior);
            if (!await WaitForSessionEndpointAsync(prior.ProcessId, prior.RawEndpoint))
                throw new InvalidOperationException("The output switch failed and the previous YouTube output could not be verified.");
            throw;
        }
    }

    public void SetVolume(double gain)
    {
        var snapshot = _snapshot ?? throw new InvalidOperationException("YouTube listening is not connected yet.");
        var value = (float)Math.Clamp(gain, 0, 1);
        if (!float.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(gain));
        if (!TrySession(snapshot.ProcessId, snapshot.RawEndpoint, out var session))
            throw new InvalidOperationException("The YouTube audio session is no longer on the selected output.");
        var former = session.SimpleAudioVolume.Volume;
        Save(snapshot with { AppliedVolume = value });
        try
        {
            session.SimpleAudioVolume.Volume = value;
            if (Math.Abs(session.SimpleAudioVolume.Volume - value) > .02f)
                throw new InvalidOperationException("Windows did not confirm the YouTube listening level.");
        }
        catch
        {
            try { session.SimpleAudioVolume.Volume = former; Save(snapshot); } catch { /* Journal retains the pending value. */ }
            throw;
        }
    }

    public bool TryEnd(out string detail)
    {
        var snapshot = _snapshot;
        if (snapshot is null) { detail = "No YouTube listening policy is active."; return true; }
        try
        {
            using var policy = new WindowsPerAppAudioPolicy();
            if (TrySession(snapshot.ProcessId, snapshot.RawEndpoint, out var session) &&
                Math.Abs(session.SimpleAudioVolume.Volume - snapshot.AppliedVolume) < .02f)
                session.SimpleAudioVolume.Volume = snapshot.PriorVolume;
            RestoreRole(policy, snapshot, 0, snapshot.PriorConsole);
            RestoreRole(policy, snapshot, 1, snapshot.PriorMultimedia);
            _snapshot = null;
            if (File.Exists(_recoveryPath)) File.Delete(_recoveryPath);
            detail = "YouTube listening policy restored.";
            return true;
        }
        catch (Exception error)
        {
            detail = "YouTube listening policy needs restoration: " + error.Message;
            return false;
        }
    }

    public bool TryRecover(out string detail)
    {
        if (_snapshot is null && File.Exists(_recoveryPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(_recoveryPath))
                    ?? throw new InvalidOperationException("Recovery record is empty.");
                if (saved.ProcessId == 0 || saved.ProcessStartTicks <= 0 ||
                    string.IsNullOrWhiteSpace(saved.AppliedEndpoint) ||
                    string.IsNullOrWhiteSpace(saved.RawEndpoint))
                    throw new InvalidOperationException("Recovery record is invalid.");
                // A PID may be reused after a crash. Never modify a different process.
                try
                {
                    using var process = Process.GetProcessById((int)saved.ProcessId);
                    if (process.StartTime.ToUniversalTime().Ticks != saved.ProcessStartTicks)
                        throw new InvalidOperationException("The original WebView process ID was reused; policy was left untouched.");
                }
                catch (ArgumentException) { /* The original process exited. */ }
                _snapshot = saved;
            }
            catch (Exception error)
            {
                detail = "YouTube listening recovery needs review: " + error.Message;
                return false;
            }
        }
        return TryEnd(out detail);
    }

    static void RestoreRole(WindowsPerAppAudioPolicy policy, Snapshot snapshot, int role, string? prior)
    {
        var current = policy.Read(snapshot.ProcessId, role);
        if (string.Equals(current, prior, StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(current, snapshot.AppliedEndpoint, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(current, snapshot.PreviousAppliedEndpoint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The per-app output changed outside WARDOGS; it was left untouched.");
        policy.Set(snapshot.ProcessId, role, prior);
        if (!string.Equals(policy.Read(snapshot.ProcessId, role), prior, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Prior per-app output did not read back after restoration.");
    }

    static void RequirePolicy(WindowsPerAppAudioPolicy policy, Snapshot snapshot)
    {
        if (!string.Equals(policy.Read(snapshot.ProcessId, 0), snapshot.AppliedEndpoint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(policy.Read(snapshot.ProcessId, 1), snapshot.AppliedEndpoint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("YouTube per-app output policy did not read back.");
    }

    static string ActiveRenderEndpoint(string id)
    {
        using var endpoints = new MMDeviceEnumerator();
        var matches = endpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Where(endpoint => SameEndpoint(endpoint.ID, id)).ToList();
        if (matches.Count != 1)
            throw new InvalidOperationException("The selected listening output is unavailable or ambiguous.");
        return matches[0].ID;
    }

    static bool SameEndpoint(string first, string second) =>
        AudioDeviceIdentity.SameEndpoint(first, second);

    static List<(uint Pid, string EndpointId, float Volume)> AudioSessions(HashSet<uint> pids)
    {
        using var endpoints = new MMDeviceEnumerator();
        var result = new List<(uint, string, float)>();
        foreach (var endpoint in endpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            for (var index = 0; index < endpoint.AudioSessionManager.Sessions.Count; index++)
            {
                var session = endpoint.AudioSessionManager.Sessions[index];
                if (pids.Contains(session.GetProcessID))
                    result.Add((session.GetProcessID, endpoint.ID, session.SimpleAudioVolume.Volume));
            }
        return result;
    }

    static bool TrySession(uint pid, string endpointId, out AudioSessionControl session)
    {
        using var endpoints = new MMDeviceEnumerator();
        foreach (var endpoint in endpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            if (!SameEndpoint(endpoint.ID, endpointId)) continue;
            for (var index = 0; index < endpoint.AudioSessionManager.Sessions.Count; index++)
            {
                var candidate = endpoint.AudioSessionManager.Sessions[index];
                if (candidate.GetProcessID != pid) continue;
                session = candidate;
                return true;
            }
        }
        session = null!;
        return false;
    }

    static async Task<bool> WaitForSessionEndpointAsync(uint pid, string endpointId)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            if (TrySession(pid, endpointId, out _)) return true;
            await Task.Delay(100);
        }
        return false;
    }

    void Save(Snapshot snapshot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_recoveryPath)!);
        var pending = _recoveryPath + ".pending";
        File.WriteAllText(pending, JsonSerializer.Serialize(snapshot));
        File.Move(pending, _recoveryPath, true);
        _snapshot = snapshot;
    }
}
