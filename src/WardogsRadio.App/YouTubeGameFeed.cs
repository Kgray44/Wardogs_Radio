using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Runtime.InteropServices;
using WardogsRadio.Playback;

namespace WardogsRadio.App;

/// <summary>
/// Copies only the WebView2 browser process tree's rendered audio into the
/// selected Voicemeeter render endpoint. The visible YouTube player remains
/// responsible for playback; this does not fetch or decode YouTube media.
/// </summary>
internal sealed class YouTubeGameFeed : IAsyncDisposable
{
    readonly MMDeviceEnumerator _endpoints = new();
    MMDevice? _renderDevice;
    WasapiRecorder? _capture;
    WasapiOut? _output;
    BufferedWaveProvider? _buffer;
    long _lastSignalTicks;
    float _capturedPeak;
    string? _fault;
    bool _stopping;

    public bool HasRecentSignal => DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastSignalTicks) < TimeSpan.FromSeconds(2).Ticks;
    public float CapturedPeak => Volatile.Read(ref _capturedPeak);
    public string? Fault => Volatile.Read(ref _fault);
    public string? OutputEndpointId => _renderDevice?.ID;
    public string? OutputEndpointName => _renderDevice?.FriendlyName;
    public double RequestedGain { get; private set; }

    // Read the shared-mode stream gain back from WASAPI. This confirms the player
    // accepted our write; the independent B1 meter observes downstream signal.
    public double? ReadStreamGain()
    {
        if (_output is null) return null;
        try
        {
            var levels = _output.AudioStreamVolume.GetAllVolumes();
            return levels.Length > 0 && levels.All(float.IsFinite) ? levels.Average() : null;
        }
        catch { return null; }
    }

    public static async Task<YouTubeGameFeed> StartAsync(uint browserProcessId, string outputName, double gain)
    {
        var feed = new YouTubeGameFeed();
        try
        {
            await feed.StartCoreAsync(browserProcessId, outputName, gain);
            return feed;
        }
        catch
        {
            await feed.DisposeAsync();
            throw;
        }
    }

    async Task StartCoreAsync(uint browserProcessId, string outputName, double gain)
    {
        if (browserProcessId == 0) throw new InvalidOperationException("WebView2 browser process is not ready.");
        if (string.IsNullOrWhiteSpace(outputName))
            throw new InvalidOperationException("Choose a specific Voicemeeter game output first.");
        _renderDevice = _endpoints.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .SingleOrDefault(device => AudioDeviceIdentity.SameEndpoint(device.ID, outputName));
        if (_renderDevice is null)
            throw new InvalidOperationException("The selected Voicemeeter game output is not available in Windows.");

        _capture = await new WasapiRecorderBuilder()
            .WithProcessLoopback(browserProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2))
            .BuildAsync();
        _buffer = new BufferedWaveProvider(_capture.WaveFormat, TimeSpan.FromMilliseconds(600))
        {
            ReadFully = true,
            DiscardOnBufferOverflow = true
        };
        _output = new WasapiOut(_renderDevice, AudioClientShareMode.Shared, true, 100);
        _output.Init(_buffer);
        SetVolume(gain);
        _capture.DataAvailable += CapturedAudio;
        _capture.RecordingStopped += CaptureStopped;
        _output.PlaybackStopped += OutputStopped;
        _output.Play();
        _capture.StartRecording();
    }

    void CaptureStopped(object? sender, StoppedEventArgs e)
    {
        if (!_stopping) Volatile.Write(ref _fault, e.Exception?.Message ?? "YouTube audio capture stopped.");
    }

    void OutputStopped(object? sender, StoppedEventArgs e)
    {
        if (!_stopping) Volatile.Write(ref _fault, e.Exception?.Message ?? "Voicemeeter output stopped.");
    }

    void CapturedAudio(ReadOnlySpan<byte> data, AudioClientBufferFlags _, long __, long ___)
    {
        try
        {
            var peak = 0f;
            foreach (var sample in MemoryMarshal.Cast<byte, float>(data))
            {
                if (float.IsFinite(sample)) peak = Math.Max(peak, Math.Abs(sample));
            }
            Volatile.Write(ref _capturedPeak, peak);
            if (peak > .005f) Interlocked.Exchange(ref _lastSignalTicks, DateTime.UtcNow.Ticks);
            _buffer?.AddSamples(data);
        }
        catch (InvalidOperationException) { /* A full buffer may be dropped rather than delay live audio. */ }
    }

    public void SetVolume(double gain)
    {
        var target = Math.Clamp(gain, 0, 1);
        if (_output is null) { RequestedGain = target; return; }
        var volume = _output.AudioStreamVolume;
        var prior = volume.GetAllVolumes();
        try
        {
            volume.SetAllVolumes(Enumerable.Repeat((float)target, volume.ChannelCount).ToArray());
            var applied = volume.GetAllVolumes();
            if (applied.Length != volume.ChannelCount || applied.Any(level => !float.IsFinite(level) || Math.Abs(level - target) > .02))
                throw new InvalidOperationException("The YouTube game stream did not read back the requested level.");
            RequestedGain = target;
        }
        catch
        {
            try { volume.SetAllVolumes(prior); } catch { /* Report the failed readback to the caller. */ }
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping = true;
        if (_capture is not null)
        {
            _capture.DataAvailable -= CapturedAudio;
            _capture.RecordingStopped -= CaptureStopped;
            await _capture.DisposeAsync();
            _capture = null;
        }
        if (_output is not null)
        {
            _output.PlaybackStopped -= OutputStopped;
            _output.Dispose();
            _output = null;
        }
        _buffer = null;
        _renderDevice?.Dispose();
        _renderDevice = null;
        _endpoints.Dispose();
    }
}
