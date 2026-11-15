using System.Text;
using System.Windows;
using LegacyGameLauncher.Models;

namespace LegacyGameLauncher.Services;

/// <summary>Line-preserving INI editor (comments and ordering survive).</summary>
public sealed class IniDocument
{
    readonly List<string> _lines;
    public IniDocument(string text) => _lines = text.Replace("\r\n", "\n").Split('\n').ToList();
    public override string ToString() => string.Join("\r\n", _lines);

    /// <summary>Sets key in [section]; if not there, in any section that has it. Returns false if the key doesn't exist anywhere.</summary>
    public bool Set(string section, string key, string value)
    {
        int idx = Find(section, key);
        if (idx < 0) idx = Find(null, key);
        if (idx < 0) return false;
        var line = _lines[idx];
        int eq = line.IndexOf('=');
        _lines[idx] = line[..(eq + 1)] + " " + value;
        return true;
    }

    int Find(string? section, string key)
    {
        string? current = null;
        for (int i = 0; i < _lines.Count; i++)
        {
            var t = _lines[i].Trim();
            if (t.StartsWith('[') && t.EndsWith(']')) { current = t[1..^1].Trim(); continue; }
            if (t.StartsWith(';') || t.StartsWith('#')) continue;
            int eq = t.IndexOf('=');
            if (eq <= 0) continue;
            if (!t[..eq].Trim().Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            if (section == null || string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}

public sealed class WrapperSource
{
    /// <summary>(source path, file name to create in the game folder)</summary>
    public List<(string Source, string Name)> Files { get; } = new();
    public string? ConfigTemplate { get; set; }
    public string Description { get; set; } = "";
}

/// <summary>
/// Installs a user-downloaded dgVoodoo2 or DXVK d3d8.dll / d3d9.dll (matching the game's Direct3D version) into a game folder with backup/restore.
/// Picks the DLL that matches the GAME's architecture (x86 for these games), never the launcher's.
/// </summary>
public static class WrapperManager
{
    /// <summary>
    /// "d3d8" or "d3d9". The main exe decides when it names exactly one of them; folder DLLs
    /// (e.g. EAInstall.dll, which mentions both) are only a fallback. "d3d9" if still ambiguous.
    /// </summary>
    public static string GraphicsApi(GameScan scan, out bool certain)
    {
        var fromExe = GraphicsApi((ISet<string>)scan.ExeReferenced, out certain);
        return certain ? fromExe : GraphicsApi((ISet<string>)scan.Referenced, out certain);
    }

    public static string GraphicsApi(ISet<string> referenced, out bool certain)
    {
        bool d8 = referenced.Contains("d3d8.dll"), d9 = referenced.Contains("d3d9.dll");
        certain = d8 ^ d9;
        return d8 && !d9 ? "d3d8" : "d3d9";
    }

    static string FindDll(string kind, string folder, bool game32, string dll)
    {
        string[] candidates = kind switch
        {
            "dgVoodoo2" => game32
                ? new[] { $@"MS\x86\{dll}", $@"x86\{dll}", dll }
                : new[] { $@"MS\x64\{dll}", $@"x64\{dll}", dll },
            "DXVK" => game32 ? new[] { $@"x32\{dll}", dll } : new[] { $@"x64\{dll}", dll },
            _ => throw new InvalidOperationException("Choose dgVoodoo2 or DXVK."),
        };
        ushort want = game32 ? (ushort)0x014C : (ushort)0x8664;
        var wrongArch = new List<string>();
        foreach (var rel in candidates)
        {
            var p = Path.Combine(folder, rel);
            if (!File.Exists(p)) continue;
            var m = PeReader.ReadMachine(p);
            if (m == want) return p;
            wrongArch.Add($"{rel} is {(m == 0x8664 ? "x64" : m == 0x014C ? "x86" : "unknown")}");
        }
        var hint = kind == "dgVoodoo2" ? $@"Expected MS\{(game32 ? "x86" : "x64")}\{dll} inside the extracted dgVoodoo2 zip."
                 : dll.StartsWith("d3d8", StringComparison.OrdinalIgnoreCase) ? @"Direct3D 8 support needs DXVK 2.4 or newer (x32\d3d8.dll)."
                 : $@"Expected {(game32 ? "x32" : "x64")}\{dll} inside the extracted DXVK archive.";
        throw new InvalidOperationException($"No {(game32 ? "32-bit (x86)" : "64-bit")} {dll} found. {hint}" +
            (wrongArch.Count > 0 ? " Found but wrong architecture: " + string.Join(", ", wrongArch) : ""));
    }

    /// <summary>Finds the wrapper DLL(s) matching the game's Direct3D version AND architecture.</summary>
    public static WrapperSource Locate(string kind, string folder, bool game32, string api = "d3d9")
    {
        if (!Directory.Exists(folder)) throw new InvalidOperationException("Wrapper folder does not exist.");
        var src = new WrapperSource();
        var main = api == "d3d8" ? "d3d8.dll" : "d3d9.dll";
        src.Files.Add((FindDll(kind, folder, game32, main), main));
        // DXVK implements D3D8 on top of its own D3D9, so both DLLs must sit next to the game.
        if (kind == "DXVK" && api == "d3d8") src.Files.Add((FindDll(kind, folder, game32, "d3d9.dll"), "d3d9.dll"));
        if (kind == "dgVoodoo2")
            src.ConfigTemplate = new[] { "dgVoodoo.conf", @"..\..\dgVoodoo.conf", @"..\dgVoodoo.conf" }
                .Select(c => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(src.Files[0].Source)!, c))).FirstOrDefault(File.Exists);
        src.Description = $"{kind} for Direct3D {(api == "d3d8" ? "8" : "9")} ({(game32 ? "x86" : "x64")}): " +
                          string.Join(" + ", src.Files.Select(f => Path.GetRelativePath(folder, f.Source)));
        return src;
    }

    /// <summary>Builds the config text. Returns notes about settings that couldn't be applied.</summary>
    public static (string Text, List<string> Notes) BuildConfig(string kind, WrapperSettings s, string? template)
    {
        var notes = new List<string>();
        if (kind == "DXVK")
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Managed by Legacy Game Launcher - removed/restored automatically on uninstall.");
            sb.AppendLine($"d3d9.presentInterval = {(s.VSync ? 1 : 0)}");
            if (s.FpsLimit > 0) sb.AppendLine($"d3d9.maxFrameRate = {s.FpsLimit}");
            if (s.ForcedWidth > 0 || s.Windowed || s.Borderless || s.RefreshCap > 0)
                notes.Add("DXVK cannot force resolution, refresh rate or windowed mode - use dgVoodoo2 for those, or set them in the game's own config.");
            return (sb.ToString(), notes);
        }

        if (template == null)
        {
            notes.Add("No dgVoodoo.conf template found next to the dgVoodoo2 files, so dgVoodoo2 defaults will be used. " +
                      "Run dgVoodooCpl.exe once in the dgVoodoo2 folder to create one, then reinstall.");
            return ("", notes);
        }
        var ini = new IniDocument(File.ReadAllText(template));
        void Set(string sec, string key, string val, string what)
        {
            if (!ini.Set(sec, key, val)) notes.Add($"'{key}' not found in this dgVoodoo2 version's config - set {what} in dgVoodooCpl instead.");
        }
        bool windowed = s.Windowed || s.Borderless;
        Set("General", "FullScreenMode", windowed ? "false" : "true", "windowed mode");
        if (s.Borderless) Set("General", "WindowedAttributes", "borderless, fullscreensize", "borderless window");
        else ini.Set("General", "WindowedAttributes", "");
        if (s.ForcedWidth > 0 && s.ForcedHeight > 0)
            Set("DirectX", "Resolution", s.RefreshCap > 0 ? $"h:{s.ForcedWidth}, v:{s.ForcedHeight}, r:{s.RefreshCap}" : $"h:{s.ForcedWidth}, v:{s.ForcedHeight}", "the resolution");
        else
        {
            ini.Set("DirectX", "Resolution", "unforced");
            if (s.RefreshCap > 0)
                notes.Add("dgVoodoo2 applies a refresh-rate cap together with a forced resolution. Set a forced width/height to make the 60 Hz cap take effect " +
                          "(or set your desktop to 60 Hz).");
        }
        Set("DirectX", "ForceVerticalSync", s.VSync ? "true" : "false", "VSync");
        if (s.FpsLimit > 0) Set("GeneralExt", "FPSLimit", s.FpsLimit.ToString(), "the FPS limit");
        ini.Set("DirectX", "dgVoodooWatermark", "false");
        return (ini.ToString(), notes);
    }

    public static async Task<(bool Ok, string Log)> InstallAsync(GameEntry g, bool game32, string api, Window? owner)
    {
        var w = g.Wrapper;
        if (w.Installed && w.InstalledKind != w.Kind)
            return (false, $"{w.InstalledKind} is currently installed. Remove it first, then install {w.Kind}.");
        var src = Locate(w.Kind, w.SourceFolder, game32, api);
        var log = new List<string> { "Source: " + src.Description };
        var ops = new List<FileOp>();
        var recs = new List<ChangeRecord?>();

        foreach (var (source, name) in src.Files)
            recs.Add(ChangeJournal.PlanPlaceFile(g, "wrapper", source, Path.Combine(g.GameDir, name), ops, $"{w.Kind} {name}"));

        var confName = w.Kind == "DXVK" ? "dxvk.conf" : "dgVoodoo.conf";
        var (text, notes) = BuildConfig(w.Kind, w, src.ConfigTemplate);
        log.AddRange(notes.Select(n => "Note: " + n));
        if (text.Length > 0)
        {
            AppPaths.Ensure();
            var staged = Path.Combine(AppPaths.Staging, $"{Guid.NewGuid():N}-{confName}");
            File.WriteAllText(staged, text);
            recs.Add(ChangeJournal.PlanPlaceFile(g, "wrapper", staged, Path.Combine(g.GameDir, confName), ops, $"{w.Kind} {confName}"));
        }

        var dllNames = string.Join(" + ", src.Files.Select(f => f.Name));
        var (ok, err) = await FileOpRunner.RunAsync(ops, $"Install {w.Kind} ({dllNames} + {confName}) into the game folder.", owner);
        if (!ok) return (false, string.Join("\n", log.Append("FAILED: " + err)));
        foreach (var r in recs.Where(r => r != null)) g.Changes.Add(r!);
        w.Installed = true;
        w.InstalledKind = w.Kind;
        w.InstalledDlls = src.Files.Select(f => f.Name).ToList();
        log.Add($"Installed into {g.GameDir}. Originals (if any) are backed up under {AppPaths.Backups}.");
        if (w.Kind == "DXVK") log.Add("DXVK needs a Vulkan-capable GPU and driver.");
        return (true, string.Join("\n", log));
    }
}
