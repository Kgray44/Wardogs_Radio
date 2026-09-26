using System.Text;
using WardogsRadio.Core;
using WardogsRadio.Input;
using WardogsRadio.Playback;
using WardogsRadio.Voicemeeter;

namespace WardogsRadio.Diagnostics;

public sealed record DiagnosticItem(string Area, string Status, string Summary, string Detail);

public sealed class DiagnosticService(IVoicemeeterRemote vm, XInputControllerService controllers)
{
    public IReadOnlyList<DiagnosticItem> Collect(AppConfiguration configuration, PlaybackSnapshot? nativePlayback = null, bool youtubePlayerAvailable = false)
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
        items.Add(new("Controllers", found.Count == 0 ? "NEEDS SETUP" : "CONNECTED",
            found.Count == 0 ? "No game controllers found" : $"{found.Count} game controller{(found.Count == 1 ? "" : "s")} found",
            found.Count == 0 ? "Connect a joystick, gamepad, or multi-axis controller and scan again." : "Open technical details for the device list. Test an actual control press in the Macro editor to verify binding."));
        foreach (var controller in found)
            items.Add(new("Controller", controller.Connected ? "CONNECTED" : "DISCONNECTED",
                $"{controller.Name} · {controller.Kind} · {controller.ButtonCount?.ToString() ?? "?"} buttons · {controller.AxisCount?.ToString() ?? "?"} axes · {controller.PovCount?.ToString() ?? "?"} POV",
                $"Input backend: {controller.Kind}; button capability records: {controller.ButtonCapabilityCount?.ToString() ?? "unknown"}; value capability records: {controller.ValueCapabilityCount?.ToString() ?? "unknown"}; VID/PID {(controller.VendorId is ushort vid ? $"{vid:X4}" : "----")}/{(controller.ProductId is ushort pid ? $"{pid:X4}" : "----")}; device ID: {controller.DeviceId}"));

        foreach (var station in configuration.Profile.Stations.OrderBy(x => x.Order))
        {
            var status = !station.Enabled ? "DISABLED" : string.IsNullOrWhiteSpace(station.Source) ? "NEEDS SETUP" : "CONFIGURED";
            var summary = !station.Enabled ? $"{station.Name} is disabled" : string.IsNullOrWhiteSpace(station.Source) ? $"{station.Name} needs a source" : $"{station.Name} has a source";
            items.Add(new("Station", status, summary, $"Provider: {station.ProviderId}; source: {station.Source}; playlist entries: {station.Runtime.Sequence.Count}."));
        }
        return items;
    }

    public async Task<string> ExportAsync(AppConfiguration configuration, string directory, PlaybackSnapshot? nativePlayback = null, bool youtubePlayerAvailable = false, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"WARDOGS-Radio-diagnostic-{DateTime.Now:yyyyMMdd-HHmmss}.md");
        var report = new StringBuilder("# WARDOGS Radio Diagnostic Report\n\n").AppendLine($"Generated: {DateTimeOffset.Now:O}\n");
        foreach (var item in Collect(configuration, nativePlayback, youtubePlayerAvailable))
            report.AppendLine($"- **{item.Area} / {item.Status}** — {item.Summary}  ").AppendLine($"  {item.Detail}");
        await File.WriteAllTextAsync(path, report.ToString(), ct);
        return path;
    }
}
