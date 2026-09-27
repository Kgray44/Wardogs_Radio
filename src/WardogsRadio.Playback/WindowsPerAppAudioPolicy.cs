using System.Runtime.InteropServices;

namespace WardogsRadio.Playback;

/// <summary>
/// Windows per-app render endpoint policy. The interface is an internal Windows
/// contract also used by EarTrumpet; callers must verify both persisted policy
/// and the real audio session endpoint, then restore their prior policy.
/// </summary>
public sealed class WindowsPerAppAudioPolicy : IDisposable
{
    const string RenderSuffix = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    const string DevicePrefix = @"\\?\SWD#MMDEVAPI#";
    readonly IAudioPolicyConfigFactory21H2 _policy;

    public WindowsPerAppAudioPolicy()
    {
        var iid = typeof(IAudioPolicyConfigFactory21H2).GUID;
        const string classId = "Windows.Media.Internal.AudioPolicyConfig";
        WindowsCreateString(classId, (uint)classId.Length, out var classHandle);
        try
        {
            var hr = RoGetActivationFactory(classHandle, ref iid, out var factory);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            try { _policy = (IAudioPolicyConfigFactory21H2)Marshal.GetTypedObjectForIUnknown(factory,
                typeof(IAudioPolicyConfigFactory21H2)); }
            finally { Marshal.Release(factory); }
        }
        finally { WindowsDeleteString(classHandle); }
    }

    public string? Read(uint pid, int role)
    {
        var hr = _policy.GetPersistedDefaultAudioEndpoint(pid, 0, role, out var value);
        if (hr < 0 && hr != unchecked((int)0x80070490) && hr != unchecked((int)0x80070057))
            Marshal.ThrowExceptionForHR(hr);
        if (hr < 0 || value == 0) return null;
        try
        {
            var chars = WindowsGetStringRawBuffer(value, out var length);
            return Marshal.PtrToStringUni(chars, (int)length);
        }
        finally { WindowsDeleteString(value); }
    }

    public void Set(uint pid, int role, string? rawDeviceId)
    {
        nint handle = 0;
        try
        {
            if (!string.IsNullOrWhiteSpace(rawDeviceId))
            {
                WindowsCreateString(rawDeviceId, (uint)rawDeviceId.Length, out handle);
            }
            var hr = _policy.SetPersistedDefaultAudioEndpoint(pid, 0, role, handle);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
        finally { if (handle != 0) WindowsDeleteString(handle); }
    }

    public static string FormatEndpoint(string id) => DevicePrefix + id + RenderSuffix;

    public void Dispose() => Marshal.ReleaseComObject(_policy);

    [DllImport("combase.dll")]
    static extern int RoGetActivationFactory(nint classId, ref Guid iid, out nint factory);

    [DllImport("combase.dll", PreserveSig = false)]
    static extern void WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string value,
        uint length, out nint handle);

    [DllImport("combase.dll")]
    static extern int WindowsDeleteString(nint handle);

    [DllImport("combase.dll")]
    static extern nint WindowsGetStringRawBuffer(nint handle, out uint length);

    [Guid("ab3d4648-e242-459f-b02f-541c70306324")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioPolicyConfigFactory21H2
    {
        // IInspectable adds three slots after IUnknown. .NET 10 cannot
        // marshal IInspectable RCWs, so model those slots explicitly.
        int GetIids(); int GetRuntimeClassName(); int GetTrustLevel();
        int M01(); int M02(); int M03(); int M04(); int M05(); int M06();
        int M07(); int M08(); int M09(); int M10(); int M11(); int M12();
        int M13(); int M14(); int M15(); int M16(); int M17(); int M18(); int M19();
        [PreserveSig] int SetPersistedDefaultAudioEndpoint(uint pid, int flow, int role, nint deviceId);
        [PreserveSig] int GetPersistedDefaultAudioEndpoint(uint pid, int flow, int role, out nint deviceId);
    }
}
