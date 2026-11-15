using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Interop;

namespace LegacyGameLauncher.Services;

public sealed class PadAxis
{
    public ushort Page, Usage;
    public string Name = "";
    public double Value;          // 0..1 (0.5 = centre for sticks)
    public long Raw;
    public bool IsHat;
    public string HatText = "centre";
}

public sealed class PadState
{
    public IntPtr Handle;
    public string Name = "HID game controller";
    public int ButtonCount;
    public HashSet<int> Pressed = new();
    public List<PadAxis> Axes = new();
    public DateTime LastReport;
}

/// <summary>
/// Live view of game controllers through Windows Raw Input + the HID parser. Axis and button names come from the
/// HID usages (X, Y, Z, Rx, Ry, Rz, Button N), which are exactly the names DirectInput games see - so this shows
/// what an old DirectInput game like Rugby 08 gets from the pad. Read-only; nothing is sent to the device.
/// </summary>
public sealed class RawGamepadMonitor : IDisposable
{
    const int WM_INPUT = 0x00FF;
    const uint RIDEV_INPUTSINK = 0x100, RIDEV_REMOVE = 0x1;
    const uint RID_INPUT = 0x10000003, RIM_TYPEHID = 2;
    const uint RIDI_PREPARSEDDATA = 0x20000005, RIDI_DEVICENAME = 0x20000007;
    const int HidP_Input = 0;
    const int HIDP_STATUS_SUCCESS = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage, Usage; public uint Flags; public IntPtr Target; }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint n, uint size);
    [DllImport("user32.dll")] static extern uint GetRawInputData(IntPtr h, uint cmd, IntPtr data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfoW(IntPtr dev, uint cmd, IntPtr data, ref uint size);
    [DllImport("hid.dll")] static extern int HidP_GetCaps(IntPtr pp, byte[] caps);
    [DllImport("hid.dll")] static extern int HidP_GetValueCaps(int type, byte[] caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetButtonCaps(int type, byte[] caps, ref ushort len, IntPtr pp);
    [DllImport("hid.dll")] static extern int HidP_GetUsages(int type, ushort page, ushort link, ushort[] list, ref uint len, IntPtr pp, byte[] report, uint reportLen);
    [DllImport("hid.dll")] static extern int HidP_GetUsageValue(int type, ushort page, ushort link, ushort usage, out uint value, IntPtr pp, byte[] report, uint reportLen);
    [DllImport("hid.dll")] static extern uint HidP_MaxUsageListLength(int type, ushort page, IntPtr pp);
    [DllImport("hid.dll", CharSet = CharSet.Unicode)] static extern bool HidD_GetProductString(IntPtr h, StringBuilder buf, int bytes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

    sealed class ValueCap { public ushort Page, Usage, Link, BitSize; public int Min, Max; }

    sealed class Device
    {
        public PadState State = new();
        public IntPtr Preparsed;
        public List<ValueCap> Values = new();
        public ushort ButtonLink;
        public uint MaxButtons;
    }

    readonly Dictionary<IntPtr, Device> _devices = new();
    HwndSource? _source;

    public IReadOnlyList<PadState> Pads => _devices.Values.Select(d => d.State).OrderBy(s => s.Name).ToList();

    public void Start(System.Windows.Window window)
    {
        var helper = new WindowInteropHelper(window);
        _source = HwndSource.FromHwnd(helper.Handle);
        _source.AddHook(Hook);
        var devs = new[]
        {
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 4, Flags = RIDEV_INPUTSINK, Target = helper.Handle }, // joystick
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 5, Flags = RIDEV_INPUTSINK, Target = helper.Handle }, // gamepad
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 8, Flags = RIDEV_INPUTSINK, Target = helper.Handle }, // multi-axis
        };
        if (!RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
            throw new InvalidOperationException("Windows refused raw input registration (error " + Marshal.GetLastWin32Error() + ").");
    }

    public void Stop()
    {
        var devs = new[]
        {
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 4, Flags = RIDEV_REMOVE },
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 5, Flags = RIDEV_REMOVE },
            new RAWINPUTDEVICE { UsagePage = 1, Usage = 8, Flags = RIDEV_REMOVE },
        };
        RegisterRawInputDevices(devs, (uint)devs.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        _source?.RemoveHook(Hook);
        _source = null;
        foreach (var d in _devices.Values) if (d.Preparsed != IntPtr.Zero) Marshal.FreeHGlobal(d.Preparsed);
        _devices.Clear();
    }

    public void Dispose() => Stop();

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_INPUT) { try { OnInput(lParam); } catch { } }
        return IntPtr.Zero;
    }

    void OnInput(IntPtr hRaw)
    {
        int header = 8 + 2 * IntPtr.Size;
        uint size = 0;
        GetRawInputData(hRaw, RID_INPUT, IntPtr.Zero, ref size, (uint)header);
        if (size == 0) return;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRaw, RID_INPUT, buf, ref size, (uint)header) != size) return;
            if ((uint)Marshal.ReadInt32(buf, 0) != RIM_TYPEHID) return;
            var hDevice = Marshal.ReadIntPtr(buf, 8);
            int sizeHid = Marshal.ReadInt32(buf, header);
            int count = Marshal.ReadInt32(buf, header + 4);
            if (sizeHid <= 0 || count <= 0) return;
            var dev = GetDevice(hDevice);
            if (dev == null) return;
            // Use the latest report in the batch.
            var report = new byte[sizeHid];
            Marshal.Copy(buf + header + 8 + (count - 1) * sizeHid, report, 0, sizeHid);
            Parse(dev, report);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    Device? GetDevice(IntPtr h)
    {
        if (_devices.TryGetValue(h, out var d)) return d;
        uint size = 0;
        GetRawInputDeviceInfoW(h, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return null;
        var pp = Marshal.AllocHGlobal((int)size);
        if (GetRawInputDeviceInfoW(h, RIDI_PREPARSEDDATA, pp, ref size) == unchecked((uint)-1)) { Marshal.FreeHGlobal(pp); return null; }

        d = new Device { Preparsed = pp, State = { Handle = h } };
        var caps = new byte[64];
        if (HidP_GetCaps(pp, caps) != HIDP_STATUS_SUCCESS) { Marshal.FreeHGlobal(pp); return null; }
        ushort nButtonCaps = BitConverter.ToUInt16(caps, 46), nValueCaps = BitConverter.ToUInt16(caps, 48);

        if (nButtonCaps > 0)
        {
            var bc = new byte[72 * nButtonCaps];
            if (HidP_GetButtonCaps(HidP_Input, bc, ref nButtonCaps, pp) == HIDP_STATUS_SUCCESS)
                for (int i = 0; i < nButtonCaps; i++)
                {
                    int o = i * 72;
                    if (BitConverter.ToUInt16(bc, o) != 9) continue; // Button page
                    d.ButtonLink = BitConverter.ToUInt16(bc, o + 6);
                    bool range = bc[o + 12] != 0;
                    int max = range ? BitConverter.ToUInt16(bc, o + 58) : BitConverter.ToUInt16(bc, o + 56);
                    d.State.ButtonCount = Math.Max(d.State.ButtonCount, max);
                }
            d.MaxButtons = HidP_MaxUsageListLength(HidP_Input, 9, pp);
        }
        if (nValueCaps > 0)
        {
            var vc = new byte[72 * nValueCaps];
            if (HidP_GetValueCaps(HidP_Input, vc, ref nValueCaps, pp) == HIDP_STATUS_SUCCESS)
                for (int i = 0; i < nValueCaps; i++)
                {
                    int o = i * 72;
                    ushort page = BitConverter.ToUInt16(vc, o);
                    if (page >= 0xFF00) continue; // vendor-specific (e.g. DS4 motion/touch data)
                    bool range = vc[o + 12] != 0;
                    ushort uMin = BitConverter.ToUInt16(vc, o + 56), uMax = range ? BitConverter.ToUInt16(vc, o + 58) : uMin;
                    for (ushort u = uMin; u <= uMax; u++)
                    {
                        var cap = new ValueCap
                        {
                            Page = page, Usage = u, Link = BitConverter.ToUInt16(vc, o + 6), BitSize = BitConverter.ToUInt16(vc, o + 18),
                            Min = BitConverter.ToInt32(vc, o + 40), Max = BitConverter.ToInt32(vc, o + 44),
                        };
                        if (cap.Max < cap.Min || (cap.Min == 0 && cap.Max == -1)) { cap.Min = 0; cap.Max = (int)Math.Min(int.MaxValue, (1L << Math.Min((int)cap.BitSize, 31)) - 1); }
                        if (d.Values.Any(v => v.Page == page && v.Usage == u)) continue;
                        d.Values.Add(cap);
                        d.State.Axes.Add(new PadAxis { Page = page, Usage = u, Name = UsageName(page, u), IsHat = page == 1 && u == 0x39 });
                    }
                }
        }
        // Order like DirectInput: X, Y, Z, Rx, Ry, Rz, sliders, then hat.
        d.State.Axes = d.State.Axes.OrderBy(a => a.IsHat).ThenBy(a => a.Page).ThenBy(a => a.Usage).ToList();
        // If the device can't be opened by path, something (usually HidHide via DS4Windows) is hiding it from
        // normal access - DirectInput games then won't see it, even though Raw Input still shows its data here.
        d.State.Name = ProductName(h) ?? "HID game controller (hidden from games - e.g. by HidHide; shown here via Raw Input only)";
        _devices[h] = d;
        return d;
    }

    void Parse(Device d, byte[] report)
    {
        var s = d.State;
        s.LastReport = DateTime.Now;
        if (d.MaxButtons > 0)
        {
            var list = new ushort[d.MaxButtons];
            uint len = d.MaxButtons;
            s.Pressed.Clear();
            if (HidP_GetUsages(HidP_Input, 9, 0, list, ref len, d.Preparsed, report, (uint)report.Length) == HIDP_STATUS_SUCCESS)
                for (int i = 0; i < len; i++) s.Pressed.Add(list[i]);
        }
        foreach (var cap in d.Values)
        {
            if (HidP_GetUsageValue(HidP_Input, cap.Page, 0, cap.Usage, out var raw, d.Preparsed, report, (uint)report.Length) != HIDP_STATUS_SUCCESS) continue;
            var axis = s.Axes.First(a => a.Page == cap.Page && a.Usage == cap.Usage);
            long v = raw;
            if (cap.Min < 0 && cap.BitSize is > 0 and < 32 && (v & (1L << (cap.BitSize - 1))) != 0) v -= 1L << cap.BitSize; // sign-extend
            axis.Raw = v;
            if (axis.IsHat)
            {
                int steps = cap.Max - cap.Min + 1;
                axis.HatText = v < cap.Min || v > cap.Max ? "centre" : HatName((int)(v - cap.Min), steps);
                axis.Value = 0;
            }
            else axis.Value = cap.Max == cap.Min ? 0 : Math.Clamp((v - cap.Min) / (double)(cap.Max - cap.Min), 0, 1);
        }
    }

    static string HatName(int pos, int steps)
    {
        string[] eight = { "up", "up-right", "right", "down-right", "down", "down-left", "left", "up-left" };
        if (steps == 8) return eight[pos];
        if (steps == 4) return new[] { "up", "right", "down", "left" }[pos];
        return pos.ToString();
    }

    public static string UsageName(ushort page, ushort usage) => (page, usage) switch
    {
        (1, 0x30) => "X", (1, 0x31) => "Y", (1, 0x32) => "Z",
        (1, 0x33) => "Rx", (1, 0x34) => "Ry", (1, 0x35) => "Rz",
        (1, 0x36) => "Slider", (1, 0x37) => "Dial", (1, 0x38) => "Wheel", (1, 0x39) => "Hat (D-pad)",
        (2, 0xC4) => "Accelerator", (2, 0xC5) => "Brake",
        _ => $"Page {page:X2} usage {usage:X2}",
    };

    static string? ProductName(IntPtr hDevice)
    {
        uint chars = 0;
        GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0) return null;
        var buf = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            if (GetRawInputDeviceInfoW(hDevice, RIDI_DEVICENAME, buf, ref chars) == unchecked((uint)-1)) return null;
            var path = Marshal.PtrToStringUni(buf);
            if (string.IsNullOrEmpty(path)) return null;
            var h = CreateFileW(path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == new IntPtr(-1)) return null;
            try
            {
                var sb = new StringBuilder(128);
                return HidD_GetProductString(h, sb, sb.Capacity * 2) && sb.Length > 0 ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
