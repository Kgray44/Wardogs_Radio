using System.Text;
using WardogsRadio.Core;
using WardogsRadio.Input;
using WardogsRadio.Playback;
using WardogsRadio.Voicemeeter;

namespace WardogsRadio.Diagnostics;

public sealed record DiagnosticItem(string Area, string Status, string Summary, string Detail);

public sealed class DiagnosticService(IVoicemeeterRemote vm, XInputControllerService controllers)
{
    public IReadOnlyList<DiagnosticItem> Collect(AppConfiguration configuration, PlaybackSnapshot? nativePlayback = null,
        bool youtubePlayerAvailable = false, OutputHealthSnapshot? outputHealth = null)
    {
        var vmStatus = vm.Probe();
        var mpv = new MpvLocator().Find(configuration.MpvPath);
        var items = new List<DiagnosticItem>
        {
            new("Application", "READY", "WARDOGS settings are available", Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)),
            new("Voicemeeter", vmStatus.Connected ? "CONNECTED" : "NEEDS SETUP",
                vmStatus.Connected ? $"Voicemeeter connected{(vmStatus.Edition is null ? "" : $" · {vmStatus.Edition}")}; test the game voice output" :
                vmStatus.Installed ? "Voicemeeter is installed but its audio engine is not connected" : "Voicemeeter was not found",
                vmStatus.Detail),
            new("Local Music", mpv is null ? "NEEDS SETUP" : nativePlayback?.Health == ProviderHealth.Ready ? nativePlayback.IsPlaying ? "PLAYING" : "READY" : "READY",
                mpv is null ? "Local music player not found" : nativePlayback?.Health == ProviderHealth.Ready
                    ? $"{nativePlayback.Track?.Title ?? "Station"} · {(nativePlayback.IsPlaying ? "playing" : "ready")}; check your headphones and B1 meters"
                    : "Local music player available; tune a station to test playback",
                mpv ?? "Install mpv or choose its executable in Settings."),
            new("YouTube", youtubePlayerAvailable ? "READY" : "NEEDS SETUP",
                youtubePlayerAvailable ? "YouTube player available; tune a video to test playback" : "YouTube player unavailable",
                youtubePlayerAvailable ? "WebView2 initialized. A specific video's playback and audio still need to be tested." : "WebView2 failed to initialize. Check the runtime installation."),
            new("SoundCloud", "NEEDS SETUP", "Account connection required", "Official API credentials and authorization are not configured."),
            new("Apple Music", "NEEDS SETUP", "Account connection required", "MusicKit developer configuration and authorization are not configured.")
        };

        var found = controllers.Enumerate().ToList();
        items.Add(OutputHealthDiagnostic(configuration.ClipGuard, outputHealth));
        var limiter = new VoicemeeterStripLimiter(vm).Probe(configuration.MusicStripIndex);
        items.Add(new("Voicemeeter limiter", configuration.ClipGuard.LimiterEnabled && limiter.Available ? "READY" :
            limiter.Available ? "AVAILABLE" : "UNAVAILABLE",
            configuration.ClipGuard.LimiterEnabled ? "Selected music-strip limiter requested" : "Selected music-strip limiter not enabled",
            limiter.Detail + " WARDOGS writes only after the user enables the control and always verifies the readback."));
        items.Add(new("Controllers", found.Count == 0 ? "NEEDS SETUP" : "CONNECTED",
            found.Count == 0 ? "No game controllers found" : $"{found.Count} game controller{(found.Count == 1 ? "" : "s")} found",
            found.Count == 0 ? "Connect a joystick, gamepad, or multi-axis controller and scan again." : "Open technical details for the device list. Test an actual control press in the Macro editor to verify binding."));
        foreach (var controller in found)
            items.Add(new("Controller", controller.Connected ? "CONNECTED" : "DISCONNECTED",
                $"{controller.Name} · {controller.Kind} · {controller.ButtonCount?.ToString() ?? "?"} buttons · {controller.AxisCount?.ToString() ?? "?"} axes · {controller.PovCount?.ToString() ?? "?"} POV",
                $"Input backend: {controller.Kind}; button capability records: {controller.ButtonCapabilityCount?.ToString() ?? "unknown"}; value capability records: {controller.ValueCapabilityCount?.ToString() ?? "unknown"}; VID/PID {(controller.VendorId is ushort vid ? $"{vid:X4}" : "----")}/{(controller.ProductId is ushort pid ? $"{pid:X4}" : "----")}; device ID: {controller.DeviceId}"));

