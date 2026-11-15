using System.Runtime.InteropServices;
using System.Windows;
using LegacyGameLauncher.Models;
using Microsoft.Win32;

namespace LegacyGameLauncher.Services;

public sealed class ControllerInfo
{
    public string Api { get; set; } = "";
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
}

public static class ControllerService
{
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_GAMEPAD { public ushort wButtons; public byte bLeftTrigger, bRightTrigger; public short sThumbLX, sThumbLY, sThumbRX, sThumbRY; }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_STATE { public uint dwPacketNumber; public XINPUT_GAMEPAD Gamepad; }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_VIBRATION { public ushort l, r; }
    [StructLayout(LayoutKind.Sequential)]
    struct XINPUT_CAPABILITIES { public byte Type, SubType; public ushort Flags; public XINPUT_GAMEPAD Gamepad; public XINPUT_VIBRATION Vibration; }

    [DllImport("xinput1_4.dll")] static extern uint XInputGetState(uint idx, out XINPUT_STATE s);
    [DllImport("xinput1_4.dll")] static extern uint XInputGetCapabilities(uint idx, uint flags, out XINPUT_CAPABILITIES c);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct JOYCAPSW
    {
        public ushort wMid, wPid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint wXmin, wXmax, wYmin, wYmax, wZmin, wZmax, wNumButtons, wPeriodMin, wPeriodMax,
                    wRmin, wRmax, wUmin, wUmax, wVmin, wVmax, wCaps, wMaxAxes, wNumAxes, wMaxButtons;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szRegKey;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szOEMVxD;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct JOYINFOEX { public uint dwSize, dwFlags, dwXpos, dwYpos, dwZpos, dwRpos, dwUpos, dwVpos, dwButtons, dwButtonNumber, dwPOV, dwReserved1, dwReserved2; }

    [DllImport("winmm.dll")] static extern uint joyGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint joyGetDevCapsW(UIntPtr id, ref JOYCAPSW caps, uint size);
    [DllImport("winmm.dll")] static extern uint joyGetPosEx(uint id, ref JOYINFOEX info);

    static readonly string[] XSubTypes = { "unknown", "gamepad", "wheel", "arcade stick", "flight stick", "dance pad", "guitar", "guitar (alt)", "drum kit" };

    public static List<ControllerInfo> Detect()
    {
        var list = new List<ControllerInfo>();
        for (uint i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetState(i, out _) != 0) continue;
                XInputGetCapabilities(i, 0, out var caps);
                var sub = caps.SubType < XSubTypes.Length ? XSubTypes[caps.SubType] : $"subtype {caps.SubType}";
                list.Add(new ControllerInfo { Api = "XInput", Name = $"XInput slot {i + 1}", Detail = $"{sub} (Xbox-compatible; also visible to DirectInput games)" });
            }
            catch (DllNotFoundException) { break; }
        }

