using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using LegacyGameLauncher.Models;

namespace LegacyGameLauncher.Services;

public sealed class ModInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTime Added { get; set; } = DateTime.Now;
    public string TargetSubfolder { get; set; } = "";
    /// <summary>Paths relative to the mod's files folder (= relative to the target folder in the game).</summary>
    public List<string> Files { get; set; } = new();
    public long Size { get; set; }
    public bool Enabled { get; set; }
    public string Notes { get; set; } = "";
    public override string ToString() => $"{(Enabled ? "● " : "○ ")}{Name}   ({Files.Count} file{(Files.Count == 1 ? "" : "s")}, {Size / 1024.0 / 1024.0:0.0} MB){(Enabled ? "  - ON" : "")}";
}

/// <summary>
/// Per-game mod library. Mods are imported (zip or folder) into %LocalAppData%\LegacyGameLauncher\mods\&lt;game&gt;\&lt;mod&gt;,
/// enabled by copying their files into the game folder through the change journal (group "mod:&lt;id&gt;", originals
/// backed up), and disabled by undoing that group. Two enabled mods may not touch the same file, so restoring one can
/// never clobber the other. Nothing inside a mod is ever executed; the game's own exe is never replaced.
/// </summary>
public static class ModManager
{
    public static string GameDir(GameEntry g) => Path.Combine(AppPaths.Root, "mods", g.Id.ToString("N"));
    static string ModDir(GameEntry g, string id) => Path.Combine(GameDir(g), id);
    static string FilesDir(GameEntry g, string id) => Path.Combine(ModDir(g, id), "files");
    public static string Group(string id) => "mod:" + id;

    public static List<ModInfo> List(GameEntry g)
    {
        var list = new List<ModInfo>();
        if (!Directory.Exists(GameDir(g))) return list;
        foreach (var d in Directory.EnumerateDirectories(GameDir(g)))
        {
            try
            {
                var m = JsonSerializer.Deserialize<ModInfo>(File.ReadAllText(Path.Combine(d, "mod.json")), Json.Options);
                if (m == null) continue;
                m.Enabled = g.Changes.Any(c => c.Group == Group(m.Id));
                list.Add(m);
            }
            catch { }
        }
        return list.OrderBy(m => m.Name).ToList();
    }

    static void SaveInfo(GameEntry g, ModInfo m) =>
        File.WriteAllText(Path.Combine(ModDir(g, m.Id), "mod.json"), JsonSerializer.Serialize(m, Json.Options));

    static string SafeTarget(string root, string rel)
    {
        var full = Path.GetFullPath(Path.Combine(root, rel));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The mod contains a path that points outside its folder: " + rel);
        return full;
    }

