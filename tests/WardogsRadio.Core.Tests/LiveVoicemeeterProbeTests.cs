using WardogsRadio.Voicemeeter;
using WardogsRadio.Playback;
using WardogsRadio.Core;
using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Xunit;
using Xunit.Abstractions;

namespace WardogsRadio.Core.Tests;

[CollectionDefinition("Live Voicemeeter", DisableParallelization = true)]
public sealed class LiveVoicemeeterCollection { }

[Collection("Live Voicemeeter")]
public sealed class LiveVoicemeeterProbeTests(ITestOutputHelper output)
{
    [Fact]
    public void ReadCurrentBananaRoutingWithoutChangingIt()
    {
        if (Environment.GetEnvironmentVariable("WARDOGS_LIVE_VM_PROBE") != "1") return;
        using var remote = new VoicemeeterRemote();
        Assert.True(remote.TryLogin(out var detail), detail);
        var status = remote.Probe();
        output.WriteLine($"Voicemeeter: {status.Edition} · {detail}");
        var routes = new VoicemeeterRouteController(remote);
        var monitor = new VoicemeeterSignalMonitor(remote);
        var count = status.Edition switch { "Standard" => 3, "Banana" => 5, "Potato" => 8, _ => 0 };
        foreach (var device in remote.ListAudioDevices(true).Where(x => x.Name.Contains("soundcore", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("Microphone Array", StringComparison.OrdinalIgnoreCase)))
            output.WriteLine($"Capture choice: {device.InterfaceName} {device.Name} / {device.HardwareId}");
        foreach (var device in remote.ListAudioDevices(false).Where(x => x.Name.Contains("soundcore", StringComparison.OrdinalIgnoreCase)))
            output.WriteLine($"Render choice: {device.InterfaceName} {device.Name} / {device.HardwareId}");
        remote.TryGetParameterString("Bus[0].device.name", out var a1Device);
        output.WriteLine($"A1 hardware device: {a1Device}");
        for (var strip = 0; strip < count; strip++)
        {
            var b1 = routes.TryRead(strip, "B1", out var enabled) ? enabled.ToString() : "unavailable";
            var a1 = routes.TryRead(strip, "A1", out enabled) ? enabled.ToString() : "unavailable";
            var level = monitor.ReadStrip(status.Edition, strip);
            remote.TryGetParameterString($"Strip[{strip}].device.name", out var stripDevice);
            output.WriteLine($"Strip {strip}: device={stripDevice}, B1={b1}, A1={a1}, available={level.Available}, peak={level.Peak}");
        }
        var bus = monitor.ReadBus(status.Edition, "B1");
        output.WriteLine($"B1: available={bus.Available}, peak={bus.Peak}");
    }

    [Fact]
    public async Task RealOwnerMusicCanReachVoicemeeterAuxAndB1()
    {
        var media = Environment.GetEnvironmentVariable("WARDOGS_TEST_MEDIA");
        if (Environment.GetEnvironmentVariable("WARDOGS_LIVE_VM_PROBE") != "1" || string.IsNullOrWhiteSpace(media)) return;
        Assert.True(File.Exists(media));
        using var remote = new VoicemeeterRemote();
        Assert.True(remote.TryLogin(out var detail), detail);
        var status = remote.Probe();
        Assert.Equal("Banana", status.Edition);
        var routes = new VoicemeeterRouteController(remote);
        Assert.True(routes.TryRead(4, "A1", out var priorA1));
        Assert.True(routes.TryRead(4, "B1", out var priorB1));
        var monitor = new VoicemeeterSignalMonitor(remote);
        Assert.True(monitor.ReadStrip("Banana", 4).Peak < .005f, "AUX already carries audio; will not inject into an occupied input.");
        var mpvPath = new MpvLocator().Find(null);
        Assert.NotNull(mpvPath);
        var maxStrip = 0f;
        var maxBus = 0f;
        try
        {
            Assert.True(remote.TrySetParameterFloat("Strip[4].A1", 0));
            Assert.True(remote.TrySetParameterFloat("Strip[4].B1", 1));
            await using var player = new MpvProvider(new MpvLocator());
            var devices = await player.ListAudioDevicesAsync();
            var aux = Assert.Single(devices, x => x.Description.Contains("Voicemeeter AUX Input", StringComparison.OrdinalIgnoreCase));
            output.WriteLine($"MPV output: {aux.Description} / {aux.Name}");
            await player.SetAudioDeviceAsync(aux.Name);
            await player.LoadAsync(new Station { Source = media, RepeatMode = StationRepeatMode.Track });
            await player.SeekAsync(30);
            await player.SetVolumeAsync(.55);
            await player.PlayAsync();
            for (var attempt = 0; attempt < 60; attempt++)
            {
                await Task.Delay(100);
                maxStrip = Math.Max(maxStrip, monitor.ReadStrip("Banana", 4).Peak);
                maxBus = Math.Max(maxBus, monitor.ReadBus("Banana", "B1").Peak);
            }
            output.WriteLine($"Observed AUX peak={maxStrip:0.0000}; B1 peak={maxBus:0.0000}");
        }
        finally
        {
            Assert.True(remote.TrySetParameterFloat("Strip[4].A1", priorA1 ? 1 : 0), "Failed to restore original AUX A1 route.");
            Assert.True(remote.TrySetParameterFloat("Strip[4].B1", priorB1 ? 1 : 0), "Failed to restore original AUX B1 route.");
        }
        Assert.True(maxStrip > .005f, "Real track never appeared on Voicemeeter AUX input.");
        Assert.True(maxBus > .005f, "Real track never appeared on Voicemeeter B1 output.");
    }

    [Fact]
    public async Task InstallVerifiedDedicatedGameMusicRoute()
    {
        if (Environment.GetEnvironmentVariable("WARDOGS_INSTALL_VERIFIED_MUSIC_ROUTE") != "1") return;
        var media = Environment.GetEnvironmentVariable("WARDOGS_TEST_MEDIA");
        Assert.True(File.Exists(media), "The owner test song is required for installation verification.");
        var store = new ConfigurationStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio"));
        var config = await store.LoadAsync();
        Assert.NotNull(config.MonitorDeviceId);
        Assert.Null(config.AutoMusicRouteStrip);
        using var remote = new VoicemeeterRemote();
        Assert.True(remote.TryLogin(out var detail), detail);
        Assert.Equal("Banana", remote.Probe().Edition);
        var routes = new VoicemeeterRouteController(remote);
        Assert.True(routes.TryRead(4, "A1", out var priorA1));
        Assert.True(routes.TryRead(4, "B1", out var priorB1));
        var monitor = new VoicemeeterSignalMonitor(remote);
        Assert.True(monitor.ReadStrip("Banana", 4).Peak < .005f, "AUX is occupied; will not take it over.");
        await using var player = new MpvProvider(new MpvLocator(), config.MpvPath);
        var devices = await player.ListAudioDevicesAsync();
        var headsetId = config.MonitorDeviceId!.Split('{').Last().TrimEnd('}');
        var headset = Assert.Single(devices, x => x.Name.Contains(headsetId, StringComparison.OrdinalIgnoreCase));
        var aux = Assert.Single(devices, x => x.Description.Contains("Voicemeeter AUX Input", StringComparison.OrdinalIgnoreCase));
        var oldHeadset = config.MpvAudioDeviceName;
        var oldGame = config.GameMpvAudioDeviceName;
        var oldStrip = config.MusicStripIndex;
        var success = false;
        try
        {
            Assert.True(remote.TrySetParameterFloat("Strip[4].A1", 0));
            Assert.True(remote.TrySetParameterFloat("Strip[4].B1", 1));
            await player.SetAudioDeviceAsync(aux.Name);
            await player.LoadAsync(new Station { Source = media! });
            await player.SeekAsync(30);
            await player.SetVolumeAsync(.55);
            await player.PlayAsync();
            var maxStrip = 0f;
            var maxB1 = 0f;
            for (var attempt = 0; attempt < 40; attempt++)
            {
                await Task.Delay(100);
                maxStrip = Math.Max(maxStrip, monitor.ReadStrip("Banana", 4).Peak);
                maxB1 = Math.Max(maxB1, monitor.ReadBus("Banana", "B1").Peak);
            }
            Assert.True(maxStrip > .005f && maxB1 > .005f, $"Real audio did not reach AUX and B1: {maxStrip}, {maxB1}.");
            await player.PauseAsync();
            config.AutoMusicRouteStrip = 4;
            config.AutoMusicPreviousA1 = priorA1;
            config.AutoMusicPreviousB1 = priorB1;
            config.AutoMusicPreviousHeadsetDeviceName = oldHeadset;
            config.AutoMusicPreviousGameDeviceName = oldGame;
            config.AutoMusicPreviousStripIndex = oldStrip;
            config.MpvAudioDeviceName = headset.Name;
            config.GameMpvAudioDeviceName = aux.Name;
            config.MusicStripIndex = 4;
            config.SetupComplete = false;
            await store.SaveAsync(config);
            success = true;
            output.WriteLine($"Persisted headset={headset.Description}; game={aux.Description}; strip=4; AUX peak={maxStrip:0.0000}; B1 peak={maxB1:0.0000}. Original routes saved for restore.");
        }
        finally
        {
            if (!success)
            {
                Assert.True(remote.TrySetParameterFloat("Strip[4].A1", priorA1 ? 1 : 0));
                Assert.True(remote.TrySetParameterFloat("Strip[4].B1", priorB1 ? 1 : 0));
            }
        }
    }

    [Fact]
    public async Task ReadSelectedWindowsHeadsetEndpoint()
    {
        if (Environment.GetEnvironmentVariable("WARDOGS_LIVE_VM_PROBE") != "1") return;
        var store = new ConfigurationStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio"));
        var config = await store.LoadAsync();
        using var meter = new WindowsAudioPeakMeter();
        Assert.True(meter.TryRead(config.MonitorDeviceId, out var peak), "Selected headset render endpoint meter was not available.");
        output.WriteLine($"Soundcore endpoint peak={peak:0.0000}");
    }

    [Fact]
    public async Task ReadActualVoicemeeterB1WindowsEndpoint()
    {
        if (Environment.GetEnvironmentVariable("WARDOGS_LIVE_VM_PROBE") != "1") return;
        var endpoints = await new WindowsAudioEndpointService().DiscoverAsync();
        var b1 = Assert.Single(endpoints, endpoint => endpoint.IsInput && endpoint.Name.StartsWith("Voicemeeter Out B1", StringComparison.OrdinalIgnoreCase));
        using var meter = new WindowsAudioPeakMeter();
        Assert.True(meter.TryRead(b1.Id, out var peak), "Windows did not expose a meter on the B1 capture endpoint.");
        output.WriteLine($"Windows B1 endpoint: {b1.Name}; peak={peak:0.0000}");
        using var enumerator = new MMDeviceEnumerator();
        using var capture = new WasapiCapture(enumerator.GetDevice(b1.Id.Split('\\').Last()));
        output.WriteLine($"B1 capture format: {capture.WaveFormat}");
    }

    [Fact]
    public async Task ReadB1CapturedSamplePeak()
    {
        if (Environment.GetEnvironmentVariable("WARDOGS_LIVE_VM_PROBE") != "1") return;
        var endpoints = await new WindowsAudioEndpointService().DiscoverAsync();
        var b1 = Assert.Single(endpoints, endpoint => endpoint.IsInput && endpoint.Name.StartsWith("Voicemeeter Out B1", StringComparison.OrdinalIgnoreCase));
        using var meter = new WindowsAudioCapturePeakMeter();
        var maxPeak = 0f;
        for (var attempt = 0; attempt < 25; attempt++)
        {
            Assert.True(meter.TryRead(b1.Id, out var sample), "The actual B1 capture stream could not open.");
            maxPeak = Math.Max(maxPeak, sample);
            await Task.Delay(100);
        }
        output.WriteLine($"B1 captured-sample peak={maxPeak:0.0000}");
        Assert.True(maxPeak > .005f, "No live B1 samples were captured; play music or speak and retry.");
    }

    [Fact]
    public async Task ProbeB1PreviewDeviceOpenWithoutChangingRoutes()
    {
        if (Environment.GetEnvironmentVariable("WARDOGS_B1_OPEN_PROBE") != "1") return;
        var store = new ConfigurationStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio"));
        var config = await store.LoadAsync();
        var endpoints = await new WindowsAudioEndpointService().DiscoverAsync();
        var b1 = Assert.Single(endpoints, endpoint => endpoint.IsInput && endpoint.Name.StartsWith("Voicemeeter Out B1", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(config.MonitorDeviceId);
        using var enumerator = new MMDeviceEnumerator();
        using var input = enumerator.GetDevice(b1.Id.Split('\\').Last());
        using var outputDevice = enumerator.GetDevice(config.MonitorDeviceId.Split('\\').Last());
        output.WriteLine($"B1 input: {input.FriendlyName}; headset: {outputDevice.FriendlyName}");
        using var capture = new WasapiCapture(input);
        var buffer = new BufferedWaveProvider(capture.WaveFormat, TimeSpan.FromMilliseconds(500)) { DiscardOnBufferOverflow = true, ReadFully = true };
        using var player = new WasapiOut(outputDevice, AudioClientShareMode.Shared, true, 100);
        player.Init(buffer);
        var volume = player.AudioStreamVolume;
        volume.SetAllVolumes(Enumerable.Repeat(.50f, volume.ChannelCount).ToArray());
        capture.DataAvailable += (_, args) => buffer.AddSamples(args.Buffer, 0, args.BytesRecorded);
        player.Play();
        capture.StartRecording();
        try { await Task.Delay(1500); }
        finally { capture.StopRecording(); player.Stop(); }
        output.WriteLine("B1 preview opened and closed successfully.");
    }

    [Fact]
    public async Task ProbeSharedMmeYouTubeListeningAndRestoreWdm()
    {
        if (Environment.GetEnvironmentVariable("WARDOGS_MME_ROUTE_PROBE") != "1") return;
        var recovery = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio", "youtube-route-recovery.json");
        Assert.True(File.Exists(recovery), "The temporary YouTube route must be active.");
        var store = new ConfigurationStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio"));
        var config = await store.LoadAsync();
        Assert.NotNull(config.MonitorDeviceId);
        using var endpoints = new MMDeviceEnumerator();
        using var headset = endpoints.GetDevice(config.MonitorDeviceId.Split('\\').Last());
        using var remote = new VoicemeeterRemote();
        Assert.True(remote.TryLogin(out var detail), detail);
        Assert.True(remote.TryGetParameterString("Bus[0].device.name", out var current));
        output.WriteLine($"Current A1 reported device: '{current}'");
        var mme = Assert.Single(remote.ListAudioDevices(false), x => x.InterfaceName.Equals("MME", StringComparison.OrdinalIgnoreCase) && x.Name.Equals(headset.FriendlyName, StringComparison.OrdinalIgnoreCase));
        output.WriteLine($"Switching temporary A1 from WDM to {mme.InterfaceName} {mme.Name} for the probe.");
        var changed = false;
        try
        {
            Assert.True(remote.TrySetParameterString("Bus[0].device.mme", mme.Name));
            changed = true;
            await Task.Delay(1500);
            var peak = 0f;
            using var meter = new WindowsAudioPeakMeter();
            for (var i = 0; i < 20; i++)
            {
                if (meter.TryRead(config.MonitorDeviceId, out var sample)) peak = Math.Max(peak, sample);
                await Task.Delay(100);
            }
            output.WriteLine($"Shared MME headset Windows peak={peak:0.0000}");
            using var player = new WasapiOut(headset, AudioClientShareMode.Shared, true, 100);
            player.Init(new BufferedWaveProvider(headset.AudioClient.MixFormat));
            output.WriteLine("B1 preview's shared headset output opened under MME.");
        }
        finally
        {
            if (changed)
            {
                Assert.True(remote.TrySetParameterString("Bus[0].device.wdm", headset.FriendlyName), "Could not restore the original WDM route.");
                await Task.Delay(1200);
                output.WriteLine("Restored the existing WDM YouTube listening route.");
            }
        }
    }

    [Fact]
    public async Task OwnerApprovedSwitchFromSoundcoreMicToAmdArray()
    {
        if (Environment.GetEnvironmentVariable("WARDOGS_SWITCH_OWNER_MIC_TO_AMD") != "1") return;
        var store = new ConfigurationStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WARDOGS Radio"));
        var config = await store.LoadAsync();
        Assert.Equal(0, config.AutoMicrophoneStrip);
        Assert.Equal(0, config.MicrophoneStripIndex);
        Assert.Contains("E286FAFD-58C0-4876-9385-E3F928BC6AEE", config.MicrophoneDeviceId, StringComparison.OrdinalIgnoreCase);
        using var remote = new VoicemeeterRemote();
        Assert.True(remote.TryLogin(out var detail), detail);
        Assert.Equal("Banana", remote.Probe().Edition);
        Assert.True(remote.TryGetParameterString("Strip[0].device.name", out var before));
        Assert.Contains("soundcore P31i", before, StringComparison.OrdinalIgnoreCase);
        var route = new VoicemeeterRouteController(remote);
        Assert.True(route.TryRead(0, "B1", out var b1) && b1);
        Assert.True(route.TryRead(0, "A1", out var a1) && !a1);
        var amd = Assert.Single(remote.ListAudioDevices(true), x => x.InterfaceType == 3 && x.Name.Equals("Microphone Array (AMD Audio Device)", StringComparison.OrdinalIgnoreCase));
        var switched = false;
        try
        {
            Assert.True(remote.TrySetParameterString("Strip[0].device.wdm", amd.Name));
            for (var attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(100);
                if (remote.TryGetParameterString("Strip[0].device.name", out var current) && current.Contains("Microphone Array (AMD Audio Device)", StringComparison.OrdinalIgnoreCase))
                {
                    switched = true;
                    output.WriteLine($"Voicemeeter strip 0 now uses {current}; B1 remains on, A1 remains off.");
                    break;
                }
            }
            Assert.True(switched, "Voicemeeter did not report the AMD microphone after reassignment.");
            Assert.True(route.TryRead(0, "B1", out b1) && b1);
            Assert.True(route.TryRead(0, "A1", out a1) && !a1);
        }
        finally
        {
            if (!switched) remote.TrySetParameterString("Strip[0].device.wdm", before);
        }
    }

    [Fact]
    public async Task ZeroGameGainSilencesRealVoicemeeterOutput()
    {
        if (Environment.GetEnvironmentVariable("WARDOGS_LIVE_GAIN_PROBE") != "1") return;
        var preexistingPlayers = Process.GetProcessesByName("mpv");
        try { Assert.Empty(preexistingPlayers); }
        finally { foreach (var process in preexistingPlayers) process.Dispose(); }
        var media = Environment.GetEnvironmentVariable("WARDOGS_TEST_MEDIA");
        Assert.True(File.Exists(media));
        using var remote = new VoicemeeterRemote();
        Assert.True(remote.TryLogin(out var detail), detail);
        var monitor = new VoicemeeterSignalMonitor(remote);
        Assert.True(monitor.ReadBus("Banana", "B1").Peak < .005f, "B1 already has signal; cannot isolate this test.");
        using var endpointMeter = new WindowsAudioPeakMeter();
        await using var player = new MpvProvider(new MpvLocator());
        var device = Assert.Single(await player.ListAudioDevicesAsync(), x => x.Description.Contains("Voicemeeter AUX Input", StringComparison.OrdinalIgnoreCase));
        var endpointId = "{0.0.0.00000000}.{" + device.Name.Split('{').Last();
        await player.SetAudioDeviceAsync(device.Name);
        await player.LoadAsync(new Station { Source = media! });
        await player.SeekAsync(35);
        await player.SetVolumeAsync(.8);
        await player.PlayAsync();
        var audible = 0f;
        var audibleAux = 0f;
        var audibleEndpoint = 0f;
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(100);
            audible = Math.Max(audible, monitor.ReadBus("Banana", "B1").Peak);
            audibleAux = Math.Max(audibleAux, monitor.ReadStrip("Banana", 4).Peak);
            if (endpointMeter.TryRead(endpointId, out var peak)) audibleEndpoint = Math.Max(audibleEndpoint, peak);
        }
        await player.SetVolumeAsync(0);
        Assert.InRange(await player.ReadVolumeAsync(), 0, .0001);
        Assert.True(await player.ReadMuteAsync());
        await Task.Delay(500);
        var muted = 0f;
        var mutedAux = 0f;
        var mutedEndpoint = 0f;
        var lastMuted = 0f;
        var lastMutedEndpoint = 0f;
        for (var attempt = 0; attempt < 70; attempt++)
        {
            await Task.Delay(100);
            lastMuted = monitor.ReadBus("Banana", "B1").Peak;
            muted = Math.Max(muted, lastMuted);
            mutedAux = Math.Max(mutedAux, monitor.ReadStrip("Banana", 4).Peak);
            if (endpointMeter.TryRead(endpointId, out var peak))
            {
                lastMutedEndpoint = peak;
                mutedEndpoint = Math.Max(mutedEndpoint, peak);
            }
            if (attempt % 10 == 9) output.WriteLine($"At mute +{(attempt + 1) * 100} ms: B1={lastMuted:0.0000}, endpoint={lastMutedEndpoint:0.0000}");
        }
        await player.PauseAsync();
        await Task.Delay(500);
        var afterPause = monitor.ReadBus("Banana", "B1").Peak;
        var competingPlayers = Process.GetProcessesByName("mpv");
        try { output.WriteLine($"Concurrent mpv processes at end: {competingPlayers.Length}"); }
        finally { foreach (var process in competingPlayers) process.Dispose(); }
        output.WriteLine($"B1 80%={audible:0.0000}, 0% max={muted:0.0000}, 0% last={lastMuted:0.0000}, after pause={afterPause:0.0000}; AUX 80%={audibleAux:0.0000}, 0%={mutedAux:0.0000}; Windows endpoint 80%={audibleEndpoint:0.0000}, 0% max={mutedEndpoint:0.0000}, 0% last={lastMutedEndpoint:0.0000}");
        Assert.True(audible > .01f, "Music did not reach B1 before muting.");
        Assert.True(lastMuted < .005f, "B1 still had significant music after MPV gain reached zero.");
    }
}
