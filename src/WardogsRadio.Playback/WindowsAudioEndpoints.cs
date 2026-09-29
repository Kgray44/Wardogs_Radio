using System.Diagnostics;
using System.Text.Json;

namespace WardogsRadio.Playback;

public sealed record WindowsAudioEndpoint(string Id, string Name, bool IsInput, bool IsPresent = true)
{
    public string Category => IsInput ? "Microphone / recording" : "Headphones / speakers";
    public string Availability => IsPresent ? "Available now" : "Selected, but disconnected";
}

/// <summary>Discovers currently present Windows AudioEndpoint PnP entries without changing routing.</summary>
public sealed class WindowsAudioEndpointService
{
    public async Task<IReadOnlyList<WindowsAudioEndpoint>> DiscoverAsync(CancellationToken ct = default)
    {
        const string script = "$ErrorActionPreference='Stop'; Get-PnpDevice -PresentOnly -Class AudioEndpoint | Where-Object Status -eq 'OK' | Select-Object FriendlyName,InstanceId | ConvertTo-Json -Compress";
        using var process = Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -Command \"{script}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        });
        if (process is null) return [];
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            throw;
        }
        var output = await outputTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Windows audio endpoint discovery exited with code {process.ExitCode}.");
        if (string.IsNullOrWhiteSpace(output)) return [];
        using var document = JsonDocument.Parse(output);
        var entries = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToList() : [document.RootElement];
        return entries.Select(entry =>
        {
            var name = entry.TryGetProperty("FriendlyName", out var n) ? n.GetString() : null;
            var id = entry.TryGetProperty("InstanceId", out var i) ? i.GetString() : null;
            return string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) ? null : new WindowsAudioEndpoint(id, name, IsCaptureEndpoint(id));
        }).Where(x => x is not null).Cast<WindowsAudioEndpoint>().OrderBy(x => x.Name).ToList();
    }

    public static bool IsCaptureEndpoint(string id) =>
        id.Contains(@"\{0.0.1.00000000}", StringComparison.OrdinalIgnoreCase);
}
