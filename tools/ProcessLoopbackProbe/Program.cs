using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using WardogsRadio.App;
using WardogsRadio.Core;
using WardogsRadio.Voicemeeter;

if (args.Length > 0 && args[0] is "--webview-mute" or "--webview-fanout" or "--webview-policy" or "--webview-policy-silent" or "--webview-session-gain" or "--webview-policy-vaio" or "--webview-route")
{
    string? gameOutput = null, listeningOutput = null;
    if (args[0] is "--webview-fanout" or "--webview-policy" or "--webview-policy-silent" or "--webview-session-gain" or "--webview-policy-vaio")
    {
        var fanoutConfig = await new ConfigurationStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio")).LoadAsync();
        if (args[0] is "--webview-fanout" or "--webview-policy" or "--webview-policy-silent" or "--webview-session-gain" or "--webview-policy-vaio")
            gameOutput = fanoutConfig.GameMpvAudioDeviceName ?? throw new InvalidOperationException("Game output not configured.");
        listeningOutput = fanoutConfig.MonitorDeviceId ?? throw new InvalidOperationException("Listening output not configured.");
        if (args[0] is "--webview-policy" or "--webview-policy-silent" or "--webview-session-gain")
        {
            using var physicalEndpoints = new MMDeviceEnumerator();
            listeningOutput = physicalEndpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Single(endpoint => endpoint.FriendlyName == "Speakers (Realtek(R) Audio)").ID;
        }
        if (args[0] == "--webview-policy-vaio")
        {
            using var virtualEndpoints = new MMDeviceEnumerator();
            listeningOutput = virtualEndpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                .Single(endpoint => endpoint.FriendlyName.StartsWith("Voicemeeter Input (", StringComparison.OrdinalIgnoreCase)).ID;
        }
    }
    var priorDefault = WindowsDefaultRender.Current(Role.Multimedia);
    VoicemeeterRemote? vaioReader = null;
    float priorVaioA1 = 0;
    if (args[0] == "--webview-policy-vaio")
    {
        if (priorDefault.Equals(listeningOutput, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("VAIO is the Windows default; cannot isolate it for this probe.");
        vaioReader = new VoicemeeterRemote();
        if (!vaioReader.TryLogin(out var connection)) throw new InvalidOperationException(connection);
        if (!vaioReader.TryGetParameterFloat("Strip[3].A1", out priorVaioA1) ||
            !vaioReader.TryGetParameterFloat("Strip[3].B1", out var vaioB1) || vaioB1 > .1f)
            throw new InvalidOperationException("VAIO route cannot be safely isolated.");
        var vaioMeter = new VoicemeeterSignalMonitor(vaioReader);
        for (var sample = 0; sample < 10; sample++)
        {
            var signal = vaioMeter.ReadStrip("Banana", 3);
            if (!signal.Available || signal.Peak > .005f)
                throw new InvalidOperationException("VAIO has another live signal; no routing changed.");
            await Task.Delay(100);
        }
        if (!vaioReader.TrySetParameterFloat("Strip[3].A1", 0))
            throw new InvalidOperationException("VAIO A1 could not be isolated.");
        var isolated = false;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(100);
            if (vaioReader.TryGetParameterFloat("Strip[3].A1", out var a1) && a1 < .1f)
            { isolated = true; break; }
        }
        if (!isolated) throw new InvalidOperationException("VAIO A1 isolation did not read back.");
    }
    WebViewMuteProbe.Result result;
    try
    {
        result = await WebViewMuteProbe.RunAsync(
            args[0] is "--webview-fanout" or "--webview-policy" or "--webview-policy-silent" or "--webview-session-gain" or "--webview-policy-vaio" ? gameOutput : null,
            args[0] == "--webview-fanout" ? listeningOutput : null,
            args[0] is "--webview-policy" or "--webview-policy-silent" or "--webview-session-gain" or "--webview-policy-vaio" ? listeningOutput : null,
            args[0] is "--webview-policy-silent" or "--webview-route", args[0] == "--webview-session-gain",
            args[0] == "--webview-route");
    }
    finally
    {
        if (vaioReader is not null)
        {
            var restored = vaioReader.TrySetParameterFloat("Strip[3].A1", priorVaioA1);
            for (var attempt = 0; restored && attempt < 12; attempt++)
            {
                await Task.Delay(100);
                if (vaioReader.TryGetParameterFloat("Strip[3].A1", out var restoredVaioA1) &&
                    Math.Abs(restoredVaioA1 - priorVaioA1) <= .1f) break;
                if (attempt == 11) restored = false;
            }
            if (!restored)
                throw new InvalidOperationException("VAIO A1 restoration failed; inspect Voicemeeter immediately.");
            vaioReader.Dispose();
        }
    }
    if (args[0] == "--webview-route") return;
    if (args[0] is "--webview-policy" or "--webview-policy-silent" or "--webview-session-gain" or "--webview-policy-vaio")
    {
        Console.WriteLine($"Per-app policy readback={result.PolicyReadback}");
        Console.WriteLine($"523 Hz at selected output={result.PolicyTargetTone:F4}; at Windows default={result.PolicyDefaultTone:F4}; global default unchanged={WindowsDefaultRender.Current(Role.Multimedia).Equals(priorDefault, StringComparison.OrdinalIgnoreCase)}");
        Console.WriteLine($"Process-loopback game copy: captured peak={result.FanoutCapturedPeak:F4}; game output 523 Hz={result.GameWithGame:F4}; prior per-app policy restored by readback");
        return;
    }
    Console.WriteLine($"WebView2 process loopback: before IsMuted={result.Before:F4}; during IsMuted={result.Muted:F4}; after unmute={result.Restored:F4}");
    Console.WriteLine($"Windows default endpoint loopback: before={result.RenderedBefore:F4}; muted={result.RenderedMuted:F4}; restored={result.RenderedRestored:F4}");
    Console.WriteLine($"Windows default endpoint 523 Hz component: before={result.ToneBefore:F4}; muted={result.ToneMuted:F4}; restored={result.ToneRestored:F4}");
    if (gameOutput is not null)
    {
        Console.WriteLine($"Muted WebView fanout, listening: game on={result.ListeningWithGame:F4}; game off={result.ListeningWithoutGame:F4}");
        Console.WriteLine($"Muted WebView fanout, game output: game on={result.GameWithGame:F4}; game off={result.GameWithoutGame:F4}");
        Console.WriteLine($"Windows default 523 Hz during fanout={result.DefaultDuringFanout:F4}; default unchanged={WindowsDefaultRender.Current(Role.Multimedia).Equals(priorDefault, StringComparison.OrdinalIgnoreCase)}");
        Console.WriteLine($"Fanout source captured peak={result.FanoutCapturedPeak:F4}; fault={result.FanoutFault ?? "none"}");
    }
    return;
}

var store = new ConfigurationStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio"));
var config = await store.LoadAsync();
if (args.Length > 1 && args[0] == "--restore-configured-headphones")
{
    using var endpoints = new MMDeviceEnumerator();
    using var target = endpoints.GetDevice((config.MonitorDeviceId ?? throw new InvalidOperationException("No headset selected in WARDOGS.")).Split('\\').Last());
    if (target.DataFlow != DataFlow.Render || target.State != DeviceState.Active ||
        !target.FriendlyName.Equals(args[1], StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The configured active headset does not match the expected device; no default output changed.");
    Console.WriteLine($"Restoring Windows default output to {target.FriendlyName}.");
    foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
    {
        var prior = WindowsDefaultRender.Current(role);
        if (!prior.Equals(target.ID, StringComparison.OrdinalIgnoreCase)) WindowsDefaultRender.Set(target.ID, role);
        Console.WriteLine($"{role}: {endpoints.GetDevice(prior).FriendlyName} -> {target.FriendlyName}");
    }
    return;
}
if (args.Length > 0 && args[0] == "--same-default")
{
    var prior = WindowsDefaultRender.Current(Role.Multimedia);
    WindowsDefaultRender.Set(prior, Role.Multimedia);
    Console.WriteLine($"Windows multimedia default stayed {WindowsDefaultRender.Current(Role.Multimedia)}");
    return;
}
if (args.Length > 0 && args[0] == "--clear-a1")
{
    using var reader = new VoicemeeterRemote();
    if (!reader.TryLogin(out var connection)) throw new InvalidOperationException(connection);
    foreach (var driver in new[] { "wdm", "mme", "ks", "asio" })
    {
        var accepted = reader.TrySetParameterString("Bus[0].device." + driver, "");
        await Task.Delay(300);
        reader.TryGetParameterString("Bus[0].device.name", out var current);
        Console.WriteLine($"A1 after clearing {driver}: accepted={accepted}, name='{current}'");
        if (string.IsNullOrWhiteSpace(current)) break;
    }
    return;
}
if (args.Length > 0 && args[0] == "--restore-virtual-b1")
{
    using var reader = new VoicemeeterRemote();
    if (!reader.TryLogin(out var connection)) throw new InvalidOperationException(connection);
    if (!reader.TrySetParameterFloat("Strip[3].B1", 1)) throw new InvalidOperationException("Voicemeeter refused B1 restoration.");
    await Task.Delay(300);
    if (!reader.TryGetParameterFloat("Strip[3].B1", out var restored) || restored < .5f)
        throw new InvalidOperationException("Voicemeeter did not confirm B1 restoration.");
    Console.WriteLine("Original virtual-input B1 route restored.");
    return;
}
if (args.Length > 0 && args[0] == "--temp-route")
{
    using var reader = new VoicemeeterRemote();
    if (!reader.TryLogin(out var connection)) throw new InvalidOperationException(connection);
    using var route = new TemporaryYouTubeRoute(new AudioBridgeService(reader));
    if (!route.TryRecover(out var recovery)) throw new InvalidOperationException(recovery);
    var priorDefault = WindowsDefaultRender.Current(Role.Multimedia);
    try
    {
        route.Begin(config.MonitorDeviceId ?? throw new InvalidOperationException("No headphones selected."), 1);
        Console.WriteLine($"Temporary route default: {WindowsDefaultRender.Current(Role.Multimedia)}");
        route.SetAuditioning(true);
        if (!reader.TryGetParameterFloat("Strip[3].A1", out var mutedA1) || mutedA1 != 0)
            throw new InvalidOperationException("B1 hold did not mute direct Voicemeeter A1 listening.");
        route.SetAuditioning(false);
        if (!reader.TryGetParameterFloat("Strip[3].A1", out var restoredA1) || restoredA1 != 1)
            throw new InvalidOperationException("Releasing B1 hold did not restore direct A1 listening.");
        await Task.Delay(500);
    }
    finally { route.End(); }
    var after = WindowsDefaultRender.Current(Role.Multimedia);
    Console.WriteLine($"Restored Windows default: {after}; matched before: {after.Equals(priorDefault, StringComparison.OrdinalIgnoreCase)}");
    return;
}
if (args.Length > 0 && args[0] == "--read-mic-gain")
{
    using var reader = new VoicemeeterRemote();
    if (!reader.TryLogin(out var connection)) throw new InvalidOperationException(connection);
    var strip = config.MicrophoneStripIndex ?? throw new InvalidOperationException("No microphone strip selected.");
    if (!reader.TryGetParameterFloat($"Strip[{strip}].Gain", out var gainDb)) throw new InvalidOperationException("Voicemeeter mic-strip gain could not be read.");
    Console.WriteLine($"Mic strip {strip} gain: {gainDb:F2} dB, scalar {Math.Pow(10, gainDb / 20):P0}");
    return;
}
if (args.Length > 0 && args[0] == "--inspect-youtube-route")
{
    using var endpoints = new MMDeviceEnumerator();
    using var defaultDevice = endpoints.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
    Console.WriteLine($"Windows default render: {defaultDevice.FriendlyName} ({defaultDevice.ID})");
    foreach (var endpoint in endpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        if (!endpoint.FriendlyName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"Physical render endpoint: {endpoint.FriendlyName} ({endpoint.ID})");
    foreach (var endpoint in endpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        if (endpoint.FriendlyName.Contains("Voicemeeter", StringComparison.OrdinalIgnoreCase))
            Console.WriteLine($"VM render endpoint: {endpoint.FriendlyName} ({endpoint.ID})");
    using var reader = new VoicemeeterRemote();
    if (!reader.TryLogin(out var connection)) throw new InvalidOperationException(connection);
    for (var i = 3; i <= 4; i++)
    {
        reader.TryGetParameterFloat($"Strip[{i}].A1", out var a1);
        reader.TryGetParameterFloat($"Strip[{i}].B1", out var b1);
        reader.TryGetParameterFloat($"Strip[{i}].Gain", out var gain);
        Console.WriteLine($"VM virtual strip {i}: A1={a1} B1={b1} Gain={gain}dB");
    }
    reader.TryGetParameterString("Bus[0].device.name", out var busDevice);
    Console.WriteLine($"Voicemeeter A1 device: {busDevice}");
    if (!string.IsNullOrWhiteSpace(config.MonitorDeviceId))
    {
        using var selected = endpoints.GetDevice(config.MonitorDeviceId.Split('\\').Last());
        foreach (var device in reader.ListAudioDevices(false).Where(x =>
            selected.FriendlyName.StartsWith(x.Name, StringComparison.OrdinalIgnoreCase) ||
            x.Name.StartsWith(selected.FriendlyName, StringComparison.OrdinalIgnoreCase)))
            Console.WriteLine($"VM selected-headset choice: {device.InterfaceName} {device.Name} ({device.HardwareId})");
    }
    return;
}
if (args.Length > 0 && args[0] == "--observe")
{
    using var reader = new VoicemeeterRemote();
    if (!reader.TryLogin(out var connection)) throw new InvalidOperationException(connection);
    var edition = reader.Probe().Edition;
    var signals = new VoicemeeterSignalMonitor(reader);
    for (var i = 0; i < 30; i++)
    {
        var music = signals.ReadStrip(edition, config.MusicStripIndex);
        var b1 = signals.ReadBus(edition, config.GameBus);
        Console.WriteLine($"{i * .2:F1}s music={music.Peak:F4} B1={b1.Peak:F4}");
        await Task.Delay(200);
    }
    return;
}
if (args.Length > 0 && args[0] == "--audition")
{
    using var endpoints = new MMDeviceEnumerator();
    using var b1 = endpoints.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
        .FirstOrDefault(x => x.FriendlyName.Contains("Voicemeeter Out B1", StringComparison.OrdinalIgnoreCase))
        ?? throw new InvalidOperationException("Voicemeeter Out B1 recording endpoint not found.");
    using var headset = endpoints.GetDevice(config.MonitorDeviceId!.Split('\\').Last());
    using var preview = new B1Audition();
    preview.Start(b1.ID, headset.ID);
    Console.WriteLine($"B1 audition started through {headset.FriendlyName}; headset device master {headset.AudioEndpointVolume.MasterVolumeLevelScalar:P0}.");
    await Task.Delay(1500);
    Console.WriteLine($"B1 audition remained active; headset device master {headset.AudioEndpointVolume.MasterVolumeLevelScalar:P0}.");
    return;
}
if (args.Length > 0 && args[0] == "--tone")
{
    await Task.Delay(1200);
    using var endpoints = new MMDeviceEnumerator();
    using var device = endpoints.GetDevice(config.MonitorDeviceId!.Split('\\').Last());
    var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
    var samples = new byte[format.AverageBytesPerSecond * 4];
    for (var frame = 0; frame < 48_000 * 4; frame++)
    {
        var fade = Math.Min(1d, Math.Min(frame, 48_000 * 4 - 1 - frame) / 2400d);
        var sample = (float)(Math.Sin(2 * Math.PI * 523.25 * frame / 48_000) * .08 * fade);
        BitConverter.TryWriteBytes(samples.AsSpan(frame * 8, 4), sample);
        BitConverter.TryWriteBytes(samples.AsSpan(frame * 8 + 4, 4), sample);
    }
    var buffer = new BufferedWaveProvider(format, TimeSpan.FromSeconds(5));
    buffer.AddSamples(samples);
    using var player = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
    player.Init(buffer);
    player.Play();
    await Task.Delay(2000);
    if (!args.Contains("--session-tone"))
    {
        var streamVolume = player.AudioStreamVolume;
        streamVolume.SetAllVolumes(Enumerable.Repeat(0f, streamVolume.ChannelCount).ToArray());
    }
    await Task.Delay(2500);
    player.Stop();
    return;
}

if (string.IsNullOrWhiteSpace(config.GameMpvAudioDeviceName) || string.IsNullOrWhiteSpace(config.MonitorDeviceId))
    throw new InvalidOperationException("Configure separate headset and Voicemeeter game outputs first.");
using var vm = new VoicemeeterRemote();
if (!vm.TryLogin(out var detail)) throw new InvalidOperationException(detail);
var status = vm.Probe();
var meter = new VoicemeeterSignalMonitor(vm);
var before = meter.ReadBus(status.Edition, config.GameBus);
var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Probe executable not found.");
var childStart = new ProcessStartInfo(executable)
{
    UseShellExecute = false,
    CreateNoWindow = true
};
childStart.ArgumentList.Add("--tone");
if (args.Contains("--session-mute")) childStart.ArgumentList.Add("--session-tone");
using var child = Process.Start(childStart) ?? throw new InvalidOperationException("Tone child process could not start.");
await using var feed = await YouTubeGameFeed.StartAsync((uint)child.Id, config.GameMpvAudioDeviceName, .5);
float maxStrip = 0, maxBus = 0, maxStripAfterSourceMute = 0;
float maxCapturedBeforeMute = 0, maxCapturedAfterMute = 0;
AudioSessionControl? childSession = null;
for (var i = 0; i < 45; i++)
{
    if (args.Contains("--session-mute") && i == 20)
    {
        using var endpoints = new MMDeviceEnumerator();
        using var device = endpoints.GetDevice(config.MonitorDeviceId!.Split('\\').Last());
        for (var sessionIndex = 0; sessionIndex < device.AudioSessionManager.Sessions.Count; sessionIndex++)
        {
            var session = device.AudioSessionManager.Sessions[sessionIndex];
            if (session.GetProcessID != child.Id) continue;
            childSession = session;
            childSession.SimpleAudioVolume.Mute = true;
            break;
        }
        if (childSession is null) throw new InvalidOperationException("Child audio session was not found.");
    }
    var strip = meter.ReadStrip(status.Edition, config.MusicStripIndex);
    var bus = meter.ReadBus(status.Edition, config.GameBus);
    if (strip.Available)
    {
        maxStrip = Math.Max(maxStrip, strip.Peak);
        if (i >= 33) maxStripAfterSourceMute = Math.Max(maxStripAfterSourceMute, strip.Peak);
    }
    if (bus.Available) maxBus = Math.Max(maxBus, bus.Peak);
    if (i is >= 15 and < 20) maxCapturedBeforeMute = Math.Max(maxCapturedBeforeMute, feed.CapturedPeak);
    if (i >= 33) maxCapturedAfterMute = Math.Max(maxCapturedAfterMute, feed.CapturedPeak);
    await Task.Delay(100);
}
await child.WaitForExitAsync();
if (childSession is not null) childSession.SimpleAudioVolume.Mute = false;
Console.WriteLine($"Process-loopback signal: {feed.HasRecentSignal}; capture before mute: {maxCapturedBeforeMute:F4}; capture after mute: {maxCapturedAfterMute:F4}; Voicemeeter strip max: {maxStrip:F4}; after source mute: {maxStripAfterSourceMute:F4}; B1 max: {maxBus:F4}; B1 before: {before.Peak:F4}; Child exit: {child.ExitCode}");
if (child.ExitCode != 0 || maxStrip < .01f || maxBus < .01f)
    Environment.ExitCode = 1;
