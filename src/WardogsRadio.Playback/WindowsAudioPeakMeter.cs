using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace WardogsRadio.Playback;

public sealed record WindowsAudioEndpointMeterState(float Peak, float Volume, bool Muted);

/// <summary>Reads the Windows endpoint peak for the actual capture or render device.</summary>
public sealed class WindowsAudioPeakMeter : IDisposable
{
    string? _endpointId;
    MMDeviceEnumerator? _enumerator;
    MMDevice? _device;

    public bool TryRead(string? pnpEndpointId, out float peak)
    {
        peak = 0;
        if (!TryReadState(pnpEndpointId, out var state)) return false;
        peak = state.Peak;
        return true;
    }

    public bool TryReadState(string? pnpEndpointId, out WindowsAudioEndpointMeterState state)
    {
        state = new(0, 0, false);
        if (string.IsNullOrWhiteSpace(pnpEndpointId)) return false;
        var id = pnpEndpointId.Split('\\').Last();
        try
        {
            if (_device is null || !string.Equals(_endpointId, id, StringComparison.OrdinalIgnoreCase))
            {
                Dispose();
                _enumerator = new MMDeviceEnumerator();
                _device = _enumerator.GetDevice(id);
                _endpointId = id;
            }
            var peak = _device.AudioMeterInformation.MasterPeakValue;
            state = new(peak, _device.AudioEndpointVolume.MasterVolumeLevelScalar, _device.AudioEndpointVolume.Mute);
            return float.IsFinite(peak);
        }
        catch (Exception error) when (error is InvalidOperationException or InvalidCastException or COMException or TypeLoadException)
        {
            Dispose();
            return false;
        }
    }

    public void Dispose()
    {
        _device?.Dispose();
        _enumerator?.Dispose();
        _device = null;
        _enumerator = null;
        _endpointId = null;
    }
}