    /// <summary>Imports a .zip or a folder. If everything sits in one top-level folder, that folder is used as the root.</summary>
    public static ModInfo Import(GameEntry g, string path, string name, string targetSubfolder)
    {
        var id = Guid.NewGuid().ToString("N")[..10];
        var files = FilesDir(g, id);
        Directory.CreateDirectory(files);
        try
        {
            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var z = ZipFile.OpenRead(path);
                foreach (var e in z.Entries)
                {
                    if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) continue;
                    var t = SafeTarget(files, e.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                    e.ExtractToFile(t, true);
                }
            }
            else if (Directory.Exists(path))
            {
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    var t = SafeTarget(files, Path.GetRelativePath(path, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                    File.Copy(f, t, true);
                }
            }
            else throw new InvalidOperationException("Choose a .zip file or a folder.");

            // A single wrapping folder (e.g. "MyMod\...") is not part of the game's layout - unwrap it.
            while (Directory.GetFiles(files).Length == 0 && Directory.GetDirectories(files) is { Length: 1 } only)
            {
                var tmp = files + "_unwrap";
                Directory.Move(only[0], tmp);
                Directory.Delete(files, true);
                Directory.Move(tmp, files);
            }

            var all = Directory.EnumerateFiles(files, "*", SearchOption.AllDirectories).ToList();
            if (all.Count == 0) throw new InvalidOperationException("The mod is empty.");
            var m = new ModInfo
            {
                Id = id, Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/')) : name.Trim(),
                Source = path, TargetSubfolder = targetSubfolder.Trim().Trim('\\', '/'),
                Files = all.Select(f => Path.GetRelativePath(files, f)).OrderBy(f => f).ToList(),
                Size = all.Sum(f => new FileInfo(f).Length),
            };
            var exes = m.Files.Where(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToList();
            if (exes.Count > 0) m.Notes = $"Contains {exes.Count} .exe file(s) - they are copied like any other file, never run. If this mod is an installer, it can't be managed here.";
            SaveInfo(g, m);
            return m;
        }
        catch { try { Directory.Delete(ModDir(g, id), true); } catch { } throw; }
    }

    static string TargetPath(GameEntry g, ModInfo m, string rel) =>
        Path.GetFullPath(Path.Combine(g.GameDir, m.TargetSubfolder, rel));

    /// <summary>Enabled mods that write any of the same files as <paramref name="m"/>.</summary>
    public static List<(ModInfo Mod, List<string> Files)> Conflicts(GameEntry g, ModInfo m)
    {
        var mine = m.Files.Select(f => TargetPath(g, m, f)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<(ModInfo, List<string>)>();
        foreach (var other in List(g).Where(o => o.Enabled && o.Id != m.Id))
        {
            var shared = other.Files.Where(f => mine.Contains(TargetPath(g, other, f))).ToList();
            if (shared.Count > 0) result.Add((other, shared));
        }
        // Files already changed by other launcher features (wrapper, "Your own files", ...) count as conflicts too.
        var foreign = g.Changes.Where(c => c.Kind != "Registry" && !c.Group.StartsWith("mod:") && mine.Contains(c.Target)).Select(c => Path.GetFileName(c.Target)).ToList();
        if (foreign.Count > 0) result.Add((new ModInfo { Name = "another launcher fix" }, foreign));
        return result;
    }

    public static async Task<(bool Ok, string Log)> EnableAsync(GameEntry g, ModInfo m, Window? owner)
    {
        if (m.Enabled) return (true, $"{m.Name} is already on.");
        var exe = Path.GetFullPath(g.ExePath);
        var root = Path.GetFullPath(g.GameDir) + Path.DirectorySeparatorChar;
        var conflicts = Conflicts(g, m);
        if (conflicts.Count > 0)
            return (false, "Not enabled - it changes the same files as: " +
                           string.Join("; ", conflicts.Select(c => $"{c.Mod.Name} ({string.Join(", ", c.Files.Take(4))}{(c.Files.Count > 4 ? ", …" : "")})")) +
                           ". Turn that off first.");
        var ops = new List<FileOp>();
        var recs = new List<ChangeRecord>();
        foreach (var rel in m.Files)
        {
            var target = TargetPath(g, m, rel);
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return (false, $"\"{rel}\" would land outside the game folder - not enabled.");
            if (target.Equals(exe, StringComparison.OrdinalIgnoreCase)) return (false, $"The mod would replace the game's exe ({Path.GetFileName(exe)}). The launcher doesn't do that - not enabled.");
            var rec = ChangeJournal.PlanPlaceFile(g, Group(m.Id), Path.Combine(FilesDir(g, m.Id), rel), target, ops, $"Mod \"{m.Name}\": {rel}");
            if (rec != null) recs.Add(rec);
        }
        var (ok, err) = await FileOpRunner.RunAsync(ops, $"Turn on the mod \"{m.Name}\" ({m.Files.Count} files) for {g.DisplayName}. Originals are backed up.", owner);
        if (!ok) return (false, "FAILED: " + err);
        g.Changes.AddRange(recs);
        var replaced = recs.Count(r => r.Kind == "FileReplaced");
        return (true, $"{m.Name} is on: {m.Files.Count} file(s) copied{(replaced > 0 ? $", {replaced} original(s) backed up" : "")}.");
    }

    public static async Task<(bool Ok, string Log)> DisableAsync(GameEntry g, ModInfo m, Window? owner)
    {
        if (!m.Enabled) return (true, $"{m.Name} is already off.");
        var (ok, log) = await ChangeJournal.UndoAsync(g, Group(m.Id), owner);
        return (ok, ok ? $"{m.Name} is off - the game folder is back as it was." : log);
    }

    public static void Delete(GameEntry g, ModInfo m)
    {
        if (m.Enabled) throw new InvalidOperationException("Turn the mod off before removing it from the library.");
        Directory.Delete(ModDir(g, m.Id), true);
    }

    public static string FilesFolder(GameEntry g, ModInfo m) => FilesDir(g, m.Id);
}
