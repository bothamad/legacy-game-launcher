using System.Windows;
using LegacyGameLauncher.Models;
using Microsoft.Win32;

namespace LegacyGameLauncher.Services;

/// <summary>
/// Records the ORIGINAL state of everything the launcher touches (first change wins), so any group of
/// changes - or all of them - can be reverted exactly.
/// </summary>
public static class ChangeJournal
{
    public static bool HasFileRecord(GameEntry g, string target) =>
        g.Changes.Any(c => c.Kind != "Registry" && c.Target.Equals(target, StringComparison.OrdinalIgnoreCase));

    public static string NewBackupPath(GameEntry g, string target)
    {
        var dir = Path.Combine(AppPaths.Backups, g.Id.ToString("N"), DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Path.GetFileName(target));
    }

    /// <summary>
    /// Adds ops that place <paramref name="source"/> at <paramref name="target"/>, backing up any original first,
    /// and returns the journal record to commit if the ops succeed (null if the target is already journaled).
    /// </summary>
    public static ChangeRecord? PlanPlaceFile(GameEntry g, string group, string source, string target, List<FileOp> ops, string description)
    {
        ChangeRecord? rec = null;
        if (!HasFileRecord(g, target))
        {
            if (File.Exists(target))
            {
                var backup = NewBackupPath(g, target);
                ops.Add(new FileOp { Op = "Copy", Source = target, Target = backup });
                rec = new ChangeRecord { Group = group, Kind = "FileReplaced", Target = target, BackupPath = backup, PreviousExisted = true, Description = description + " (original backed up)" };
            }
            else rec = new ChangeRecord { Group = group, Kind = "FileAdded", Target = target, Description = description };
        }
        ops.Add(new FileOp { Op = "Copy", Source = source, Target = target });
        return rec;
    }

    public static void RecordRegistryOnce(GameEntry g, string group, string hiveKey, string name, string? previous, string description)
    {
        if (g.Changes.Any(c => c.Kind == "Registry" && c.Target == hiveKey && c.RegistryName == name)) return;
        g.Changes.Add(new ChangeRecord
        {
            Group = group, Kind = "Registry", Target = hiveKey, RegistryName = name,
            PreviousExisted = previous != null, PreviousValue = previous, Description = description,
        });
    }

    /// <summary>Reverts records of one group (or all when group is null). Returns a human-readable log.</summary>
    public static async Task<(bool Ok, string Log)> UndoAsync(GameEntry g, string? group, Window? owner)
    {
        var records = g.Changes.Where(c => group == null || c.Group == group).Reverse().ToList();
        if (records.Count == 0) return (true, "Nothing to undo.");
        var log = new List<string>();

        // Registry (HKCU only - no elevation needed).
        foreach (var r in records.Where(r => r.Kind == "Registry"))
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(r.Target.Replace(@"HKCU\", ""), true);
                if (r.PreviousExisted) key.SetValue(r.RegistryName!, r.PreviousValue ?? "");
                else key.DeleteValue(r.RegistryName!, false);
                log.Add($"Restored registry {r.Target}\\{r.RegistryName} -> {(r.PreviousExisted ? r.PreviousValue : "(removed)")}");
                g.Changes.Remove(r);
            }
            catch (Exception ex) { log.Add($"FAILED registry {r.RegistryName}: {ex.Message}"); }
        }

        var fileRecs = records.Where(r => r.Kind != "Registry").ToList();
        var ops = new List<FileOp>();
        foreach (var r in fileRecs)
        {
            if (r.Kind == "FileReplaced" && r.BackupPath != null)
            {
                if (!File.Exists(r.BackupPath)) { log.Add($"WARNING backup missing for {r.Target}: {r.BackupPath}"); continue; }
                ops.Add(new FileOp { Op = "Copy", Source = r.BackupPath, Target = r.Target });
            }
            else if (r.Kind == "FileAdded") ops.Add(new FileOp { Op = "Delete", Target = r.Target });
        }
        if (ops.Count > 0)
        {
            var (ok, err) = await FileOpRunner.RunAsync(ops, $"Undo {(group ?? "all")} changes for {g.DisplayName}", owner);
            if (!ok) { log.Add("File restore failed: " + err); return (false, string.Join("\n", log)); }
        }
        foreach (var r in fileRecs)
        {
            log.Add(r.Kind == "FileAdded" ? $"Removed {r.Target}" : $"Restored original {r.Target}");
            g.Changes.Remove(r);
        }
        if (group is null or "wrapper") { g.Wrapper.Installed = false; g.Wrapper.InstalledKind = ""; g.Wrapper.InstalledDlls.Clear(); }
        if (group is null or "input") { g.Input.Installed = false; g.Input.InstalledKind = ""; }
        return (true, string.Join("\n", log));
    }
}
