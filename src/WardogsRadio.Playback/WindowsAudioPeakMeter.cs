using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace WardogsRadio.Playback;

/// <summary>Reads the Windows endpoint peak for the actual capture or render device.</summary>
public sealed class WindowsAudioPeakMeter : IDisposable
{
    string? _endpointId;
    MMDeviceEnumerator? _enumerator;
    MMDevice? _device;

    public bool TryRead(string? pnpEndpointId, out float peak)
    {
        peak = 0;
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
            peak = _device.AudioMeterInformation.MasterPeakValue;
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
