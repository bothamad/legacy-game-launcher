using LegacyGameLauncher.Models;
using Microsoft.Win32;

namespace LegacyGameLauncher.Services;

/// <summary>
/// Windows compatibility layers via the documented per-user key
/// HKCU\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers (value name = full exe path).
/// This is exactly what the exe's Properties > Compatibility tab writes.
/// </summary>
public static class CompatLayers
{
    public const string SubKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";
    public const string HkcuKey = @"HKCU\" + SubKey;

    public static readonly string[] OsModes = { "", "WIN7RTM", "WIN8RTM", "VISTASP2", "VISTASP1", "VISTARTM", "WINXPSP3", "WINXPSP2", "WIN98", "WIN95" };

    static readonly string[] Managed = OsModes.Where(m => m != "")
        .Concat(new[] { "DISABLEDXMAXIMIZEDWINDOWEDMODE", "HIGHDPIAWARE", "RUNASADMIN", "16BITCOLOR", "256COLOR", "640X480", "DISABLETHEMES" })
        .ToArray();

    public static string? ReadHkcu(string exe)
    {
        using var k = Registry.CurrentUser.OpenSubKey(SubKey);
        return k?.GetValue(exe) as string;
    }

    public static string? ReadHklm(string exe)
    {
        using var hk = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var k = hk.OpenSubKey(SubKey);
        return k?.GetValue(exe) as string;
    }

    static List<string> Tokens(string? value) =>
        (value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t != "~" && t != "$" && t != "#").ToList();

    public static List<string> FlagsFor(CompatSettings s)
    {
        var f = new List<string>();
        if (!string.IsNullOrEmpty(s.OsMode)) f.Add(s.OsMode);
        if (s.DisableFullscreenOptimizations) f.Add("DISABLEDXMAXIMIZEDWINDOWEDMODE");
        if (s.HighDpiAware) f.Add("HIGHDPIAWARE");
        if (s.RunAsAdmin) f.Add("RUNASADMIN");
        if (s.ReducedColor16) f.Add("16BITCOLOR");
        if (s.Res640x480) f.Add("640X480");
        if (s.DisableThemes) f.Add("DISABLETHEMES");
        return f;
    }

    /// <summary>Builds the new value, keeping any flags we don't manage (e.g. set by Windows or by hand).</summary>
    public static string? BuildValue(string? existing, CompatSettings s)
    {
        var keep = Tokens(existing).Where(t => !Managed.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList();
        var all = keep.Concat(FlagsFor(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // "~" prefix = flags apply on Windows 8+ (what the Compatibility tab writes).
        return all.Count == 0 ? null : "~ " + string.Join(" ", all);
    }

    /// <summary>Writes the layers for the game. Returns the value now in effect.</summary>
    public static string? Apply(GameEntry g)
    {
        var existing = ReadHkcu(g.ExePath);
        var next = BuildValue(existing, g.Compat);
        if (string.Equals(existing, next, StringComparison.Ordinal)) return existing;
        ChangeJournal.RecordRegistryOnce(g, "compat", HkcuKey, g.ExePath, existing, "Windows compatibility layers for " + Path.GetFileName(g.ExePath));
        using var k = Registry.CurrentUser.CreateSubKey(SubKey, true);
        if (next == null) k.DeleteValue(g.ExePath, false);
        else k.SetValue(g.ExePath, next, RegistryValueKind.String);
        return next;
    }

    /// <summary>
    /// Removes the per-user layers value for the exe (used when a SafeDisc loader is active: any layer makes the
    /// shim engine load the system version.dll first). The old value is journaled, so "Undo" restores it. HKLM is untouched.
    /// Returns the removed value, or null if there was none.
    /// </summary>
    public static string? Clear(GameEntry g)
    {
        var existing = ReadHkcu(g.ExePath);
        if (existing == null) return null;
        ChangeJournal.RecordRegistryOnce(g, "compat", HkcuKey, g.ExePath, existing, "Windows compatibility layers for " + Path.GetFileName(g.ExePath));
        using var k = Registry.CurrentUser.CreateSubKey(SubKey, true);
        k.DeleteValue(g.ExePath, false);
        return existing;
    }

    /// <summary>Mask of logical processors for a core limit. Prefers one logical CPU per physical core (even indexes) when SMT is likely.</summary>
    public static long AffinityMask(int coreLimit)
    {
        int n = Environment.ProcessorCount;
        if (coreLimit <= 0 || coreLimit >= n) return 0;
        long mask = 0;
        bool spread = n >= coreLimit * 2;
        for (int i = 0, cpu = 0; i < coreLimit && cpu < Math.Min(n, 32); i++, cpu += spread ? 2 : 1)
            mask |= 1L << cpu;
        return mask;
    }
}
