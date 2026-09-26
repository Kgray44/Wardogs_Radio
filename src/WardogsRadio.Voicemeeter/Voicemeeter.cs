using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WardogsRadio.Voicemeeter;

public sealed record VoicemeeterStatus(bool Installed, bool Running, string? Edition, string? Version, string? DllPath, string Detail)
{
    public bool Connected { get; init; }
}

public sealed record VoicemeeterAudioDevice(int InterfaceType, string Name, string HardwareId)
{
    public string InterfaceName => InterfaceType switch { 1 => "MME", 3 => "WDM", 4 => "KS", 5 => "ASIO", _ => $"Type {InterfaceType}" };
}

public static class VoicemeeterSharedOutputSelector
{
    public static VoicemeeterAudioDevice? Find(IReadOnlyList<VoicemeeterAudioDevice> devices, string endpointName) =>
        devices.Where(device => device.InterfaceType == 1 && device.Name.Length >= 18 &&
            (device.Name.Equals(endpointName, StringComparison.OrdinalIgnoreCase) ||
             endpointName.StartsWith(device.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(device => device.Name.Length)
            .FirstOrDefault();
}

public interface IVoicemeeterRemote : IDisposable
{
    VoicemeeterStatus Probe();
    bool TryLogin(out string detail);
    bool TryGetLevel(int type, int channel, out float value);
    /// <summary>Returns the native Remote API status code for a level query. 0 is success.</summary>
    int GetLevelResult(int type, int channel, out float value) =>
        TryGetLevel(type, channel, out value) ? 0 : -1;
    bool TryGetParameterFloat(string name, out float value);
    bool TrySetParameterFloat(string name, float value);
}

public sealed unsafe class VoicemeeterRemote : IVoicemeeterRemote
{
    IntPtr _dll;
    bool _loggedIn;
    delegate* unmanaged<int> _login;
    delegate* unmanaged<int> _logout;
    delegate* unmanaged<int> _isParametersDirty;
    delegate* unmanaged<int*, int> _getVoicemeeterType;
    delegate* unmanaged[Stdcall]<int, int, float*, int> _getLevel;
    delegate* unmanaged[Stdcall]<byte*, float*, int> _getParameterFloat;
    delegate* unmanaged[Stdcall]<byte*, float, int> _setParameterFloat;
    delegate* unmanaged[Stdcall]<byte*, byte*, int> _getParameterString;
    delegate* unmanaged[Stdcall]<byte*, byte*, int> _setParameterString;
    delegate* unmanaged[Stdcall]<int> _inputDeviceCount;
    delegate* unmanaged[Stdcall]<int, int*, byte*, byte*, int> _inputDeviceDescription;
    delegate* unmanaged[Stdcall]<int> _outputDeviceCount;
    delegate* unmanaged[Stdcall]<int, int*, byte*, byte*, int> _outputDeviceDescription;

    static string? FindDll()
    {
        var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
        return roots.Select(root => Path.Combine(root, "VB", "Voicemeeter", "VoicemeeterRemote64.dll")).FirstOrDefault(File.Exists);
    }

    public VoicemeeterStatus Probe()
    {
        var path = FindDll();
        var processes = Process.GetProcesses();
        var running = false;
        try { running = processes.Any(p => p.ProcessName.StartsWith("voicemeeter", StringComparison.OrdinalIgnoreCase)); }
        finally { foreach (var process in processes) process.Dispose(); }
        if (path is null) return new(false, running, null, null, null, "Voicemeeter Remote API DLL was not detected.");
        if (!_loggedIn || _isParametersDirty == null)
            return new(true, running, null, null, path, "Remote API is installed; live connection not tested.");
        var connected = _isParametersDirty() >= 0;
        int type = 0;
        var edition = connected && _getVoicemeeterType != null && _getVoicemeeterType(&type) == 0
            ? type switch { 1 => "Standard", 2 => "Banana", 3 => "Potato", _ => $"Type {type}" } : null;
        return new(true, running, edition, null, path,
            connected ? "Remote API connected to a running Voicemeeter engine." : "Remote API registered, but the Voicemeeter engine is disconnected.")
        { Connected = connected };
    }

    public bool TryLogin(out string detail)
    {
        var status = Probe();
        if (status.DllPath is null) { detail = status.Detail; return false; }
        try
        {
            if (_dll == IntPtr.Zero)
            {
                _dll = NativeLibrary.Load(status.DllPath);
                _login = (delegate* unmanaged<int>)NativeLibrary.GetExport(_dll, "VBVMR_Login");
                _logout = (delegate* unmanaged<int>)NativeLibrary.GetExport(_dll, "VBVMR_Logout");
                _isParametersDirty = (delegate* unmanaged<int>)NativeLibrary.GetExport(_dll, "VBVMR_IsParametersDirty");
                _getVoicemeeterType = (delegate* unmanaged<int*, int>)NativeLibrary.GetExport(_dll, "VBVMR_GetVoicemeeterType");
                _getLevel = (delegate* unmanaged[Stdcall]<int, int, float*, int>)NativeLibrary.GetExport(_dll, "VBVMR_GetLevel");
                _getParameterFloat = (delegate* unmanaged[Stdcall]<byte*, float*, int>)NativeLibrary.GetExport(_dll, "VBVMR_GetParameterFloat");
                _setParameterFloat = (delegate* unmanaged[Stdcall]<byte*, float, int>)NativeLibrary.GetExport(_dll, "VBVMR_SetParameterFloat");
                _getParameterString = (delegate* unmanaged[Stdcall]<byte*, byte*, int>)NativeLibrary.GetExport(_dll, "VBVMR_GetParameterStringA");
                _setParameterString = (delegate* unmanaged[Stdcall]<byte*, byte*, int>)NativeLibrary.GetExport(_dll, "VBVMR_SetParameterStringA");
                _inputDeviceCount = (delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(_dll, "VBVMR_Input_GetDeviceNumber");
                _inputDeviceDescription = (delegate* unmanaged[Stdcall]<int, int*, byte*, byte*, int>)NativeLibrary.GetExport(_dll, "VBVMR_Input_GetDeviceDescA");
                _outputDeviceCount = (delegate* unmanaged[Stdcall]<int>)NativeLibrary.GetExport(_dll, "VBVMR_Output_GetDeviceNumber");
                _outputDeviceDescription = (delegate* unmanaged[Stdcall]<int, int*, byte*, byte*, int>)NativeLibrary.GetExport(_dll, "VBVMR_Output_GetDeviceDescA");
            }
            if (!_loggedIn)
            {
                var code = _login();
                if (code < 0) { detail = $"Remote API registration failed ({code})."; return false; }
                _loggedIn = true;
            }
            var connected = _isParametersDirty() >= 0;
            detail = connected ? "Connected to the Voicemeeter audio engine." : "Remote API registered; start Voicemeeter, then reconnect.";
            return connected;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            detail = ex.Message;
            return false;
        }
    }

    public bool TryGetLevel(int type, int channel, out float value)
    {
        return GetLevelResult(type, channel, out value) == 0;
    }

    public int GetLevelResult(int type, int channel, out float value)
    {
        value = 0;
        if (!_loggedIn || _isParametersDirty == null || _getLevel == null || _isParametersDirty() < 0) return -1;
        fixed (float* pointer = &value) return _getLevel(type, channel, pointer);
    }

    public bool TryGetParameterFloat(string name, out float value)
    {
        value = 0;
        if (!_loggedIn || _isParametersDirty == null || _getParameterFloat == null || _isParametersDirty() < 0) return false;
        var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* namePointer = bytes)
        fixed (float* valuePointer = &value)
            return _getParameterFloat(namePointer, valuePointer) == 0;
    }

    public bool TrySetParameterFloat(string name, float value)
    {
        if (!_loggedIn || _isParametersDirty == null || _setParameterFloat == null || _isParametersDirty() < 0 || !float.IsFinite(value)) return false;
        var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* namePointer = bytes) return _setParameterFloat(namePointer, value) == 0;
    }

    public bool TryGetParameterString(string name, out string value)
    {
        value = "";
        if (!_loggedIn || _getParameterString == null || _isParametersDirty() < 0) return false;
        var parameter = Encoding.ASCII.GetBytes(name + "\0");
        var buffer = new byte[1024];
        fixed (byte* namePointer = parameter)
        fixed (byte* valuePointer = buffer)
        {
            if (_getParameterString(namePointer, valuePointer) != 0) return false;
        }
        value = Decode(buffer);
        return true;
    }

    public bool TrySetParameterString(string name, string value)
    {
        if (!_loggedIn || _setParameterString == null || _isParametersDirty() < 0 || value.Contains('\0')) return false;
        var parameter = Encoding.ASCII.GetBytes(name + "\0");
        var bytes = Encoding.UTF8.GetBytes(value + "\0");
        fixed (byte* namePointer = parameter)
        fixed (byte* valuePointer = bytes)
            return _setParameterString(namePointer, valuePointer) == 0;
    }

    public IReadOnlyList<VoicemeeterAudioDevice> ListAudioDevices(bool inputs)
    {
        if (!_loggedIn || _isParametersDirty() < 0) return [];
        var count = inputs ? _inputDeviceCount() : _outputDeviceCount();
        if (count is < 0 or > 512) return [];
        var devices = new List<VoicemeeterAudioDevice>(count);
        for (var index = 0; index < count; index++)
        {
            var type = 0;
            var name = new byte[1024];
            var hardwareId = new byte[1024];
            fixed (byte* namePointer = name)
            fixed (byte* idPointer = hardwareId)
            {
                var result = inputs ? _inputDeviceDescription(index, &type, namePointer, idPointer) :
                    _outputDeviceDescription(index, &type, namePointer, idPointer);
                if (result != 0) continue;
            }
            devices.Add(new(type, Decode(name), Decode(hardwareId)));
        }
        return devices;
    }

    static string Decode(byte[] bytes)
    {
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end >= 0 ? end : bytes.Length);
    }

    public void Dispose()
    {
        if (_loggedIn && _logout != null) _ = _logout();
        _loggedIn = false;
        if (_dll != IntPtr.Zero) NativeLibrary.Free(_dll);
        _dll = IntPtr.Zero;
        _login = null;
        _logout = null;
        _isParametersDirty = null;
        _getVoicemeeterType = null;
        _getLevel = null;
        _getParameterFloat = null;
        _setParameterFloat = null;
        _getParameterString = null;
        _setParameterString = null;
        _inputDeviceCount = null;
        _inputDeviceDescription = null;
        _outputDeviceCount = null;
        _outputDeviceDescription = null;
    }
}
