using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LegacyGameLauncher.Services;

/// <summary>A third-party fix the launcher can fetch from its official GitHub releases.</summary>
public sealed record ToolSpec(string Id, string Name, string Repo, Regex Asset, string Licence, string What);

public sealed class ToolRelease
{
    public string Tag { get; set; } = "";
    public string AssetName { get; set; } = "";
    public string Url { get; set; } = "";
    public long Size { get; set; }
    public string? Sha256 { get; set; }
    public DateTime Published { get; set; }
}

public sealed class ToolManifest
{
    public string Id { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Source { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public DateTime Downloaded { get; set; }
}

/// <summary>
/// Downloads open-source / freeware game fixes (never Microsoft DLLs, never anything that touches copy protection)
/// from the projects' own GitHub releases into %LocalAppData%\LegacyGameLauncher\tools, verifies each file
/// against the SHA-256 that GitHub publishes for it, and extracts it safely. Installing into a game folder is
/// then done by the existing wrapper/input installers, with backup and undo.
/// </summary>
public static class ToolDownloader
{
    public static readonly ToolSpec Dinputto8 = new("dinputto8", "dinputto8", "elishacloud/dinputto8",
        new Regex(@"^dinput\.dll$", RegexOptions.IgnoreCase), "zlib licence",
        "Runs old DirectInput 1-7 games on DirectInput 8 - fixes crashes inside Windows' DINPUT.dll.");
    public static readonly ToolSpec DgVoodoo2 = new("dgvoodoo2", "dgVoodoo2", "dege-diosg/dgVoodoo2",
        new Regex(@"^dgVoodoo2_\d+(_\d+)*\.zip$", RegexOptions.IgnoreCase), "freeware",
        "Runs Direct3D 1-9 games on Direct3D 11/12 - forced resolution, refresh cap, windowed/borderless.");
    public static readonly ToolSpec Dxvk = new("dxvk", "DXVK", "doitsujin/dxvk",
        new Regex(@"^dxvk-\d+\.\d+(\.\d+)?\.tar\.gz$", RegexOptions.IgnoreCase), "zlib licence",
        "Runs Direct3D 8-11 games on Vulkan.");

    public static string Root => Path.Combine(AppPaths.Root, "tools");

    static readonly HttpClient Http = CreateClient();
    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LegacyGameLauncher/1.0");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>Latest release info from api.github.com (no download yet).</summary>
    public static async Task<ToolRelease> LatestAsync(ToolSpec t, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{t.Repo}/releases/latest", ct));
        var root = doc.RootElement;
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            if (!t.Asset.IsMatch(name)) continue;
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            // Only accept downloads that come from the project's own GitHub release.
            if (!url.StartsWith($"https://github.com/{t.Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Unexpected download location: " + url);
            string? sha = null;
            if (a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String && dg.GetString()!.StartsWith("sha256:"))
                sha = dg.GetString()!["sha256:".Length..];
            return new ToolRelease
            {
                Tag = root.GetProperty("tag_name").GetString() ?? "?", AssetName = name, Url = url,
                Size = a.GetProperty("size").GetInt64(), Sha256 = sha,
                Published = root.TryGetProperty("published_at", out var p) && p.TryGetDateTime(out var d) ? d : default,
            };
        }
        throw new InvalidOperationException($"The latest {t.Name} release has no file matching the expected name.");
    }

    /// <summary>The folder of an already-downloaded copy, or null.</summary>
    public static (string Folder, ToolManifest Manifest)? Cached(ToolSpec t)
    {
        var dir = Path.Combine(Root, t.Id);
        if (!Directory.Exists(dir)) return null;
        foreach (var sub in Directory.EnumerateDirectories(dir).OrderByDescending(Directory.GetCreationTime))
        {
            var mf = Path.Combine(sub, "manifest.json");
            if (!File.Exists(mf)) continue;
            try
            {
                var m = JsonSerializer.Deserialize<ToolManifest>(File.ReadAllText(mf), Json.Options);
                if (m != null) return (Path.Combine(sub, "files"), m);
            }
            catch { }
        }
        return null;
    }

    /// <summary>Downloads, verifies and extracts. Returns the folder that holds the tool's files.</summary>
    public static async Task<string> DownloadAsync(ToolSpec t, ToolRelease r, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var safeTag = string.Concat(r.Tag.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var dir = Path.Combine(Root, t.Id, safeTag);
        var files = Path.Combine(dir, "files");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(files);

        var archive = Path.Combine(dir, r.AssetName);
        progress?.Report($"Downloading {r.AssetName} ({r.Size / 1024.0 / 1024.0:0.0} MB) from github.com/{t.Repo}…");
        using (var resp = await Http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(archive);
            var buf = new byte[81920]; long done = 0; int n; var last = DateTime.MinValue;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (DateTime.Now - last > TimeSpan.FromMilliseconds(300) && r.Size > 0) { progress?.Report($"Downloading {r.AssetName}: {done * 100 / r.Size}%"); last = DateTime.Now; }
            }
        }

        string actual;
        await using (var fs = File.OpenRead(archive)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (r.Sha256 != null && !actual.Equals(r.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            Directory.Delete(dir, true);
            throw new InvalidOperationException($"Checksum mismatch for {r.AssetName} - the download was discarded. Expected {r.Sha256}, got {actual}.");
        }
        progress?.Report(r.Sha256 != null ? "Checksum verified against GitHub's SHA-256." : "GitHub published no checksum for this file; recorded the SHA-256 locally.");

        if (r.AssetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ExtractZip(archive, files);
        else if (r.AssetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) await ExtractTarGzAsync(archive, files, ct);
        else File.Copy(archive, Path.Combine(files, r.AssetName));
        File.Delete(archive);

        var manifest = new ToolManifest { Id = t.Id, Tag = r.Tag, Source = r.Url, Sha256 = actual, Downloaded = DateTime.Now };
        File.WriteAllText(Path.Combine(dir, "manifest.json"), JsonSerializer.Serialize(manifest, Json.Options));
        progress?.Report($"{t.Name} {r.Tag} ready.");
        return files;
    }

    static string SafeTarget(string root, string entry)
    {
        var full = Path.GetFullPath(Path.Combine(root, entry));
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Archive entry points outside the extraction folder: " + entry);
        return full;
    }

    static void ExtractZip(string zip, string dest)
    {
        using var z = ZipFile.OpenRead(zip);
        foreach (var e in z.Entries)
        {
            var target = SafeTarget(dest, e.FullName);
            if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            e.ExtractToFile(target, true);
        }
    }

    static async Task ExtractTarGzAsync(string file, string dest, CancellationToken ct)
    {
        await using var fs = File.OpenRead(file);
        await using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var tar = new TarReader(gz);
        TarEntry? e;
        while ((e = await tar.GetNextEntryAsync(cancellationToken: ct)) != null)
        {
            if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.Directory)) continue; // no links
            var target = SafeTarget(dest, e.Name);
            if (e.EntryType == TarEntryType.Directory) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await e.ExtractToFileAsync(target, true, ct);
        }
    }

    /// <summary>The folder inside an extracted tool that the installers expect (e.g. the one holding MS\ or x32\).</summary>
    public static string InstallRoot(ToolSpec t, string files)
    {
        string? marker = t.Id switch { "dgvoodoo2" => "MS", "dxvk" => "x32", _ => null };
        if (marker == null) return files;
        if (Directory.Exists(Path.Combine(files, marker))) return files;
        var hit = Directory.EnumerateDirectories(files, marker, SearchOption.AllDirectories).FirstOrDefault();
        return hit != null ? Path.GetDirectoryName(hit)! : files;
    }
}
