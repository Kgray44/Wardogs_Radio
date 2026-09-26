using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WardogsRadio.Input;

public sealed record ControllerInfo(int Slot, string Name, bool Connected, ushort Buttons, string Kind = "XInput", string? DeviceId = null,
    ushort? VendorId = null, ushort? ProductId = null, int? ButtonCapabilityCount = null, int? ValueCapabilityCount = null,
    int? ButtonCount = null, int? AxisCount = null, int? PovCount = null);

public static class ControllerUsage
{
    // Generic Desktop page: Joystick, Game Pad, and Multi-axis Controller.
    public static bool IsGameController(ushort usagePage, ushort usage) =>
        usagePage == 0x01 && usage is 0x04 or 0x05 or 0x08;
}

public sealed class XInputControllerService
{
    [StructLayout(LayoutKind.Sequential)]
    struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short LeftX, LeftY, RightX, RightY;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputDeviceList
    {
        public IntPtr Device;
        public uint Type;
    }

    [DllImport("xinput1_4.dll")]
    static extern uint XInputGetState(uint user, out XInputState state);

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputDeviceList([Out] RawInputDeviceList[]? list, ref uint count, uint elementSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("hid.dll", EntryPoint = "HidD_GetProductString")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidDGetProductString(SafeFileHandle device, IntPtr buffer, uint bytes);

    [DllImport("hid.dll", EntryPoint = "HidD_GetPreparsedData")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidDGetPreparsedData(SafeFileHandle device, out IntPtr data);

    [DllImport("hid.dll", EntryPoint = "HidD_FreePreparsedData")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool HidDFreePreparsedData(IntPtr data);

    [DllImport("hid.dll", EntryPoint = "HidP_GetCaps")]
    static extern int HidPGetCaps(IntPtr data, IntPtr caps);

    [DllImport("hid.dll", EntryPoint = "HidP_GetButtonCaps")]
    static extern int HidPGetButtonCaps(int reportType, [Out] byte[] caps, ref ushort length, IntPtr data);

    [DllImport("hid.dll", EntryPoint = "HidP_GetValueCaps")]
    static extern int HidPGetValueCaps(int reportType, [Out] byte[] caps, ref ushort length, IntPtr data);

    const uint RimTypeHid = 2, RidiDeviceName = 0x20000007, RidiDeviceInfo = 0x2000000b;
    const uint InvalidResult = uint.MaxValue;

    public IEnumerable<ControllerInfo> Enumerate()
    {
        var result = new List<ControllerInfo>();
        if (!OperatingSystem.IsWindows()) return result;
        for (uint i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetState(i, out var state) == 0)
                    result.Add(new((int)i, $"XInput Controller {i + 1}", true, state.Gamepad.Buttons, "XInput", $"xinput:{i}",
                        ButtonCount: 14, AxisCount: 6, PovCount: 1));
            }
            catch (DllNotFoundException) { break; }
            catch (EntryPointNotFoundException) { break; }
        }
        result.AddRange(EnumerateHid());
        return result;
    }

    public bool TryReadButtons(int slot, out ushort buttons)
    {
        buttons = 0;
        if (!OperatingSystem.IsWindows() || slot is < 0 or > 3) return false;
        try
        {
            if (XInputGetState((uint)slot, out var state) != 0) return false;
            buttons = state.Gamepad.Buttons;
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    static IEnumerable<ControllerInfo> EnumerateHid()
    {
        var found = new List<ControllerInfo>();
        var elementSize = (uint)Marshal.SizeOf<RawInputDeviceList>();
        uint count = 0;
        if (GetRawInputDeviceList(null, ref count, elementSize) == InvalidResult || count == 0 || count > 4096) return found;
        var devices = new RawInputDeviceList[(int)count];
        if (GetRawInputDeviceList(devices, ref count, elementSize) == InvalidResult) return found;
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices.Take((int)count))
        {
            if (device.Type != RimTypeHid) continue;
            var info = Marshal.AllocHGlobal(32);
            try
            {
                Marshal.WriteInt32(info, 32);
                uint infoSize = 32;
                if (GetRawInputDeviceInfo(device.Device, RidiDeviceInfo, info, ref infoSize) == InvalidResult || infoSize < 24) continue;
                if ((uint)Marshal.ReadInt32(info, 4) != RimTypeHid) continue;
                var vendor = (ushort)Marshal.ReadInt32(info, 8);
                var product = (ushort)Marshal.ReadInt32(info, 12);
                var usagePage = (ushort)Marshal.ReadInt16(info, 20);
                var usage = (ushort)Marshal.ReadInt16(info, 22);
                if (!ControllerUsage.IsGameController(usagePage, usage)) continue;
                var path = DevicePath(device.Device);
                if (string.IsNullOrWhiteSpace(path) || !identities.Add(path)) continue;
                var controls = Capabilities(path, usagePage, usage);
                if (controls is null || !controls.HasGameControls) continue;
                var name = ProductName(path);
                var type = usage switch { 0x04 => "Joystick", 0x05 => "Gamepad", _ => "Multi-axis controller" };
                if (string.IsNullOrWhiteSpace(name)) name = $"{type} · VID {vendor:X4} PID {product:X4}";
                found.Add(new(100 + found.Count, name, true, 0, $"HID {type}", path, vendor, product,
                    controls.ButtonGroups, controls.ValueGroups, controls.Buttons, controls.Axes, controls.Povs));
            }
            finally { Marshal.FreeHGlobal(info); }
        }
        return found;
    }

    static string? DevicePath(IntPtr device)
    {
        uint characters = 0;
        _ = GetRawInputDeviceInfo(device, RidiDeviceName, IntPtr.Zero, ref characters);
        if (characters == 0 || characters > 4096) return null;
        var buffer = Marshal.AllocHGlobal(checked((int)(characters + 1) * 2));
        try
        {
            return GetRawInputDeviceInfo(device, RidiDeviceName, buffer, ref characters) == InvalidResult
                ? null : Marshal.PtrToStringUni(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static string? ProductName(string path)
    {
        using var device = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (device.IsInvalid) return null;
        var buffer = Marshal.AllocHGlobal(512);
        try { return HidDGetProductString(device, buffer, 512) ? Marshal.PtrToStringUni(buffer)?.Trim() : null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static HidControlCapabilities? Capabilities(string path, ushort expectedUsagePage, ushort expectedUsage)
    {
        using var device = CreateFile(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (device.IsInvalid || !HidDGetPreparsedData(device, out var preparsed)) return null;
        var caps = Marshal.AllocHGlobal(64);
        try
        {
            if (HidPGetCaps(preparsed, caps) < 0) return null;
            var usage = (ushort)Marshal.ReadInt16(caps, 0);
            var usagePage = (ushort)Marshal.ReadInt16(caps, 2);
            if (usage != expectedUsage || usagePage != expectedUsagePage || !ControllerUsage.IsGameController(usagePage, usage)) return null;
            var buttonGroups = (ushort)Marshal.ReadInt16(caps, 46);
            var valueGroups = (ushort)Marshal.ReadInt16(caps, 48);
            if (buttonGroups > 512 || valueGroups > 512) return null;
            var buttonRecords = new byte[buttonGroups * HidCapabilityClassifier.CapabilityRecordBytes];
            var valueRecords = new byte[valueGroups * HidCapabilityClassifier.CapabilityRecordBytes];
            var readButtons = buttonGroups;
            var readValues = valueGroups;
            if (buttonGroups > 0 && HidPGetButtonCaps(0, buttonRecords, ref readButtons, preparsed) < 0) return null;
            if (valueGroups > 0 && HidPGetValueCaps(0, valueRecords, ref readValues, preparsed) < 0) return null;
            return HidCapabilityClassifier.Parse(buttonRecords, readButtons, valueRecords, readValues);
        }
        finally { Marshal.FreeHGlobal(caps); HidDFreePreparsedData(preparsed); }
    }
}

public sealed record HotkeyRegistration(int Id, uint VirtualKey, string Description, uint Modifiers = 0);

public sealed class GlobalHotkeyService : IDisposable
{
    const int WmHotkey = 0x0312;
    readonly IntPtr _window;
    readonly HashSet<int> _registered = [];

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnregisterHotKey(IntPtr window, int id);

    public GlobalHotkeyService(IntPtr window) => _window = window;
    public bool TryRegister(HotkeyRegistration registration, out string detail)
    {
        if (RegisterHotKey(_window, registration.Id, registration.Modifiers | 0x4000, registration.VirtualKey))
        {
            _registered.Add(registration.Id);
            detail = "Registered.";
            return true;
        }
        detail = $"Registration failed (Win32 {Marshal.GetLastWin32Error()}); the key may already be in use.";
        return false;
    }
    public bool IsHotkeyMessage(int message, IntPtr wParam, out int id)
    {
        id = 0;
        if (message != WmHotkey) return false;
        var raw = wParam.ToInt64();
        if (raw is < int.MinValue or > int.MaxValue) return false;
        id = (int)raw;
        return _registered.Contains(id);
    }
    public void Dispose()
    {
        foreach (var id in _registered) UnregisterHotKey(_window, id);
        _registered.Clear();
    }
}

public sealed class GlobalKeyReleaseMonitor : IDisposable
{
    const int WhKeyboardLl = 13, WmKeyUp = 0x0101, WmSysKeyUp = 0x0105;
    readonly HashSet<uint> _watched;
    readonly HookProc _callback;
    IntPtr _hook;
    public bool IsAvailable => _hook != IntPtr.Zero;
    public event Action<uint>? KeyReleased;

    delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int hookId, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    public GlobalKeyReleaseMonitor(IEnumerable<uint> virtualKeys)
    {
        _watched = new HashSet<uint>(virtualKeys);
        _callback = Handle;
        if (_watched.Count > 0 && OperatingSystem.IsWindows())
            _hook = SetWindowsHookEx(WhKeyboardLl, _callback, IntPtr.Zero, 0);
    }

    IntPtr Handle(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && message.ToInt64() is WmKeyUp or WmSysKeyUp && data != IntPtr.Zero)
        {
            var key = unchecked((uint)Marshal.ReadInt32(data));
            if (_watched.Contains(key))
                ThreadPool.QueueUserWorkItem(_ => KeyReleased?.Invoke(key));
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
        GC.KeepAlive(_callback);
    }
}
