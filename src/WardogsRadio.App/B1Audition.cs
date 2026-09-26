using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace WardogsRadio.App;

/// <summary>
/// A held, listen-only copy of the actual Windows B1 capture endpoint.
/// It never changes Voicemeeter routes or the game's recording device.
/// </summary>
internal sealed class B1Audition : IDisposable
{
    readonly MMDeviceEnumerator _endpoints = new();
    MMDevice? _captureDevice;
    MMDevice? _renderDevice;
    WasapiCapture? _capture;
    WasapiOut? _output;
    BufferedWaveProvider? _buffer;

    public void Start(string b1EndpointId, string headsetEndpointId)
    {
        try
        {
            _captureDevice = _endpoints.GetDevice(b1EndpointId.Split('\\').Last());
            _renderDevice = _endpoints.GetDevice(headsetEndpointId.Split('\\').Last());
            if (_captureDevice.DataFlow != DataFlow.Capture || _renderDevice.DataFlow != DataFlow.Render)
                throw new InvalidOperationException("Choose a B1 recording endpoint and a headphone output.");

            _capture = new WasapiCapture(_captureDevice);
            _buffer = new BufferedWaveProvider(_capture.WaveFormat, TimeSpan.FromMilliseconds(500))
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };
            _output = new WasapiOut(_renderDevice, AudioClientShareMode.Shared, true, 100);
            _output.Init(_buffer);
            // WasapiOut.Volume changes the physical endpoint master, not this preview.
            // Attenuate only our shared-mode audition stream.
            var streamVolume = _output.AudioStreamVolume;
            streamVolume.SetAllVolumes(Enumerable.Repeat(.50f, streamVolume.ChannelCount).ToArray());
            _capture.DataAvailable += CaptureDataAvailable;
            _output.Play();
            _capture.StartRecording();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    void CaptureDataAvailable(object? sender, WaveInEventArgs args)
    {
        try { _buffer?.AddSamples(args.Buffer, 0, args.BytesRecorded); }
        catch (InvalidOperationException) { /* Device may have stopped between callback and teardown. */ }
    }

    public void Dispose()
    {
        if (_capture is not null)
        {
            _capture.DataAvailable -= CaptureDataAvailable;
            try { _capture.StopRecording(); } catch { }
            try { _capture.Dispose(); } catch { }
            _capture = null;
        }
        if (_output is not null)
        {
            try { _output.Stop(); } catch { }
            try { _output.Dispose(); } catch { }
            _output = null;
        }
        _buffer = null;
        try { _captureDevice?.Dispose(); } catch { }
        try { _renderDevice?.Dispose(); } catch { }
        _captureDevice = null;
        _renderDevice = null;
        try { _endpoints.Dispose(); } catch { }
    }
}