        var library = configuration.MusicLibrary ?? new MusicLibrary();
        var sourceIds = library.Sources.Select(source => source.Id).ToHashSet();
        var songIds = library.Songs.Select(song => song.Id).ToHashSet();
        var brokenSongs = library.Songs.Count(song => !sourceIds.Contains(song.SourceId));
        var brokenEntries = configuration.Profile.Stations.Sum(station => station.PlaylistEntries.Count(entry => !songIds.Contains(entry.SongId)));
        var missingLocal = library.Sources.Count(source => source.ProviderId == "mpv" &&
            !Uri.TryCreate(source.Source, UriKind.Absolute, out _) && !File.Exists(source.Source));
        var assignments = configuration.Profile.Stations.Sum(station => station.PlaylistEntries.Count);
        var libraryStatus = brokenSongs == 0 && brokenEntries == 0 ? missingLocal == 0 ? "READY" : "WARNING" : "WARNING";
        items.Add(new("Music Library", libraryStatus,
            $"{library.Sources.Count} sources · {library.Songs.Count} song cues · {assignments} station assignments",
            $"Broken song references: {brokenSongs}; broken station entries: {brokenEntries}; missing local sources: {missingLocal}."));

        foreach (var station in configuration.Profile.Stations.OrderBy(x => x.Order))
        {
            var status = !station.Enabled ? "DISABLED" : string.IsNullOrWhiteSpace(station.Source) ? "NEEDS SETUP" : "CONFIGURED";
            var summary = !station.Enabled ? $"{station.Name} is disabled" : string.IsNullOrWhiteSpace(station.Source) ? $"{station.Name} needs a source" : $"{station.Name} has a source";
            items.Add(new("Station", status, summary, $"Provider: {station.ProviderId}; source: {station.Source}; playlist entries: {station.PlaylistEntries.Count}."));
        }
        return items;
    }

    static DiagnosticItem OutputHealthDiagnostic(ClipGuardSettings settings, OutputHealthSnapshot? health)
    {
        if (health is null)
            return new("Clip Guard", "NOT TESTED", "No output-health sample has been collected yet",
                "Open Dashboard or Audio & Routing while Voicemeeter is connected. The selected-strip limiter is opt-in and its live capability is listed separately.");
        var status = health.State switch
        {
            OutputHealthState.Unavailable => "WARNING",
            OutputHealthState.Safe or OutputHealthState.Healthy => "READY",
            OutputHealthState.Protected => "PROTECTED",
            _ => "WARNING"
        };
        var peak = health.GameBus.PeakDbfs is { } db && !double.IsNegativeInfinity(db) ? $"{db:0.0} dBFS" : "unavailable/quiet";
        var events = health.RecentEvents.Count == 0 ? "No recent events." : string.Join(" | ", health.RecentEvents.TakeLast(3).Select(x => $"{x.Timestamp:HH:mm:ss} {x.Kind}: {x.Detail}"));
        var lastClip = health.LastClipAt is { } at
            ? $" Last clip: {at:O}; music {Display(health.LastClipMusicDbfs)}; microphone {Display(health.LastClipMicrophoneDbfs)}; B1 {Display(health.LastClipGameDbfs)}; assessment: {health.LastClipDiagnosis ?? "insufficient evidence to classify"}."
            : " No clip has been observed in this session.";
        return new("Clip Guard", status, $"{health.State} · B1 {peak}",
            $"Mode: {settings.Mode}; preset: {settings.Preset}; ceiling: {settings.SafetyCeilingDbfs:0.0} dBFS; near clip: {settings.NearClipThresholdDbfs:0.0} dBFS; " +
            $"runtime game-music reduction: {health.ProtectionReductionDb:0.0} dB; session peak: {Display(health.SessionPeakDbfs)}; maximum reduction: {health.SessionMaximumReductionDb:0.0} dB; near clips: {health.NearClipEvents}; clips: {health.ClipEvents}; interventions: {health.ProtectionInterventions}; " +
            $"telemetry music/microphone/B1: {(health.Music.Available ? "available" : "unknown")}/{(health.Microphone.Available ? "available" : "unknown")}/{(health.GameBus.Available ? "available" : "unknown")}; independent game-music path: {(health.IndependentGameMusicPathAvailable ? "available" : "not available")}; automatic protection currently {(health.AutoProtectionAvailable ? "active" : "paused")}. " +
            $"Strip limiter preference: {(settings.LimiterEnabled ? "enabled; see the Limiter diagnostic for live readback" : "disabled; WARDOGS does not change the mixer limiter")}." + lastClip + " " + events);
    }

    static string Display(double? dbfs) => dbfs is { } value && !double.IsNegativeInfinity(value) ? $"{value:0.0} dBFS" : "unknown/quiet";

    public async Task<string> ExportAsync(AppConfiguration configuration, string directory, PlaybackSnapshot? nativePlayback = null,
        bool youtubePlayerAvailable = false, OutputHealthSnapshot? outputHealth = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"WARDOGS-Radio-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        var report = new StringBuilder("# WARDOGS Radio Diagnostic Report\n\n").AppendLine($"Generated: {DateTimeOffset.Now:O}\n");
        foreach (var item in Collect(configuration, nativePlayback, youtubePlayerAvailable, outputHealth))
            report.AppendLine($"- **{item.Area} / {item.Status}** — {item.Summary}  ").AppendLine($"  {item.Detail}");
        await File.WriteAllTextAsync(path, report.ToString(), ct);
        return path;
    }
}
