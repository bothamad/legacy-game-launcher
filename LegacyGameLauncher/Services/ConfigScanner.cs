using System.Text;
using System.Text.RegularExpressions;
using LegacyGameLauncher.Models;
using Microsoft.Win32;

namespace LegacyGameLauncher.Services;

public sealed class ConfigCandidate
{
    public string Path { get; set; } = "";
    public string Location { get; set; } = "";
    public long Size { get; set; }
    public DateTime Modified { get; set; }
    public bool IsText { get; set; }
    public int Score { get; set; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Kind => IsText ? "text" : "binary";
}

public sealed class RegistryValueRow
{
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
}

public sealed class DetectedSetting
{
    public int Line { get; set; }
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}

/// <summary>
/// Finds likely config files instead of assuming fixed paths: install folder, VirtualStore redirection,
/// Documents / AppData / LocalAppData (incl. EA Sports and My Games subfolders), and keyword-matched folders.
/// </summary>
public static class ConfigScanner
{
    static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase) { ".ini", ".cfg", ".txt", ".xml", ".conf", ".opt", ".set", ".json", ".prf" };
    static readonly string[] Hot = { "config", "option", "setting", "video", "display", "graphic", "pref", "setup", "user", "profile" };
    static readonly string[] Managed = { "dgvoodoo.conf", "dxvk.conf", "x360ce.ini" };

