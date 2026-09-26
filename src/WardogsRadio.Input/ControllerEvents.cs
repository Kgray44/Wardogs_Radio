using System.Runtime.InteropServices;

namespace WardogsRadio.Input;

public sealed record ControllerControlEvent(string DeviceId, string DeviceName, string ControlId, string ControlName, bool Pressed)
{
    public string BindingKey => ControllerBindingCodec.Encode(DeviceId, ControlId);
}

public static class ControllerBindingCodec
{
    public static string Encode(string deviceId, string controlId) =>
        $"controller:{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(deviceId))}:{controlId}";

    public static bool TryDecode(string? binding, out string deviceId, out string controlId)
    {
        deviceId = controlId = "";
        if (binding is null || !binding.StartsWith("controller:", StringComparison.Ordinal)) return false;
        var parts = binding.Split(':', 3);
        if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[2])) return false;
        try { deviceId = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])); controlId = parts[2]; return true; }
        catch (FormatException) { return false; }
    }

    public static string Display(string binding, IReadOnlyList<ControllerInfo> devices)
    {
        if (!TryDecode(binding, out var deviceId, out var controlId)) return "Unknown controller binding";
        var name = devices.FirstOrDefault(x => string.Equals(x.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))?.Name ?? "Disconnected controller";
        var control = controlId.StartsWith("button-", StringComparison.Ordinal) ? $"Button {controlId[7..]}" :
            controlId.StartsWith("pov-", StringComparison.Ordinal) ? $"POV {controlId[4..]}" : controlId;
        return $"{name} · {control}";
    }
}

public sealed class ControllerEdgeTracker
{
    readonly Dictionary<string, HashSet<string>> _pressed = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyList<ControllerControlEvent> Update(string deviceId, string deviceName, IEnumerable<(string Id, string Name)> active)
    {
        var now = active.ToDictionary(x => x.Id, x => x.Name, StringComparer.OrdinalIgnoreCase);
        if (!_pressed.TryGetValue(deviceId, out var old)) old = [];
        var events = now.Where(x => !old.Contains(x.Key)).Select(x => new ControllerControlEvent(deviceId, deviceName, x.Key, x.Value, true))
            .Concat(old.Where(x => !now.ContainsKey(x)).Select(x => new ControllerControlEvent(deviceId, deviceName, x, x, false))).ToList();
        _pressed[deviceId] = new(now.Keys, StringComparer.OrdinalIgnoreCase);
        return events;
    }
    public void Forget(string deviceId) => _pressed.Remove(deviceId);
    public IReadOnlyList<ControllerControlEvent> ReleaseAll(string deviceId, string deviceName)
    {
        var released = _pressed.TryGetValue(deviceId, out var controls)
            ? controls.Select(x => new ControllerControlEvent(deviceId, deviceName, x, x, false)).ToList() : [];
        _pressed.Remove(deviceId);
        return released;
    }
}