        uint n = joyGetNumDevs();
        for (uint i = 0; i < Math.Min(n, 16u); i++)
        {
            var info = new JOYINFOEX { dwSize = (uint)Marshal.SizeOf<JOYINFOEX>(), dwFlags = 0xFF };
            if (joyGetPosEx(i, ref info) != 0) continue; // not connected
            var caps = new JOYCAPSW();
            if (joyGetDevCapsW((UIntPtr)i, ref caps, (uint)Marshal.SizeOf<JOYCAPSW>()) != 0) continue;
            var name = OemName(caps.wMid, caps.wPid) ?? caps.szPname;
            var vendor = caps.wMid switch { 0x045E => "Microsoft (Xbox)", 0x054C => "Sony (PlayStation)", 0x057E => "Nintendo", 0x046D => "Logitech", 0x0E6F => "PDP", 0x0F0D => "Hori", 0x2DC8 => "8BitDo", _ => $"VID {caps.wMid:X4}" };
            list.Add(new ControllerInfo
            {
                Api = "DirectInput",
                Name = name,
                Detail = $"{vendor}, PID {caps.wPid:X4}, {caps.wNumButtons} buttons, {caps.wNumAxes} axes",
            });
        }
        return list;
    }

    static string? OemName(ushort vid, ushort pid)
    {
        var sub = $@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM\VID_{vid:X4}&PID_{pid:X4}";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try { using var k = hive.OpenSubKey(sub); if (k?.GetValue("OEMName") is string s && s.Length > 0) return s; } catch { }
        }
        return null;
    }

    public static string GameInputApi(ISet<string> referenced, GameProfile? p, out string xinputDll)
    {
        xinputDll = referenced.FirstOrDefault(r => r.StartsWith("xinput")) ?? "xinput1_3.dll";
        bool x = referenced.Any(r => r.StartsWith("xinput"));
        bool d8 = referenced.Contains("dinput8.dll"), dOld = referenced.Contains("dinput.dll");
        bool d = d8 || dOld;
        if (x && d) return "XInput + DirectInput (uses XInput for Xbox-style pads, DirectInput for others)";
        if (x) return "XInput";
        if (dOld && !d8) return "DirectInput (legacy DirectInput 7 or older, dinput.dll)";
        if (d) return "DirectInput";
        return "Unknown (no input DLL references found - exe may be packed/protected). " + (p?.InputApiHint ?? "");
    }

    public static string Advice(string api, List<ControllerInfo> pads)
    {
        bool hasX = pads.Any(c => c.Api == "XInput");
        bool hasOtherD = pads.Any(c => c.Api == "DirectInput" && !c.Detail.StartsWith("Microsoft"));
        if (pads.Count == 0) return "No controllers detected. Plug one in and press Detect again.";
        if (api.StartsWith("XInput +")) return "The game supports both APIs, so your controller should work natively.";
        if (api == "XInput")
            return hasX ? "Your Xbox-compatible controller will work natively."
                        : "The game expects XInput but you only have DirectInput controllers. Use an XInput wrapper (x360ce) below, " +
                          "or a vendor tool (e.g. DS4Windows for PlayStation pads) that presents the pad as an Xbox controller.";
        if (api.StartsWith("DirectInput (legacy"))
            return "The game uses legacy DirectInput (dinput.dll, version 7 or older). On Windows 10/11 this old interface can crash while " +
                   "enumerating modern HID devices (controllers with many buttons, RGB/macro keyboards, headsets, wheels). " +
                   "If the game crashes in DINPUT.dll: (1) unplug every controller and extra USB HID device and try again; " +
                   "(2) if that fixes it - or to use a pad - install dinputto8 below, which translates DirectInput 1-7 calls to DirectInput 8 " +
                   "(its changelog includes a fix for joysticks with more than 32 buttons). " +
                   (hasX ? "Xbox pads work through DirectInput with both triggers on one axis and no rumble." : "");
        if (api == "DirectInput")
            return hasX ? "The game uses DirectInput. Xbox controllers still work through DirectInput, but both triggers share one axis and there is no rumble; " +
                          "remap buttons in the game's controller menu. An XInput wrapper will NOT help here."
                        : hasOtherD ? "The game uses DirectInput and your controller is a DirectInput device - it should work; remap in the game's controller menu."
                        : "Controller should work via DirectInput.";
        return "Couldn't determine which API the game uses. Try the controller first; if it isn't detected, an XInput wrapper (x360ce) is the next thing to try.";
    }

    /// <summary>
    /// Copies a user-supplied dinputto8 dinput.dll (DirectInput 1-7 -> 8 translation, from elishacloud/dinputto8 or the
    /// dxwrapper package) into the game folder, with backup/restore. Never touches the system copy.
    /// </summary>
    public static async Task<(bool Ok, string Log)> InstallDinputto8Async(GameEntry g, bool game32, Window? owner)
    {
        var src = g.Input.SourceFolder;
        if (!Directory.Exists(src)) return (false, "Choose the folder where you extracted dinputto8.");
        var sysDirs = new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.SystemX86) };
        if (sysDirs.Any(s => Path.GetFullPath(src).StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            return (false, "That is a Windows system folder. Select the folder you extracted the dinputto8 download into.");
        ushort want = game32 ? (ushort)0x014C : (ushort)0x8664;
        var candidates = Directory.EnumerateFiles(src, "dinput.dll", SearchOption.AllDirectories).ToList();
        var dll = candidates.FirstOrDefault(f => PeReader.ReadMachine(f) == want);
        if (dll == null)
            return (false, candidates.Count == 0 ? "No dinput.dll found in that folder (dinputto8 releases contain dinput.dll)."
                                                 : $"dinput.dll found but not {(game32 ? "32-bit" : "64-bit")}.");
        try
        {
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll);
            if ((vi.CompanyName ?? "").Contains("Microsoft", StringComparison.OrdinalIgnoreCase))
                return (false, "That dinput.dll is Microsoft's own copy, not dinputto8 - nothing to install.");
        }
        catch { }
        var ops = new List<FileOp>();
        var rec = ChangeJournal.PlanPlaceFile(g, "input", dll, Path.Combine(g.GameDir, "dinput.dll"), ops, "dinputto8 dinput.dll");
        var (ok, err) = await FileOpRunner.RunAsync(ops, "Install the dinputto8 DirectInput wrapper (dinput.dll) into the game folder.", owner);
        if (!ok) return (false, "FAILED: " + err);
        if (rec != null) g.Changes.Add(rec);
        g.Input.Installed = true;
        g.Input.InstalledKind = "dinputto8";
        return (true, $"Copied {Path.GetRelativePath(src, dll)} to the game folder as dinput.dll. The game will now load it instead of Windows' DINPUT.dll.");
    }

    /// <summary>Copies user-supplied x360ce files (x86 build for x86 games) into the game folder with backups.</summary>
    public static async Task<(bool Ok, string Log)> InstallX360ceAsync(GameEntry g, bool game32, string xinputDllName, Window owner)
    {
        var src = g.Input.SourceFolder;
        if (!Directory.Exists(src)) return (false, "Choose the folder where you extracted x360ce.");
        ushort want = game32 ? (ushort)0x014C : (ushort)0x8664;
        var log = new List<string>();
        var ops = new List<FileOp>();
        var recs = new List<ChangeRecord?>();

        // Classic x360ce 3.x: ships xinput1_3.dll (+ x360ce.ini). Use the right-architecture dll and name it as the game expects.
        var dll = Directory.EnumerateFiles(src, "xinput*.dll", SearchOption.AllDirectories).FirstOrDefault(f => PeReader.ReadMachine(f) == want);
        if (dll != null)
        {
            recs.Add(ChangeJournal.PlanPlaceFile(g, "input", dll, Path.Combine(g.GameDir, xinputDllName), ops, $"x360ce {xinputDllName}"));
            var ini = Directory.EnumerateFiles(src, "x360ce.ini", SearchOption.AllDirectories).FirstOrDefault();
            if (ini != null) recs.Add(ChangeJournal.PlanPlaceFile(g, "input", ini, Path.Combine(g.GameDir, "x360ce.ini"), ops, "x360ce.ini"));
            log.Add($"Using {Path.GetRelativePath(src, dll)} as {xinputDllName}.");
        }
        else
        {
            // x360ce 4.x: a single exe that generates xinput dll + ini when run from the game folder.
            var exe = Directory.EnumerateFiles(src, "x360ce*.exe", SearchOption.AllDirectories)
                .FirstOrDefault(f => game32 ? !Path.GetFileName(f).Contains("x64", StringComparison.OrdinalIgnoreCase) : Path.GetFileName(f).Contains("x64", StringComparison.OrdinalIgnoreCase));
            if (exe == null) return (false, $"No {(game32 ? "32-bit" : "64-bit")} xinput*.dll or x360ce.exe found in that folder.");
            recs.Add(ChangeJournal.PlanPlaceFile(g, "input", exe, Path.Combine(g.GameDir, Path.GetFileName(exe)), ops, "x360ce configurator"));
            // Files x360ce.exe will create later: journal them so Undo removes them, but only if they don't exist yet.
            foreach (var f in new[] { xinputDllName, "x360ce.ini" })
            {
                var t = Path.Combine(g.GameDir, f);
                if (!File.Exists(t) && !ChangeJournal.HasFileRecord(g, t))
                    recs.Add(new ChangeRecord { Group = "input", Kind = "FileAdded", Target = t, Description = $"{f} (created by x360ce.exe)" });
            }
            log.Add($"Copied {Path.GetFileName(exe)} into the game folder. Run it from there once: it creates {xinputDllName} and x360ce.ini and lets you map your pad.");
        }

        var (ok, err) = await FileOpRunner.RunAsync(ops, "Install the x360ce XInput wrapper into the game folder.", owner);
        if (!ok) return (false, "FAILED: " + err);
        foreach (var r in recs.Where(r => r != null)) g.Changes.Add(r!);
        g.Input.Installed = true;
        g.Input.InstalledKind = "x360ce";
        return (true, string.Join("\n", log));
    }
}
