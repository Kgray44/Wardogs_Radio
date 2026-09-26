using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace WardogsRadio.App;

/// <summary>
/// Scoped Windows default-render endpoint switch. Windows does not publish a
/// supported setter for this preference; the PolicyConfig COM contract below
/// is used only for the owner-authorized temporary YouTube route and every
/// write is checked against the Core Audio readback.
/// </summary>
internal static class WindowsDefaultRender
{
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    sealed class PolicyConfigClient { }

    // PolicyConfig method order and IDs follow AudioDeviceCmdlets (MIT,
    // copyright 2016-2022 Francois Gendron), SOURCE/IPolicyConfig.cs.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, bool isDefault, IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, bool isDefault, IntPtr defaultPeriod, IntPtr minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string device, IntPtr mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string device, bool fxStore, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string device, Role role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string device, bool visible);
    }

    public static string Current(Role role)
    {
        using var endpoints = new MMDeviceEnumerator();
        using var device = endpoints.GetDefaultAudioEndpoint(DataFlow.Render, role);
        return device.ID;
    }

    public static void Set(string endpointId, Role role)
    {
        using var endpoints = new MMDeviceEnumerator();
        using var target = endpoints.GetDevice(endpointId);
        if (target.DataFlow != DataFlow.Render || target.State != DeviceState.Active)
            throw new InvalidOperationException("Windows audio output is not an active render device.");
        object client = new PolicyConfigClient();
        try
        {
            if (client is not IPolicyConfig policy) throw new NotSupportedException("Windows PolicyConfig audio endpoint control is unavailable.");
            Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(endpointId, role));
            if (!StringComparer.OrdinalIgnoreCase.Equals(Current(role), endpointId))
                throw new InvalidOperationException("Windows did not accept the selected default audio output.");
        }
        finally { if (Marshal.IsComObject(client)) Marshal.ReleaseComObject(client); }
    }
}
