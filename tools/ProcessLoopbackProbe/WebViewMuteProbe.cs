using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Windows.Forms;
using WardogsRadio.App;
using WardogsRadio.Playback;

internal static class WebViewMuteProbe
{
    internal sealed record Result(float Before, float Muted, float Restored,
        float RenderedBefore, float RenderedMuted, float RenderedRestored,
        double ToneBefore, double ToneMuted, double ToneRestored,
        double? ListeningWithGame, double? GameWithGame,
        double? ListeningWithoutGame, double? GameWithoutGame, double? DefaultDuringFanout,
        float? FanoutCapturedPeak, string? FanoutFault,
        double? PolicyTargetTone = null, double? PolicyDefaultTone = null, string? PolicyReadback = null);

    public static async Task<Result> RunAsync(string? gameOutputName = null, string? listeningEndpointId = null,
        string? policyEndpointId = null, bool silent = false, bool sessionGainTest = false,
        bool routeTest = false, bool youtubeBootstrap = false)
    {
        var completion = new TaskCompletionSource<Result>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            using var form = new Form { Width = 8, Height = 8, ShowInTaskbar = false, Opacity = 0,
                StartPosition = FormStartPosition.Manual, Left = -1000, Top = -1000 };
            using var webView = new WebView2 { Dock = DockStyle.Fill };
            form.Controls.Add(webView);
            form.Shown += async (_, _) =>
            {
                WindowsPerAppAudioPolicy? policy = null;
                string? priorConsole = null, priorMultimedia = null;
                uint policyPid = 0;
                try
                {
                    var profile = Path.Combine(Path.GetTempPath(), "wardogs-webview-mute-probe");
                    var environment = await CoreWebView2Environment.CreateAsync(null, profile,
                        new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
                    await webView.EnsureCoreWebView2Async(environment);
                    var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    webView.NavigationCompleted += (_, _) => loaded.TrySetResult();
                    if (youtubeBootstrap)
                    {
                        var page = Path.GetFullPath("src/WardogsRadio.App/youtube-player.html");
                        webView.CoreWebView2.Navigate(new Uri(page).AbsoluteUri + "?v=abcde&instance=probe");
                    }
                    else webView.CoreWebView2.NavigateToString($"<html><body><script>window.startTone=()=>{{let c=new AudioContext();let o=c.createOscillator();let g=c.createGain();window.toneGain=g;g.gain.value={(silent ? "0" : "0.015")};o.frequency.value=523.25;o.connect(g);g.connect(c.destination);o.start();return c.state}};</script></body></html>");
                    await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    var state = await webView.CoreWebView2.ExecuteScriptAsync(youtubeBootstrap
                        ? "window.wardogs?.prepareAudio()" : "window.startTone()");
                    if (youtubeBootstrap)
                        for (var attempt = 0; attempt < 30; attempt++)
                        {
                            state = await webView.CoreWebView2.ExecuteScriptAsync("window.wardogs?.audioState()");
                            if (state.Contains("running", StringComparison.OrdinalIgnoreCase)) break;
                            await Task.Delay(100);
                        }
                    if (!state.Contains("running", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("WebView2 audio context did not start: " + state);
                    if (routeTest)
                    {
                        using var routeEndpoints = new MMDeviceEnumerator();
                        using var routeDefaultOutput = routeEndpoints.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        using var alternate = routeEndpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                            .Single(endpoint => endpoint.FriendlyName == "Speakers (Realtek(R) Audio)");
                        var routeRoot = Path.Combine(Path.GetTempPath(), "wardogs-youtube-listening-route-probe");
                        var route = new YouTubeListeningRoute(routeRoot);
                        if (!route.TryRecover(out var recovery)) throw new InvalidOperationException(recovery);
                        try
                        {
                            await route.StartAsync(webView.CoreWebView2, routeDefaultOutput.ID, 1);
                            if (youtubeBootstrap)
                                Console.WriteLine($"YouTube page silent session opened; endpoint policy verified; global default unchanged={WindowsDefaultRender.Current(Role.Multimedia).Equals(routeDefaultOutput.ID, StringComparison.OrdinalIgnoreCase)}");
                            else
                            {
                                await webView.CoreWebView2.ExecuteScriptAsync("window.toneGain.gain.value=0.015");
                                var first = await CaptureToneAsync(routeDefaultOutput);
                                await route.SwitchAsync(alternate.ID);
                                var second = await Task.WhenAll(CaptureToneAsync(routeDefaultOutput), CaptureToneAsync(alternate));
                                route.SetVolume(.25);
                                var reduced = await CaptureToneAsync(alternate);
                                Console.WriteLine($"Production route: initial={first:F4} on {routeDefaultOutput.FriendlyName}; after switch old={second[0]:F4}, new={second[1]:F4}; quarter volume={reduced:F4}; global default unchanged={WindowsDefaultRender.Current(Role.Multimedia).Equals(routeDefaultOutput.ID, StringComparison.OrdinalIgnoreCase)}");
                            }
                        }
                        finally
                        {
                            if (!route.TryEnd(out var routeRestore)) throw new InvalidOperationException(routeRestore);
                        }
                        completion.TrySetResult(new Result(0, 0, 0, 0, 0, 0, 0, 0, 0,
                            null, null, null, null, null, null, null));
                        form.Close();
                        return;
                    }
                    if (policyEndpointId is not null)
                    {
                        await Task.Delay(300);
                        Console.WriteLine("WebView process info: " + string.Join(", ",
                            webView.CoreWebView2.Environment.GetProcessInfos().Select(info =>
                                $"{info.ProcessId}:{info.Kind}")));
                        using var sessionEndpoints = new MMDeviceEnumerator();
                        var activeWebViewAudioPids = new HashSet<uint>();
                        var probePids = webView.CoreWebView2.Environment.GetProcessInfos()
                            .Select(info => (uint)info.ProcessId).ToHashSet();
                        foreach (var endpoint in sessionEndpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                            for (var index = 0; index < endpoint.AudioSessionManager.Sessions.Count; index++)
                            {
                                var session = endpoint.AudioSessionManager.Sessions[index];
                                if (!probePids.Contains(session.GetProcessID)) continue;
                                Console.WriteLine($"WebView audio session: pid={session.GetProcessID} on {endpoint.FriendlyName}; peak={session.AudioMeterInformation.MasterPeakValue:F4}; browser pid={webView.CoreWebView2.BrowserProcessId}");
                                if (silent || session.AudioMeterInformation.MasterPeakValue > .005f)
                                    activeWebViewAudioPids.Add(session.GetProcessID);
                            }
                        if (activeWebViewAudioPids.Count != 1)
                            throw new InvalidOperationException("Could not identify one active WebView audio session.");
                        policyPid = activeWebViewAudioPids.Single();
                        policy = new WindowsPerAppAudioPolicy();
                        priorConsole = policy.Read(policyPid, 0);
                        priorMultimedia = policy.Read(policyPid, 1);
                        var formatted = WindowsPerAppAudioPolicy.FormatEndpoint(policyEndpointId);
                        policy.Set(policyPid, 0, formatted);
                        policy.Set(policyPid, 1, formatted);
                        var policyReadback = policy.Read(policyPid, 1);
                        if (!string.Equals(policyReadback, formatted, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Per-app policy readback did not match the requested endpoint: " +
                                (policyReadback ?? "<none>"));
                    }
                    if (policyEndpointId is not null && policy is not null)
                    {
                        using var policyEndpoints = new MMDeviceEnumerator();
                        using var target = policyEndpoints.GetDevice(policyEndpointId.Split('\\').Last());
                        using var systemDefault = policyEndpoints.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        using var gameEndpoint = gameOutputName is null ? null : policyEndpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                            .Single(device => AudioDeviceIdentity.SameEndpoint(device.ID, gameOutputName));
                        YouTubeGameFeed? gameFeed = null;
                        try
                        {
                            if (gameOutputName is not null)
                                gameFeed = await YouTubeGameFeed.StartAsync((uint)webView.CoreWebView2.BrowserProcessId,
                                    gameOutputName, .5);
                            var tones = gameEndpoint is null
                                ? await Task.WhenAll(CaptureToneAsync(target), CaptureToneAsync(systemDefault))
                                : await Task.WhenAll(CaptureToneAsync(target), CaptureToneAsync(systemDefault), CaptureToneAsync(gameEndpoint));
                            var capturedPeak = gameFeed?.CapturedPeak;
                            if (sessionGainTest)
                            {
                                var targetSession = Enumerable.Range(0, target.AudioSessionManager.Sessions.Count)
                                    .Select(index => target.AudioSessionManager.Sessions[index])
                                    .Single(session => session.GetProcessID == policyPid);
                                var originalVolume = targetSession.SimpleAudioVolume.Volume;
                                try
                                {
                                    targetSession.SimpleAudioVolume.Volume = .25f;
                                    await Task.Delay(250);
                                    var reducedTone = await CaptureToneAsync(target);
                                    Console.WriteLine($"Session volume quarter: target tone={reducedTone:F4}; process capture peak={gameFeed?.CapturedPeak:F4}; baseline target={tones[0]:F4}; baseline capture={capturedPeak:F4}");
                                }
                                finally { targetSession.SimpleAudioVolume.Volume = originalVolume; }
                            }
                        var readback = policy.Read(policyPid, 1);
                        policy.Set(policyPid, 0, priorConsole);
                        policy.Set(policyPid, 1, priorMultimedia);
                        if (!string.Equals(policy.Read(policyPid, 0), priorConsole, StringComparison.OrdinalIgnoreCase) ||
                            !string.Equals(policy.Read(policyPid, 1), priorMultimedia, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidOperationException("Per-app policy restoration did not read back the prior value.");
                        policy.Dispose();
                        policy = null;
                        completion.TrySetResult(new Result(0, 0, 0, 0, 0, 0, 0, 0, 0,
                            null, tones.Length > 2 ? tones[2] : null, null, null, null, capturedPeak, null,
                            tones[0], tones[1], readback));
                        }
                        finally { if (gameFeed is not null) await gameFeed.DisposeAsync(); }
                        form.Close();
                        return;
                    }
                    if (gameOutputName is not null && listeningEndpointId is not null)
                    {
                        using var fanoutEndpoints = new MMDeviceEnumerator();
                        using var gameEndpoint = fanoutEndpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                            .Single(device => AudioDeviceIdentity.SameEndpoint(device.ID, gameOutputName));
                        using var listeningEndpoint = fanoutEndpoints.GetDevice(listeningEndpointId.Split('\\').Last());
                        using var defaultEndpoint = fanoutEndpoints.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                        await using var feed = await YouTubeGameFeed.StartAsync((uint)webView.CoreWebView2.BrowserProcessId,
                            gameOutputName, .25);
                        await Task.Delay(1200);
                        var capturedBeforeMute = feed.CapturedPeak;
                        webView.CoreWebView2.IsMuted = true;
                        var first = await Task.WhenAll(CaptureToneAsync(listeningEndpoint), CaptureToneAsync(gameEndpoint),
                            CaptureToneAsync(defaultEndpoint));
                        var capturedDuringMute = feed.CapturedPeak;
                        feed.SetVolume(0);
                        var second = await Task.WhenAll(CaptureToneAsync(listeningEndpoint), CaptureToneAsync(gameEndpoint));
                        completion.TrySetResult(new Result(capturedBeforeMute, capturedDuringMute, feed.CapturedPeak,
                            0, 0, 0, 0, 0, 0,
                            first[0], first[1], second[0], second[1], first[2], feed.CapturedPeak, feed.Fault));
                        form.Close();
                        return;
                    }
                    var recorder = await new WasapiRecorderBuilder()
                        .WithProcessLoopback((uint)webView.CoreWebView2.BrowserProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
                        .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2))
                        .BuildAsync();
                    using var endpoints = new MMDeviceEnumerator();
                    using var defaultOutput = endpoints.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    using var rendered = new WasapiLoopbackCapture(defaultOutput);
                    if (rendered.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat || rendered.WaveFormat.BitsPerSample != 32)
                        throw new InvalidOperationException("Default output is not a 32-bit float mix format.");
                    float peak = 0;
                    float renderedPeak = 0;
                    double toneSin = 0, toneCos = 0;
                    long toneSamples = 0, absoluteFrame = 0;
                    recorder.DataAvailable += Captured;
                    void Captured(ReadOnlySpan<byte> data, AudioClientBufferFlags _, long __, long ___)
                    {
                        foreach (var sample in MemoryMarshal.Cast<byte, float>(data))
                            if (float.IsFinite(sample)) peak = Math.Max(peak, Math.Abs(sample));
                    }
                    rendered.DataAvailable += (_, eventArgs) =>
                    {
                        var data = MemoryMarshal.Cast<byte, float>(eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded));
                        for (var index = 0; index < data.Length; index += rendered.WaveFormat.Channels)
                        {
                            var sample = data[index];
                            if (!float.IsFinite(sample)) continue;
                            renderedPeak = Math.Max(renderedPeak, Math.Abs(sample));
                            var phase = 2 * Math.PI * 523.25 * absoluteFrame++ / rendered.WaveFormat.SampleRate;
                            toneSin += sample * Math.Sin(phase);
                            toneCos += sample * Math.Cos(phase);
                            toneSamples++;
                        }
                    };
                    double ToneAmplitude() => toneSamples == 0 ? 0 : 2 * Math.Sqrt(toneSin * toneSin + toneCos * toneCos) / toneSamples;
                    void ResetTone() { toneSin = toneCos = 0; toneSamples = 0; }
                    rendered.StartRecording();
                    recorder.StartRecording();
                    await Task.Delay(1200);
                    var before = peak;
                    var renderedBefore = renderedPeak;
                    var toneBefore = ToneAmplitude();
                    peak = 0;
                    renderedPeak = 0;
                    ResetTone();
                    webView.CoreWebView2.IsMuted = true;
                    await Task.Delay(1200);
                    var muted = peak;
                    var renderedMuted = renderedPeak;
                    var toneMuted = ToneAmplitude();
                    peak = 0;
                    renderedPeak = 0;
                    ResetTone();
                    webView.CoreWebView2.IsMuted = false;
                    await Task.Delay(1200);
                    var restored = peak;
                    var renderedRestored = renderedPeak;
                    var toneRestored = ToneAmplitude();
                    double? listeningWithGame = null, gameWithGame = null;
                    double? listeningWithoutGame = null, gameWithoutGame = null, defaultDuringFanout = null;
                    float? fanoutCapturedPeak = null;
                    string? fanoutFault = null;
                    recorder.StopRecording();
                    await recorder.DisposeAsync();
                    if (gameOutputName is not null && listeningEndpointId is not null)
                    {
                        await Task.Delay(150);
                        webView.CoreWebView2.IsMuted = false;
                        ResetTone();
                        var gameEndpoint = endpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
                            .Single(device => AudioDeviceIdentity.SameEndpoint(device.ID, gameOutputName));
                        using var listeningEndpoint = endpoints.GetDevice(listeningEndpointId.Split('\\').Last());
                        await using var feed = await YouTubeGameFeed.StartAsync((uint)webView.CoreWebView2.BrowserProcessId,
                            gameOutputName, .25);
                        await Task.Delay(1200);
                        webView.CoreWebView2.IsMuted = true;
                        var first = await Task.WhenAll(CaptureToneAsync(listeningEndpoint), CaptureToneAsync(gameEndpoint));
                        listeningWithGame = first[0];
                        gameWithGame = first[1];
                        feed.SetVolume(0);
                        var second = await Task.WhenAll(CaptureToneAsync(listeningEndpoint), CaptureToneAsync(gameEndpoint));
                        listeningWithoutGame = second[0];
                        gameWithoutGame = second[1];
                        defaultDuringFanout = ToneAmplitude();
                        fanoutCapturedPeak = feed.CapturedPeak;
                        fanoutFault = feed.Fault;
                    }
                    rendered.StopRecording();
                    completion.TrySetResult(new Result(before, muted, restored,
                        renderedBefore, renderedMuted, renderedRestored,
                        toneBefore, toneMuted, toneRestored,
                        listeningWithGame, gameWithGame, listeningWithoutGame, gameWithoutGame, defaultDuringFanout,
                        fanoutCapturedPeak, fanoutFault));
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally
                {
                    if (policy is not null)
                    {
                        try { policy.Set(policyPid, 0, priorConsole); policy.Set(policyPid, 1, priorMultimedia); }
                        catch { /* Probe reports failure; the prior policy values remain available above. */ }
                        policy.Dispose();
                    }
                    form.Close();
                }
            };
            Application.Run(form);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return await completion.Task.WaitAsync(TimeSpan.FromSeconds(25));
    }

    static async Task<double> CaptureToneAsync(MMDevice device)
    {
        using var capture = new WasapiLoopbackCapture(device);
        if (capture.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat || capture.WaveFormat.BitsPerSample != 32)
            throw new InvalidOperationException("Output is not a 32-bit float mix format: " + device.FriendlyName);
        double sin = 0, cos = 0;
        long frames = 0;
        capture.DataAvailable += (_, eventArgs) =>
        {
            var data = MemoryMarshal.Cast<byte, float>(eventArgs.Buffer.AsSpan(0, eventArgs.BytesRecorded));
            for (var index = 0; index < data.Length; index += capture.WaveFormat.Channels)
            {
                var sample = data[index];
                if (!float.IsFinite(sample)) continue;
                var phase = 2 * Math.PI * 523.25 * frames / capture.WaveFormat.SampleRate;
                sin += sample * Math.Sin(phase);
                cos += sample * Math.Cos(phase);
                frames++;
            }
        };
        capture.StartRecording();
        await Task.Delay(1200);
        capture.StopRecording();
        return frames == 0 ? 0 : 2 * Math.Sqrt(sin * sin + cos * cos) / frames;
    }
}
