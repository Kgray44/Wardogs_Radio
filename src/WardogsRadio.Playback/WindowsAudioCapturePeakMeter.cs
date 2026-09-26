using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WardogsRadio.Playback;

/// <summary>
/// Measures samples delivered by a Windows recording endpoint. Virtual devices
/// such as Voicemeeter B1 can stream audio while AudioMeterInformation reports 0.
/// </summary>
public sealed class WindowsAudioCapturePeakMeter : IDisposable
{
    MMDeviceEnumerator? _enumerator;
    MMDevice? _device;
    WasapiCapture? _capture;
    string? _endpointId;
    long _lastPacketTicks;
    float _peak;
    volatile bool _running;
    DateTime _retryAfterUtc;
    public string? LastError { get; private set; }

    public bool TryRead(string? pnpEndpointId, out float peak)
    {
        peak = 0;
        if (string.IsNullOrWhiteSpace(pnpEndpointId)) return false;
        var id = pnpEndpointId.Split('\\').Last();
        if (!_running || !string.Equals(_endpointId, id, StringComparison.OrdinalIgnoreCase))
        {
            if (DateTime.UtcNow < _retryAfterUtc && string.Equals(_endpointId, id, StringComparison.OrdinalIgnoreCase)) return false;
            Dispose();
            try
            {
                _enumerator = new MMDeviceEnumerator();
                _device = _enumerator.GetDevice(id);
                if (_device.DataFlow != DataFlow.Capture) throw new InvalidOperationException("The selected endpoint is not a recording device.");
                _capture = new WasapiCapture(_device);
                if (_capture.WaveFormat.Encoding != WaveFormatEncoding.IeeeFloat || _capture.WaveFormat.BitsPerSample != 32)
                    throw new NotSupportedException("The B1 capture endpoint did not provide 32-bit float samples.");
                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _endpointId = id;
                _running = true;
                _capture.StartRecording();
                LastError = null;
            }
            catch (Exception error)
            {
                LastError = error.Message;
                Dispose();
                _endpointId = id;
                _retryAfterUtc = DateTime.UtcNow.AddSeconds(2);
                return false;
            }
        }
        if (DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastPacketTicks) < TimeSpan.FromMilliseconds(750).Ticks)
            peak = Volatile.Read(ref _peak);
        return true;
    }

    void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        Volatile.Write(ref _peak, AudioSamplePeak.Float32(args.Buffer.AsSpan(0, args.BytesRecorded)));
        Interlocked.Exchange(ref _lastPacketTicks, DateTime.UtcNow.Ticks);
    }

    void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        LastError = args.Exception?.Message ?? "B1 capture stopped.";
        _running = false;
    }

    public void Dispose()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= OnDataAvailable;
            _capture.RecordingStopped -= OnRecordingStopped;
            try { _capture.StopRecording(); } catch { }
            _capture.Dispose();
            _capture = null;
        }
        _device?.Dispose();
        _enumerator?.Dispose();
        _device = null;
        _enumerator = null;
        _endpointId = null;
        _running = false;
        Volatile.Write(ref _peak, 0);
        Interlocked.Exchange(ref _lastPacketTicks, 0);
    }
}

public static class AudioSamplePeak
{
    public static float Float32(ReadOnlySpan<byte> bytes)
    {
        var peak = 0f;
        foreach (var sample in MemoryMarshal.Cast<byte, float>(bytes[..(bytes.Length & ~3)]))
            if (float.IsFinite(sample)) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }
}