public sealed class XInputControllerMonitor : IAsyncDisposable
{
    static readonly (ushort Mask, string Id, string Name)[] Controls =
    [
        (0x0001, "pov-0", "D-Pad North"), (0x0002, "pov-4", "D-Pad South"),
        (0x0004, "pov-6", "D-Pad West"), (0x0008, "pov-2", "D-Pad East"),
        (0x0010, "button-start", "Start"), (0x0020, "button-back", "Back"),
        (0x0040, "button-left-stick", "Left Stick"), (0x0080, "button-right-stick", "Right Stick"),
        (0x0100, "button-left-shoulder", "Left Shoulder"), (0x0200, "button-right-shoulder", "Right Shoulder"),
        (0x1000, "button-a", "A"), (0x2000, "button-b", "B"), (0x4000, "button-x", "X"), (0x8000, "button-y", "Y")
    ];
    readonly XInputControllerService _service = new();
    readonly ControllerEdgeTracker _edges = new();
    readonly CancellationTokenSource _stop = new();
    readonly Task _loop;
    public event Action<ControllerControlEvent>? ControlChanged;
    public event Action? DevicesChanged;
    public XInputControllerMonitor() => _loop = Task.Run(PollAsync);
    async Task PollAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(30));
        var connected = new HashSet<int>();
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token))
            {
                for (var slot = 0; slot < 4; slot++)
                {
                    var deviceId = $"xinput:{slot}";
                    var name = $"XInput Controller {slot + 1}";
                    if (_service.TryReadButtons(slot, out var buttons))
                    {
                        if (connected.Add(slot)) DevicesChanged?.Invoke();
                        foreach (var edge in _edges.Update(deviceId, name, Controls.Where(x => (buttons & x.Mask) != 0).Select(x => (x.Id, x.Name)))) ControlChanged?.Invoke(edge);
                    }
                    else if (connected.Remove(slot))
                    {
                        foreach (var edge in _edges.ReleaseAll(deviceId, name)) ControlChanged?.Invoke(edge);
                        DevicesChanged?.Invoke();
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync() { _stop.Cancel(); await _loop; _stop.Dispose(); }
}

/// <summary>Receives game-controller HID reports through the owning WPF window's WM_INPUT hook.</summary>
public sealed class RawHidControllerMonitor : IDisposable
{
    const int WmInput = 0x00ff, WmInputDeviceChange = 0x00fe;
    const uint RidInput = 0x10000003, RidiDeviceInfo = 0x2000000b, RidiDeviceName = 0x20000007, RidiPreparsedData = 0x20000005;
    const uint RidevInputSink = 0x00000100, RidevDevNotify = 0x00002000, RidevRemove = 0x00000001;
    readonly IntPtr _window;
    readonly Dictionary<IntPtr, DeviceState> _devices = [];
    readonly ControllerEdgeTracker _edges = new();
    bool _registered;
    public bool IsAvailable => _registered;
    public event Action<ControllerControlEvent>? ControlChanged;
    public event Action? DevicesChanged;

    sealed class DeviceState(IntPtr preparsed, string path, string name, int hatMin)
    {
        public IntPtr Preparsed { get; } = preparsed;
        public string Path { get; } = path;
        public string Name { get; } = name;
        public int HatMinimum { get; } = hatMin;
        public List<(string Id, string Name)> Buttons { get; set; } = [];
        public (string Id, string Name)? Hat { get; set; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct RawInputDevice { public ushort UsagePage, Usage; public uint Flags; public IntPtr Window; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetRawInputDeviceInfoW", SetLastError = true)]
    static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data, ref uint size);
    [DllImport("hid.dll", EntryPoint = "HidP_MaxUsageListLength")]
    static extern uint HidPMaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsed);
    [DllImport("hid.dll", EntryPoint = "HidP_GetUsages")]
    static extern int HidPGetUsages(int reportType, ushort usagePage, ushort collection, [Out] ushort[] usages, ref uint count, IntPtr preparsed, [In] byte[] report, uint reportLength);
    [DllImport("hid.dll", EntryPoint = "HidP_GetUsageValue")]
    static extern int HidPGetUsageValue(int reportType, ushort usagePage, ushort collection, ushort usage, out uint value, IntPtr preparsed, [In] byte[] report, uint reportLength);
    [DllImport("hid.dll", EntryPoint = "HidP_GetSpecificValueCaps")]
    static extern int HidPGetSpecificValueCaps(int reportType, ushort usagePage, ushort collection, ushort usage, IntPtr valueCaps, ref ushort length, IntPtr preparsed);

    public RawHidControllerMonitor(IntPtr window)
    {
        _window = window;
        if (!OperatingSystem.IsWindows() || window == IntPtr.Zero) return;
        var devices = new[] { (ushort)0x04, (ushort)0x05, (ushort)0x08 }
            .Select(usage => new RawInputDevice { UsagePage = 0x01, Usage = usage, Flags = RidevInputSink | RidevDevNotify, Window = window }).ToArray();
        _registered = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
    }

    public bool HandleMessage(int message, IntPtr wParam, IntPtr lParam)
    {
        if (!_registered) return false;
        if (message == WmInputDeviceChange)
        {
            if (_devices.Remove(lParam, out var removed))
            {
                foreach (var edge in _edges.ReleaseAll(removed.Path, removed.Name)) ControlChanged?.Invoke(edge);
                Marshal.FreeHGlobal(removed.Preparsed);
            }
            DevicesChanged?.Invoke();
            return false;
        }
        if (message != WmInput) return false;
        var headerSize = (uint)(IntPtr.Size == 8 ? 24 : 16);
        uint size = 0;
        if (GetRawInputData(lParam, RidInput, IntPtr.Zero, ref size, headerSize) != 0 || size < headerSize + 8 || size > 65_536) return false;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RidInput, buffer, ref size, headerSize) == uint.MaxValue || Marshal.ReadInt32(buffer) != 2) return false;
            var handle = Marshal.ReadIntPtr(buffer, 8);
            var device = GetDevice(handle);
            if (device is null) return false;
            var reportSize = Marshal.ReadInt32(buffer, (int)headerSize);
            var reportCount = Marshal.ReadInt32(buffer, (int)headerSize + 4);
            if (reportSize < 1 || reportCount < 1 || (long)reportSize * reportCount > size - headerSize - 8) return false;
            for (var index = 0; index < reportCount; index++)
            {
                var report = new byte[reportSize];
                Marshal.Copy(IntPtr.Add(buffer, (int)headerSize + 8 + index * reportSize), report, 0, reportSize);
                ProcessReport(device, report);
            }
            return false;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    void ProcessReport(DeviceState device, byte[] report)
    {
        var maximum = Math.Min(512u, HidPMaxUsageListLength(0, 0x09, device.Preparsed));
        var parsed = false;
        if (maximum > 0)
        {
            var buttons = new ushort[maximum]; uint count = maximum;
            if (HidPGetUsages(0, 0x09, 0, buttons, ref count, device.Preparsed, report, (uint)report.Length) >= 0)
            {
                parsed = true;
                device.Buttons = buttons.Take((int)count).Select(button => ($"button-{button}", $"Button {button}")).ToList();
            }
        }
        if (HidPGetUsageValue(0, 0x01, 0, 0x39, out var hat, device.Preparsed, report, (uint)report.Length) >= 0)
        {
            parsed = true;
            var direction = (int)hat - device.HatMinimum;
            string[] names = ["North", "North-East", "East", "South-East", "South", "South-West", "West", "North-West"];
            device.Hat = direction is >= 0 and < 8 ? ($"pov-{direction}", $"POV {names[direction]}") : null;
        }
        if (!parsed) return;
        var controls = device.Buttons.ToList();
        if (device.Hat is { } directionControl) controls.Add(directionControl);
        foreach (var edge in _edges.Update(device.Path, device.Name, controls)) ControlChanged?.Invoke(edge);
    }

    DeviceState? GetDevice(IntPtr handle)
    {
        if (_devices.TryGetValue(handle, out var cached)) return cached;
        var info = Marshal.AllocHGlobal(32);
        try
        {
            Marshal.WriteInt32(info, 32);
            uint infoSize = 32;
            if (GetRawInputDeviceInfo(handle, RidiDeviceInfo, info, ref infoSize) == uint.MaxValue || infoSize < 24 || (uint)Marshal.ReadInt32(info, 4) != 2) return null;
            if (!ControllerUsage.IsGameController((ushort)Marshal.ReadInt16(info, 20), (ushort)Marshal.ReadInt16(info, 22))) return null;
        }
        finally { Marshal.FreeHGlobal(info); }
        uint characters = 0;
        _ = GetRawInputDeviceInfo(handle, RidiDeviceName, IntPtr.Zero, ref characters);
        if (characters is 0 or > 4096) return null;
        var nameBuffer = Marshal.AllocHGlobal((int)(characters + 1) * 2);
        string? path;
        try { path = GetRawInputDeviceInfo(handle, RidiDeviceName, nameBuffer, ref characters) == uint.MaxValue ? null : Marshal.PtrToStringUni(nameBuffer); }
        finally { Marshal.FreeHGlobal(nameBuffer); }
        if (string.IsNullOrWhiteSpace(path)) return null;
        uint prepSize = 0;
        _ = GetRawInputDeviceInfo(handle, RidiPreparsedData, IntPtr.Zero, ref prepSize);
        if (prepSize is 0 or > 65_536) return null;
        var preparsed = Marshal.AllocHGlobal((int)prepSize);
        if (GetRawInputDeviceInfo(handle, RidiPreparsedData, preparsed, ref prepSize) == uint.MaxValue) { Marshal.FreeHGlobal(preparsed); return null; }
        var caps = Marshal.AllocHGlobal(128);
        var hatMinimum = 0;
        try
        {
            ushort count = 1;
            if (HidPGetSpecificValueCaps(0, 0x01, 0, 0x39, caps, ref count, preparsed) >= 0 && count > 0)
                hatMinimum = Marshal.ReadInt32(caps, 40);
        }
        finally { Marshal.FreeHGlobal(caps); }
        var friendlyName = new XInputControllerService().Enumerate().FirstOrDefault(x =>
            string.Equals(x.DeviceId, path, StringComparison.OrdinalIgnoreCase))?.Name ?? path;
        var controller = new DeviceState(preparsed, path, friendlyName, hatMinimum);
        _devices[handle] = controller;
        return controller;
    }

    public void Dispose()
    {
        if (_registered)
        {
            var devices = new[] { (ushort)0x04, (ushort)0x05, (ushort)0x08 }
                .Select(usage => new RawInputDevice { UsagePage = 0x01, Usage = usage, Flags = RidevRemove, Window = IntPtr.Zero }).ToArray();
            _ = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
        }
        foreach (var device in _devices.Values) Marshal.FreeHGlobal(device.Preparsed);
        _devices.Clear();
        _registered = false;
    }
}
