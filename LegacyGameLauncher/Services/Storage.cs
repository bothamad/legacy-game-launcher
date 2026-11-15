using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LegacyGameLauncher.Models;

namespace LegacyGameLauncher.Services;

public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LegacyGameLauncher");
    public static string LibraryFile => Path.Combine(Root, "library.json");
    public static string Backups => Path.Combine(Root, "backups");
    public static string Profiles => Path.Combine(Root, "profiles");
    public static string Staging => Path.Combine(Root, "staging");
    public static string Logs => Path.Combine(Root, "logs");

    public static void Ensure()
    {
        foreach (var d in new[] { Root, Backups, Profiles, Staging, Logs }) Directory.CreateDirectory(d);
    }
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

public static class LibraryStore
{
    public static LibraryData Load()
    {
        try
        {
            if (File.Exists(AppPaths.LibraryFile))
                return JsonSerializer.Deserialize<LibraryData>(File.ReadAllText(AppPaths.LibraryFile), Json.Options) ?? new();
        }
        catch (Exception ex)
        {
            // Keep a copy of the unreadable file instead of silently overwriting it.
            try { File.Copy(AppPaths.LibraryFile, AppPaths.LibraryFile + $".broken-{DateTime.Now:yyyyMMddHHmmss}", true); } catch { }
            System.Diagnostics.Debug.WriteLine(ex);
        }
        return new LibraryData();
    }

    public static void Save(LibraryData data)
    {
        var tmp = AppPaths.LibraryFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data, Json.Options));
        File.Move(tmp, AppPaths.LibraryFile, true);
    }
}

public static class ProfileCatalog
{
    public static List<GameProfile> Profiles { get; private set; } = new();
    public static List<string> LoadErrors { get; } = new();

    public static void Load()
    {
        var byId = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var res in asm.GetManifestResourceNames().Where(n => n.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
        {
            using var s = asm.GetManifestResourceStream(res)!;
            var p = JsonSerializer.Deserialize<GameProfile>(s, Json.Options);
            if (p == null || string.IsNullOrWhiteSpace(p.Id)) continue;
            p.BuiltIn = true;
            byId[p.Id] = p;
        }
        // User profiles in %LocalAppData%\LegacyGameLauncher\profiles\*.json add new games or override built-ins by Id.
        LoadErrors.Clear();
        foreach (var f in Directory.EnumerateFiles(AppPaths.Profiles, "*.json"))
        {
            try
            {
                var p = JsonSerializer.Deserialize<GameProfile>(File.ReadAllText(f), Json.Options);
                if (p == null || string.IsNullOrWhiteSpace(p.Id)) { LoadErrors.Add($"{Path.GetFileName(f)}: missing \"Id\""); continue; }
                p.BuiltIn = false;
                byId[p.Id] = p;
            }
            catch (Exception ex) { LoadErrors.Add($"{Path.GetFileName(f)}: {ex.Message}"); }
        }
        Profiles = byId.Values.OrderBy(p => p.Name).ToList();
    }

    public static GameProfile? Get(string? id) =>
        id == null ? null : Profiles.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Writes the built-in profiles to the user profile folder as editable examples.</summary>
    public static void ExportExamples()
    {
        foreach (var p in Profiles.Where(p => p.BuiltIn))
        {
            var path = Path.Combine(AppPaths.Profiles, $"example-{p.Id}.json.txt");
            try { File.WriteAllText(path, JsonSerializer.Serialize(p, Json.Options)); } catch { } // examples always mirror the current built-ins
        }
    }
}

public static class GameDetector
{
    public static string Sha256Of(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    public static (GameProfile? Profile, string Note) Detect(string exePath, string sha256)
    {
        var exeName = Path.GetFileName(exePath);
        var profiles = ProfileCatalog.Profiles;

        var byHash = profiles.FirstOrDefault(p => p.Sha256.Any(h => h.Equals(sha256, StringComparison.OrdinalIgnoreCase)));
        if (byHash != null) return (byHash, "Exact match by SHA-256 hash (high confidence).");

        var byName = profiles.FirstOrDefault(p => p.ExeNames.Any(n => n.Equals(exeName, StringComparison.OrdinalIgnoreCase)));
        if (byName != null) return (byName, $"Matched by executable name \"{exeName}\" (medium confidence; hash not in profile).");

        try
        {
            var vi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            var text = $"{vi.ProductName} | {vi.FileDescription}";
            var byVer = profiles.FirstOrDefault(p => p.ProductNameContains.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase)));
            if (byVer != null) return (byVer, $"Matched by version info \"{text.Trim(' ', '|')}\" (medium confidence).");
        }
        catch { }

        var folder = Path.GetFileName(Path.GetDirectoryName(exePath) ?? "") ?? "";
        var byFolder = profiles.FirstOrDefault(p => p.FolderNameContains.Any(s => folder.Contains(s, StringComparison.OrdinalIgnoreCase)));
        if (byFolder != null) return (byFolder, $"Matched by install folder name \"{folder}\" (LOW confidence - check this is the game's main exe).");

        return (null, "No built-in profile matched. Generic settings are available; you can add a profile JSON for this game.");
    }
}