    public static List<ConfigCandidate> Scan(GameEntry g, GameProfile? p)
    {
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var results = new Dictionary<string, ConfigCandidate>(StringComparer.OrdinalIgnoreCase);

        void AddDir(string dir, string label, int depth, bool installFolder)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = depth > 0, MaxRecursionDepth = depth, IgnoreInaccessible = true }))
                {
                    var fi = new FileInfo(f);
                    bool textExt = TextExt.Contains(fi.Extension);
                    if (installFolder && !textExt) continue;           // game data archives are not configs
                    if (fi.Length > (textExt ? 1_000_000 : 256_000)) continue;
                    if (results.ContainsKey(f)) continue;
                    var name = fi.Name.ToLowerInvariant();
                    int score = Hot.Count(h => name.Contains(h)) * 10 + (installFolder ? 0 : 5) + (textExt ? 3 : 0);
                    if (fi.Extension.Equals(".pro", StringComparison.OrdinalIgnoreCase)) score += 25; // EA Sports / HB Studios profile (holds the resolution)
                    if (fi.LastWriteTime > DateTime.Now.AddDays(-30)) score += 2;
                    results[f] = new ConfigCandidate
                    {
                        Path = f, Size = fi.Length, Modified = fi.LastWriteTime, Score = score, IsText = LooksText(f),
                        Location = label + (Managed.Contains(name) ? " (managed by launcher)" : ""),
                    };
                }
            }
            catch { }
        }

        AddDir(g.GameDir, "install folder", 2, true);

        // UAC file virtualization: a 32-bit game without a manifest that writes into Program Files is silently redirected here.
        var root = Path.GetPathRoot(g.GameDir) ?? "";
        var virtualStore = Path.Combine(local, "VirtualStore", g.GameDir[root.Length..]);
        AddDir(virtualStore, "VirtualStore (redirected writes)", 3, false);

        var names = new List<string>(p?.ConfigFolderNames ?? new());
        names.Add(Path.GetFileName(g.GameDir));
        names.Add(g.DisplayName);
        foreach (var n in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var (baseDir, label) in new[] { (docs, "Documents"), (Path.Combine(docs, "EA Sports"), "Documents\\EA Sports"),
                                                       (Path.Combine(docs, "My Games"), "Documents\\My Games"), (roaming, "AppData\\Roaming"),
                                                       (Path.Combine(roaming, "EA Sports"), "AppData\\Roaming\\EA Sports"), (local, "AppData\\Local"),
                                                       (Path.Combine(local, "EA Sports"), "AppData\\Local\\EA Sports") })
                AddDir(Path.Combine(baseDir, n), label, 3, false);
        }

        // Keyword search one level deep for folders we didn't predict.
        var keywords = (p?.FolderNameContains ?? new()).Append(Path.GetFileName(g.GameDir)).Where(k => k.Length >= 4).Distinct().ToList();
        foreach (var baseDir in new[] { docs, Path.Combine(docs, "EA Sports"), Path.Combine(docs, "My Games"), roaming, local, Path.Combine(local, "EA Sports") })
        {
            if (!Directory.Exists(baseDir)) continue;
            try
            {
                foreach (var d in Directory.EnumerateDirectories(baseDir))
                    if (keywords.Any(k => Path.GetFileName(d).Contains(k, StringComparison.OrdinalIgnoreCase)))
                        AddDir(d, "keyword match: " + Path.GetFileName(baseDir), 3, false);
            }
            catch { }
        }

        return results.Values.OrderByDescending(c => c.Score).ThenByDescending(c => c.Modified).ToList();
    }

    public static List<RegistryValueRow> ScanRegistry(GameProfile? p, string gameFolderName)
    {
        var rows = new List<RegistryValueRow>();
        var keys = new List<string>(p?.RegistryKeys ?? new());
        // Keyword search under the usual publisher keys (32-bit games use the WOW6432Node view).
        foreach (var parent in new[] { @"HKLM\SOFTWARE\WOW6432Node\EA SPORTS", @"HKLM\SOFTWARE\WOW6432Node\Electronic Arts", @"HKLM\SOFTWARE\WOW6432Node\Electronic Arts\EA SPORTS",
                                       @"HKCU\Software\EA SPORTS", @"HKCU\Software\Electronic Arts", @"HKCU\Software\Classes\VirtualStore\MACHINE\SOFTWARE\WOW6432Node\EA SPORTS" })
        {
            using var k = Open(parent);
            if (k == null) continue;
            foreach (var sub in k.GetSubKeyNames())
                if (sub.Contains(gameFolderName, StringComparison.OrdinalIgnoreCase) ||
                    (p?.FolderNameContains ?? new()).Any(f => sub.Contains(f, StringComparison.OrdinalIgnoreCase)))
                    keys.Add(parent + "\\" + sub);
        }
        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var k = Open(key);
            if (k == null) continue;
            foreach (var n in k.GetValueNames())
                rows.Add(new RegistryValueRow { Key = key, Name = n == "" ? "(Default)" : n, Value = Format(k.GetValue(n)) });
            foreach (var sub in k.GetSubKeyNames().Take(20))
            {
                using var sk = k.OpenSubKey(sub);
                if (sk == null) continue;
                foreach (var n in sk.GetValueNames())
                    rows.Add(new RegistryValueRow { Key = key + "\\" + sub, Name = n == "" ? "(Default)" : n, Value = Format(sk.GetValue(n)) });
            }
        }
        return rows;
    }

    static string Format(object? v) => v switch
    {
        byte[] b => BitConverter.ToString(b.Take(32).ToArray()) + (b.Length > 32 ? "…" : ""),
        string[] a => string.Join(" | ", a),
        null => "",
        _ => v.ToString() ?? "",
    };

    static RegistryKey? Open(string path)
    {
        var hive = path.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase)
            ? RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            : RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        var sub = path[(path.IndexOf('\\') + 1)..];
        try { return hive.OpenSubKey(sub); } catch { return null; }
    }

    public static bool LooksText(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var buf = new byte[Math.Min(4096, fs.Length)];
            int n = fs.Read(buf, 0, buf.Length);
            if (n >= 2 && ((buf[0] == 0xFF && buf[1] == 0xFE) || (buf[0] == 0xFE && buf[1] == 0xFF))) return true; // UTF-16 BOM
            int bad = 0;
            for (int i = 0; i < n; i++) if (buf[i] == 0 || (buf[i] < 9) || (buf[i] > 13 && buf[i] < 32)) bad++;
            return n == 0 || bad * 20 < n;
        }
        catch { return false; }
    }

    public static (string Text, Encoding Encoding) ReadText(string path)
    {
        using var sr = new StreamReader(path, Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
        var text = sr.ReadToEnd();
        return (text, sr.CurrentEncoding);
    }

    public static string HexPreview(string path, int max = 1024)
    {
        var bytes = File.ReadAllBytes(path).Take(max).ToArray();
        var sb = new StringBuilder();
        for (int i = 0; i < bytes.Length; i += 16)
        {
            var chunk = bytes.Skip(i).Take(16).ToArray();
            sb.Append($"{i:X6}  {BitConverter.ToString(chunk).Replace('-', ' '),-48}  ");
            sb.AppendLine(new string(chunk.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray()));
        }
        return sb.ToString();
    }

    // ---- Binary profiles (.pro) --------------------------------------------------------------
    // Community-documented for Rugby 08 and Cricket 07: the resolution is two little-endian 16-bit values,
    // width then height (e.g. 1024x768 = 00 04 00 03). The offset differs between profiles, so we search.

    static readonly (int W, int H)[] CommonResolutions =
    {
        (640, 480), (800, 600), (1024, 768), (1152, 864), (1280, 720), (1280, 768), (1280, 800), (1280, 960), (1280, 1024),
        (1360, 768), (1366, 768), (1400, 1050), (1440, 900), (1600, 900), (1600, 1200), (1680, 1050), (1920, 1080),
        (1920, 1200), (2560, 1080), (2560, 1440), (2560, 1600), (3440, 1440), (3840, 2160),
    };

    public sealed record ResolutionMatch(int Offset, int Width, int Height)
    {
        public override string ToString() => $"offset 0x{Offset:X} ({Offset}): {Width}x{Height}";
    }

    public static List<ResolutionMatch> FindResolutionPairs(byte[] d)
    {
        var list = new List<ResolutionMatch>();
        for (int i = 0; i + 4 <= d.Length; i++)
        {
            int w = d[i] | d[i + 1] << 8, h = d[i + 2] | d[i + 3] << 8;
            if (CommonResolutions.Contains((w, h))) list.Add(new ResolutionMatch(i, w, h));
        }
        return list;
    }

    public static byte[] WithResolution(byte[] d, int offset, int w, int h)
    {
        if (w is <= 0 or > 0xFFFF || h is <= 0 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(w), "Resolution out of range.");
        var copy = (byte[])d.Clone();
        copy[offset] = (byte)(w & 0xFF); copy[offset + 1] = (byte)(w >> 8);
        copy[offset + 2] = (byte)(h & 0xFF); copy[offset + 3] = (byte)(h >> 8);
        return copy;
    }

    static readonly Regex KeyLine = new(@"^\s*(?<key>[^=:;#\[\]\r\n]*?(width|height|resx|resy|xres|yres|res_x|res_y|screenx|screeny|resolution|windowed|fullscreen|full_screen|refresh|vsync)[^=:;#\r\n]*?)\s*[=:]\s*(?<val>[^\r\n]*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex XmlElem = new(@"<(?<key>\w*(width|height|windowed|fullscreen|refresh|resolution)\w*)>(?<val>[^<]*)</\k<key>>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static List<DetectedSetting> DetectSettings(string text)
    {
        var list = new List<DetectedSetting>();
        foreach (Match m in KeyLine.Matches(text))
            list.Add(new DetectedSetting { Line = LineOf(text, m.Index), Key = m.Groups["key"].Value.Trim(), Value = m.Groups["val"].Value.Trim() });
        foreach (Match m in XmlElem.Matches(text))
            list.Add(new DetectedSetting { Line = LineOf(text, m.Index), Key = "<" + m.Groups["key"].Value + ">", Value = m.Groups["val"].Value });
        return list;
    }

    static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    /// <summary>Rewrites recognised resolution/windowed keys. Returns the new text and what was changed.</summary>
    public static (string Text, List<string> Changes) ApplyQuickSettings(string text, int width, int height, bool? windowed)
    {
        var changes = new List<string>();
        string Classify(string key)
        {
            var k = key.ToLowerInvariant().Replace(" ", "");
            if (k.Contains("windowed")) return "windowed";
            if (k.Contains("fullscreen") || k.Contains("full_screen")) return "fullscreen";
            if (k.Contains("refresh")) return "";
            if (k.Contains("resolution")) return "resolution";
            if (k.Contains("width") || k.Contains("resx") || k.Contains("xres") || k.Contains("res_x") || k.Contains("screenx")) return "width";
            if (k.Contains("height") || k.Contains("resy") || k.Contains("yres") || k.Contains("res_y") || k.Contains("screeny")) return "height";
            return "";
        }
        string Bool(string old, bool v)
        {
            var o = old.Trim().ToLowerInvariant();
            if (o is "true" or "false") return v ? "true" : "false";
            if (o is "yes" or "no") return v ? "yes" : "no";
            return v ? "1" : "0";
        }
        string? NewValue(string kind, string old) => kind switch
        {
            "width" when width > 0 => width.ToString(),
            "height" when height > 0 => height.ToString(),
            "resolution" when width > 0 && height > 0 && Regex.IsMatch(old, @"\d+\s*[xX*,]\s*\d+") =>
                Regex.Replace(old, @"(\d+)(\s*[xX*,]\s*)(\d+)", $"{width}${{2}}{height}"),
            "windowed" when windowed != null => Bool(old, windowed.Value),
            "fullscreen" when windowed != null => Bool(old, !windowed.Value),
            _ => null,
        };

        text = KeyLine.Replace(text, m =>
        {
            var key = m.Groups["key"].Value; var old = m.Groups["val"].Value;
            var nv = NewValue(Classify(key), old);
            if (nv == null || nv == old.Trim()) return m.Value;
            changes.Add($"{key.Trim()}: {old.Trim()} -> {nv}");
            var g = m.Groups["val"];
            return m.Value[..(g.Index - m.Index)] + nv;
        });
        text = XmlElem.Replace(text, m =>
        {
            var key = m.Groups["key"].Value; var old = m.Groups["val"].Value;
            var nv = NewValue(Classify(key), old);
            if (nv == null || nv == old.Trim()) return m.Value;
            changes.Add($"<{key}>: {old} -> {nv}");
            return $"<{key}>{nv}</{key}>";
        });
        return (text, changes);
    }
}
