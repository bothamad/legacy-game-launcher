using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace LegacyGameLauncher.Services;

public sealed class FileOp
{
    /// <summary>Copy (Source -> Target, overwrite) or Delete (Target, if it exists). Both are idempotent.</summary>
    public string Op { get; set; } = "";
    public string Source { get; set; } = "";
    public string Target { get; set; } = "";
    public override string ToString() => Op == "Copy" ? $"Copy  {Source}\n   -> {Target}" : $"Delete {Target}";
}

/// <summary>
/// Runs file operations as the current user; if a target folder (e.g. Program Files) is not writable,
/// explains why and runs the same operations once in an elevated copy of this exe ("--fileops").
/// </summary>
public static class FileOpRunner
{
    public static void Execute(IEnumerable<FileOp> ops)
    {
        var winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var op in ops)
        {
            var target = Path.GetFullPath(op.Target);
            if (target.StartsWith(winDir + "\\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Refusing to modify a Windows system path: {target}");
            switch (op.Op)
            {
                case "Copy":
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(op.Source, target, true);
                    break;
                case "Delete":
                    if (File.Exists(target)) File.Delete(target);
                    break;
                default: throw new InvalidOperationException("Unknown op " + op.Op);
            }
        }
    }

    static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, $".lgl-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    public static async Task<(bool Ok, string Error)> RunAsync(List<FileOp> ops, string reason, Window? owner)
    {
        if (ops.Count == 0) return (true, "");
        var dirs = ops.Select(o => Path.GetDirectoryName(Path.GetFullPath(o.Target))!).Distinct(StringComparer.OrdinalIgnoreCase);
        bool needsAdmin = dirs.Any(d => !CanWrite(d));
        if (!needsAdmin)
        {
            try { Execute(ops); return (true, ""); }
            catch (UnauthorizedAccessException) { needsAdmin = true; }
            catch (Exception ex) { return (false, ex.Message); }
        }

        var preview = string.Join("\n", ops.Take(8).Select(o => "• " + o)) + (ops.Count > 8 ? $"\n• … and {ops.Count - 8} more" : "");
        var msg = $"Administrator permission is needed for this action:\n\n{reason}\n\n" +
                  "The game folder is protected by Windows (typically because it is under Program Files). " +
                  "The launcher will start a short-lived elevated helper that performs only these file operations and then exits:\n\n" +
                  preview + "\n\nContinue? (Windows will show a UAC prompt.)";
        var answer = owner != null
            ? MessageBox.Show(owner, msg, "Admin rights needed", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(msg, "Admin rights needed", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes) return (false, "Cancelled - admin permission was not granted.");

        AppPaths.Ensure();
        var file = Path.Combine(AppPaths.Staging, $"ops-{Guid.NewGuid():N}.json");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(ops, Json.Options);
        File.WriteAllBytes(file, bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var resultFile = file + ".result";
        try
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = true,
                Verb = "runas",
                ArgumentList = { "--fileops", file, hash },
            };
            using var p = Process.Start(psi)!;
            await p.WaitForExitAsync();
            var err = File.Exists(resultFile) ? File.ReadAllText(resultFile) : "";
            return p.ExitCode == 0 ? (true, "") : (false, string.IsNullOrEmpty(err) ? $"Elevated helper failed (exit {p.ExitCode})." : err);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, "Cancelled at the UAC prompt.");
        }
        finally
        {
            try { File.Delete(file); File.Delete(resultFile); } catch { }
        }
    }

    /// <summary>Entry point for the elevated helper. Verifies the ops file hash given on the command line before acting.</summary>
    public static int RunHelper(string file, string expectedHash)
    {
        var resultFile = file + ".result";
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Operation file was modified after it was approved; nothing was done.");
            var ops = JsonSerializer.Deserialize<List<FileOp>>(bytes, Json.Options) ?? new();
            Execute(ops);
            return 0;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(resultFile, ex.Message); } catch { }
            return 1;
        }
    }
}
