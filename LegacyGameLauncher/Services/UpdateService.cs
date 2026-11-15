using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace LegacyGameLauncher.Services;

public sealed class UpdateInfo
{
    public Version Latest { get; set; } = new();
    public string Tag { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Url { get; set; } = "";
    public string PageUrl { get; set; } = "";
    public long Size { get; set; }
    public string? Sha256 { get; set; }
    public bool IsNewer => Latest > UpdateService.Current;
}

/// <summary>
/// Checks this app's own public GitHub releases for a newer version and updates in place: downloads
/// LegacyGameLauncher.exe from the release, verifies GitHub's SHA-256, swaps it with the running exe
/// (a running exe can be renamed, not overwritten) and restarts.
/// </summary>
public static class UpdateService
{
    public const string AssetName = "LegacyGameLauncher.exe";

    public static string Repo =>
        Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepo")?.Value ?? "";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    static readonly HttpClient Http = Create();
    static HttpClient Create()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LegacyGameLauncher/" + Current);
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    static Version ParseTag(string tag)
    {
        var s = tag.TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(s, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : new Version(0, 0);
    }

    public static async Task<UpdateInfo> CheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Repo)) throw new InvalidOperationException("This build has no update repository configured.");
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct));
        var root = doc.RootElement;
        var info = new UpdateInfo
        {
            Tag = root.GetProperty("tag_name").GetString() ?? "",
            Notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
            PageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
        };
        info.Latest = ParseTag(info.Tag);
        foreach (var a in root.GetProperty("assets").EnumerateArray())
        {
            if (!string.Equals(a.GetProperty("name").GetString(), AssetName, StringComparison.OrdinalIgnoreCase)) continue;
            info.Url = a.GetProperty("browser_download_url").GetString() ?? "";
            info.Size = a.GetProperty("size").GetInt64();
            if (a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString()!.StartsWith("sha256:"))
                info.Sha256 = d.GetString()!["sha256:".Length..];
        }
        if (info.Url.Length > 0 && !info.Url.StartsWith($"https://github.com/{Repo}/releases/download/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected download location: " + info.Url);
        return info;
    }

    /// <summary>Downloads and swaps in the new exe. Returns the path to start; the caller restarts the app.</summary>
    public static async Task<string> DownloadAndSwapAsync(UpdateInfo u, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (u.Url.Length == 0) throw new InvalidOperationException($"Release {u.Tag} has no {AssetName} attached.");
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot find the running exe.");
        var dir = Path.GetDirectoryName(exe)!;
        var tmp = Path.Combine(dir, AssetName + ".download");
        try { File.WriteAllText(tmp, ""); File.Delete(tmp); }
        catch { throw new InvalidOperationException($"The launcher's folder ({dir}) isn't writable. Download {u.Tag} from {u.PageUrl} and replace the exe by hand."); }

        progress?.Report($"Downloading {u.Tag} ({u.Size / 1024.0 / 1024.0:0.0} MB)…");
        using (var resp = await Http.GetAsync(u.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buf = new byte[81920]; long done = 0; int n; var last = DateTime.MinValue;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct); done += n;
                if (u.Size > 0 && DateTime.Now - last > TimeSpan.FromMilliseconds(300)) { progress?.Report($"Downloading {u.Tag}: {done * 100 / u.Size}%"); last = DateTime.Now; }
            }
        }
        string actual;
        await using (var fs = File.OpenRead(tmp)) actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        if (u.Sha256 != null && !actual.Equals(u.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tmp);
            throw new InvalidOperationException("Checksum mismatch - the update was discarded.");
        }
        if (PeReader.ReadMachine(tmp) != 0x8664) { File.Delete(tmp); throw new InvalidOperationException("The downloaded file isn't a 64-bit Windows exe - discarded."); }

        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);           // allowed while running
        File.Move(tmp, exe);
        progress?.Report($"Installed {u.Tag}. Restarting…");
        return exe;
    }

    public static void Restart(string exe)
    {
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>Removes the previous version left behind by an update.</summary>
    public static void CleanupOld()
    {
        try { var old = Environment.ProcessPath + ".old"; if (File.Exists(old)) File.Delete(old); } catch { }
    }
}
