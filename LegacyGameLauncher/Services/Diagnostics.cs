using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Text;
using LegacyGameLauncher.Models;
using Microsoft.Win32;

namespace LegacyGameLauncher.Services;

public static class Diagnostics
{
    public static string SystemSummary()
    {
        var sb = new StringBuilder();
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var build = k?.GetValue("CurrentBuild") as string ?? "?";
            var name = int.TryParse(build, out var b) && b >= 22000 ? "Windows 11" : "Windows 10";
            sb.AppendLine($"OS: {name} {k?.GetValue("EditionID")} {k?.GetValue("DisplayVersion")} (build {build}.{k?.GetValue("UBR")})");
        }
        catch { sb.AppendLine("OS: " + Environment.OSVersion); }
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            sb.AppendLine($"CPU: {(k?.GetValue("ProcessorNameString") as string)?.Trim()} - {Environment.ProcessorCount} logical processors");
        }
        catch { }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
                sb.AppendLine($"GPU: {o["Name"]} (driver {o["DriverVersion"]}) desktop {o["CurrentHorizontalResolution"]}x{o["CurrentVerticalResolution"]} @ {o["CurrentRefreshRate"]} Hz");
        }
        catch (Exception ex) { sb.AppendLine("GPU: (WMI query failed: " + ex.Message + ")"); }
        sb.AppendLine($"Launcher: {typeof(Diagnostics).Assembly.GetName().Version} ({(Environment.Is64BitProcess ? "x64" : "x86")}), elevated: {Environment.IsPrivilegedProcess}");
        return sb.ToString();
    }

    /// <summary>Application Error / WER / Application Hang / SideBySide events mentioning the exe since <paramref name="since"/>.</summary>
    public static List<string> EventLogErrors(string exeName, DateTime since)
    {
        var list = new List<string>();
        var t = since.AddSeconds(-5).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var xpath = $"*[System[(Provider[@Name='Application Error'] or Provider[@Name='Windows Error Reporting'] or Provider[@Name='Application Hang'] or Provider[@Name='SideBySide']) and TimeCreated[@SystemTime>='{t}']]]";
        try
        {
            using var reader = new EventLogReader(new EventLogQuery("Application", PathType.LogName, xpath));
            for (var e = reader.ReadEvent(); e != null; e = reader.ReadEvent())
            {
                using (e)
                {
                    string msg;
                    try { msg = e.FormatDescription() ?? string.Join(" | ", e.Properties.Select(p => p.Value)); }
                    catch { msg = string.Join(" | ", e.Properties.Select(p => p.Value)); }
                    if (!msg.Contains(exeName, StringComparison.OrdinalIgnoreCase) && e.ProviderName != "SideBySide") continue;
                    list.Add($"[{e.TimeCreated:HH:mm:ss}] {e.ProviderName} (ID {e.Id}): {Squash(msg)}");
                }
            }
        }
        catch (Exception ex) { list.Add("(Could not read the Application event log: " + ex.Message + ")"); }
        return list;
    }

    /// <summary>Turns the "Faulting module name" from Application Error events into concrete next steps.</summary>
    public static List<string> CrashAdvice(IEnumerable<string> events, GameEntry g)
    {
        var advice = new List<string>();
        var modules = events.Select(e => System.Text.RegularExpressions.Regex.Match(e, @"Faulting module name: ([^,|]+)"))
                            .Where(m => m.Success).Select(m => m.Groups[1].Value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var mod in modules)
        {
            var m = mod.ToLowerInvariant();
            bool local = File.Exists(Path.Combine(g.GameDir, mod));
            if (m is "dinput.dll" or "dinput8.dll")
                advice.Add(local
                    ? $"Crash inside {mod} from the GAME FOLDER (a wrapper). Try removing it (Controllers tab > Remove) or a newer build of that wrapper."
                    : $"Crash inside Windows' {mod} (DirectInput) - the game crashed while reading input devices. Known pattern for DirectInput-7-era games on Windows 10/11 " +
                      "when modern HID devices are attached. Try: 1) unplug all controllers and extra USB devices (wheels, RGB/macro keyboards, headsets) and launch again; " +
                      "2) install dinputto8 on the Controllers tab (DirectInput 1-7 -> 8 translation), then reconnect your pad.");
            else if (m is "d3d8.dll" or "d3d9.dll" or "d3dimm.dll" or "ddraw.dll" || m.StartsWith("nvd3dum") || m.StartsWith("nvwgf") || m.StartsWith("atiu") || m.StartsWith("aticfx") || m.StartsWith("amdx") || m.StartsWith("igd"))
                advice.Add(local
                    ? $"Crash inside the wrapper {mod} in the game folder. Try the other wrapper, a different dgVoodoo2 version, or remove it."
                    : $"Crash in the graphics stack ({mod}). Try the dgVoodoo2 wrapper (Graphics wrapper tab) with a forced resolution and 60 Hz cap.");
            else if (m is "dsound.dll" or "xaudio2_7.dll" || m.StartsWith("xactengine"))
                advice.Add($"Crash in audio ({mod}). Try setting the default playback device to 48 kHz / 16-bit stereo, and disconnect extra audio devices.");
            else if (m.StartsWith("d3dx9_") || m.StartsWith("d3dx8"))
                advice.Add($"Crash in {mod}: reinstall the DirectX June 2010 runtime (Overview tab).");
            else if (m is "ntdll.dll" or "kernelbase.dll")
                advice.Add($"Crash reported in {mod} (generic system module) - the real cause is usually elsewhere. Check copy protection on the Overview tab and try with fewer CPU cores.");
            else if (!m.Equals(Path.GetFileName(g.ExePath), StringComparison.OrdinalIgnoreCase))
                advice.Add($"Crash inside {mod}{(local ? " (in the game folder)" : "")}.");
        }
        return advice;
    }

    static string Squash(string s)
    {
        var lines = s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);
        var joined = string.Join(" | ", lines);
        return joined.Length > 900 ? joined[..900] + "…" : joined;
    }
}
