using System.Text;
using System.Text.Json;
using LegacyGameLauncher.Models;

namespace LegacyGameLauncher.Services;

public sealed class LoaderInfo
{
    public bool Detected { get; set; }
    public string Evidence { get; set; } = "";
    public string DllPath { get; set; } = "";
    public string? ConfigPath { get; set; }
    public string LogPath { get; set; } = "";
    public bool LoggingEnabled { get; set; } = true;
}

/// <summary>
/// Cooperates with a SafeDiscLoader2 install the user already placed next to the game (a version.dll proxy plus
/// version.json). DETECTION AND LAUNCH HYGIENE ONLY: the launcher never installs, configures or modifies the loader,
/// the game exe or its copy protection. It only avoids things that break the loader (compat layers, wrong working
/// directory) and reads the loader's own log afterwards.
/// </summary>
public static class SafeDiscLoaderSupport
{
    static readonly string[] Markers = { "SafeDiscLoader", "version-proxy" };

    public static LoaderInfo Detect(string exePath)
    {
        var dir = Path.GetDirectoryName(exePath) ?? "";
        var info = new LoaderInfo { DllPath = Path.Combine(dir, "version.dll"), LogPath = Path.Combine(dir, "version.log.txt") };
        if (!File.Exists(info.DllPath)) return info;

        var evidence = new List<string> { "version.dll next to the exe" };
        bool confirmed = false;
        try
        {
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(info.DllPath));
            foreach (var m in Markers)
                if (text.Contains(m, StringComparison.OrdinalIgnoreCase)) { evidence.Add($"'{m}' text in version.dll"); confirmed = true; }
        }
        catch { }

        var cfg = Path.Combine(dir, "version.json");
        if (File.Exists(cfg))
        {
            info.ConfigPath = cfg;
            evidence.Add("version.json beside it");
            confirmed = true;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfg), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var root = doc.RootElement;
                if (root.TryGetProperty("logfile", out var lf) && lf.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(lf.GetString()))
                {
                    var name = lf.GetString()!;
                    info.LogPath = Path.IsPathRooted(name) ? name : Path.Combine(dir, name);
                }
                if (root.TryGetProperty("logging", out var lg) && (lg.ValueKind == JsonValueKind.False)) info.LoggingEnabled = false;
                if (root.TryGetProperty("exefile", out var ef) && ef.ValueKind == JsonValueKind.String &&
                    !string.Equals(ef.GetString(), Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase))
                    evidence.Add($"note: version.json names \"{ef.GetString()}\", not {Path.GetFileName(exePath)}");
            }
            catch (Exception ex) { evidence.Add("version.json could not be read: " + ex.Message); }
        }

        // A bare version.dll without markers or version.json (e.g. a stray copy of the system DLL) is not treated as a loader;
        // the user can still force loader mode with the "On" setting.
        info.Detected = confirmed;
        info.Evidence = string.Join("; ", evidence) + (confirmed ? "" : " (no loader markers found)");
        return info;
    }

    /// <summary>Resolves the per-game Auto/On/Off setting.</summary>
    public static bool IsActive(CompatSettings c, LoaderInfo info) => c.SafeDiscLoader switch
    {
        "On" => true,
        "Off" => false,
        _ => info.Detected,
    };

    /// <summary>After launch: did the loader write its log during this run? Returns log lines for the launch log.</summary>
    /// <param name="gameLooksOk">The game ran and ended without a crash (clean exit, no error events, not a quick exit).</param>
    public static List<string> PostLaunchReport(LoaderInfo info, DateTime launchStartedLocal, bool gameLooksOk = false)
    {
        var lines = new List<string>();
        if (!info.LoggingEnabled)
        {
            lines.Add("SAFEDISC LOADER: logging is turned off in version.json (\"logging\": false), so the launcher can't confirm the loader ran.");
            return lines;
        }
        var fi = new FileInfo(info.LogPath);
        if (!fi.Exists || fi.LastWriteTime < launchStartedLocal.AddSeconds(-2))
        {
            // Seen on working runs of Cricket 07 (double-clicked and from the launcher): the game reached its menu but
            // the log was not rewritten. So a missing update only points at the loader when the game also failed.
            if (gameLooksOk)
            {
                lines.Add($"SafeDisc loader: {Path.GetFileName(info.LogPath)} was not updated during this launch, but the game ran and exited cleanly, " +
                          "so it got past the disc check. (This loader doesn't always rewrite its log.)");
                return lines;
            }
            lines.Add($"SAFEDISC LOADER MAY NOT HAVE RUN: the game failed and {Path.GetFileName(info.LogPath)} was {(fi.Exists ? $"not updated (last written {fi.LastWriteTime:yyyy-MM-dd HH:mm:ss})" : "not created")} during this launch. " +
                      "Usual causes: the game was started with a different working directory, a compatibility layer loaded the system version.dll first, " +
                      "or antivirus blocked/quarantined version.dll.");
            return lines;
        }
        lines.Add($"SafeDisc loader ran: {Path.GetFileName(info.LogPath)} updated at {fi.LastWriteTime:HH:mm:ss}. Last 20 lines:");
        foreach (var l in Tail(info.LogPath, 20)) lines.Add("  | " + l);
        return lines;
    }

    static IEnumerable<string> Tail(string path, int n)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - 64 * 1024);
            fs.Seek(start, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.Latin1);
            var all = sr.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (start > 0 && all.Count > 0) all.RemoveAt(0); // partial first line
            while (all.Count > 0 && all[^1].Length == 0) all.RemoveAt(all.Count - 1);
            return all.Skip(Math.Max(0, all.Count - n)).ToList();
        }
        catch (Exception ex) { return new[] { "(could not read log: " + ex.Message + ")" }; }
    }
}
