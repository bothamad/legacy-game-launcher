using System.Security.Cryptography;
using System.Windows;
using LegacyGameLauncher.Models;

namespace LegacyGameLauncher.Services;

/// <summary>
/// "Your own files": copies files the user chose (a mod, a widescreen fix, a config pack) into the game folder,
/// backing up anything replaced and journaling every file under the "custom" group so it can be undone.
/// A plain, general-purpose copy - it has no knowledge of any particular tool. The one thing it refuses is
/// replacing the game's own executable.
/// </summary>
public static class CustomFiles
{
    public const string Group = "custom";

    /// <summary>(source file, path relative to the game folder) for a set of files or one folder.</summary>
    public static List<(string Source, string Relative)> Collect(IEnumerable<string> files, string? folder, string subfolder)
    {
        var list = new List<(string, string)>();
        subfolder = subfolder.Trim().Trim('\\', '/');
        if (folder != null)
        {
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                list.Add((f, Path.Combine(subfolder, Path.GetRelativePath(folder, f))));
        }
        foreach (var f in files) list.Add((f, Path.Combine(subfolder, Path.GetFileName(f))));
        return list;
    }

    public static async Task<(bool Ok, string Log)> InstallAsync(GameEntry g, List<(string Source, string Relative)> items, Window? owner)
    {
        if (items.Count == 0) return (false, "No files selected.");
        var root = Path.GetFullPath(g.GameDir) + Path.DirectorySeparatorChar;
        var exe = Path.GetFullPath(g.ExePath);
        var ops = new List<FileOp>();
        var recs = new List<ChangeRecord>();
        var entries = new List<CustomFileEntry>();
        var log = new List<string>();

        foreach (var (src, rel) in items)
        {
            var target = Path.GetFullPath(Path.Combine(g.GameDir, rel));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                return (false, $"\"{rel}\" would land outside the game folder - nothing was copied.");
            if (target.Equals(exe, StringComparison.OrdinalIgnoreCase))
                return (false, $"{Path.GetFileName(exe)} is the game's own executable. The launcher doesn't replace it - nothing was copied.");
            if (Path.GetFullPath(src).Equals(target, StringComparison.OrdinalIgnoreCase)) { log.Add($"Skipped {rel} (already the file in the game folder)."); continue; }

            bool existed = File.Exists(target);
            var rec = ChangeJournal.PlanPlaceFile(g, Group, src, target, ops, "Your file " + rel);
            if (rec != null) recs.Add(rec);
            var fi = new FileInfo(src);
            string sha;
            await using (var fs = File.OpenRead(src)) sha = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            entries.Add(new CustomFileEntry { RelativePath = rel, Source = src, Sha256 = sha, Size = fi.Length, ReplacedOriginal = existed || g.Changes.Any(c => c.Group == Group && c.Kind == "FileReplaced" && c.Target.Equals(target, StringComparison.OrdinalIgnoreCase)) });
        }
        if (ops.Count == 0) return (true, string.Join("\n", log.DefaultIfEmpty("Nothing to copy.")));

        var (ok, err) = await FileOpRunner.RunAsync(ops, $"Copy {entries.Count} file(s) you chose into the {g.DisplayName} folder (originals backed up).", owner);
        if (!ok) return (false, "FAILED: " + err);

        g.Changes.AddRange(recs);
        foreach (var e in entries)
        {
            g.CustomFiles.RemoveAll(x => x.RelativePath.Equals(e.RelativePath, StringComparison.OrdinalIgnoreCase));
            g.CustomFiles.Add(e);
            log.Add($"Copied {e.RelativePath}{(e.ReplacedOriginal ? " (original backed up)" : "")}");
        }
        return (true, string.Join("\n", log));
    }
}
