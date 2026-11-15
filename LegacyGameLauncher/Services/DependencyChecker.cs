namespace LegacyGameLauncher.Services;

public sealed class DependencyRow
{
    public string Dll { get; set; } = "";
    public string Package { get; set; } = "";
    public bool Present { get; set; }
    public string Where { get; set; } = "";
    public bool UsedByGame { get; set; }
    public string Status => Present ? "OK" : UsedByGame ? "MISSING - needed by this game" : "missing (not referenced by this game)";
}

public sealed class DependencyReport
{
    public List<DependencyRow> Rows { get; } = new();
    public List<string> Advice { get; } = new();
    public bool HasBlockingIssue => Rows.Any(r => r.UsedByGame && !r.Present);
}

/// <summary>
/// Checks the legacy runtimes 2006-2008 games expect. Only *detects* and links to official Microsoft installers;
/// no Microsoft DLLs are bundled or copied.
/// </summary>
public static class DependencyChecker
{
    public const string DirectXJune2010Url = "https://www.microsoft.com/en-us/download/details.aspx?id=8109";
    public const string VcRedistUrl = "https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist";
    public const string OpenAlUrl = "https://www.openal.org/downloads/";

    const string DxPkg = "DirectX End-User Runtime (June 2010)";

    static IEnumerable<string> DirectXDlls()
    {
        for (int i = 24; i <= 43; i++) yield return $"d3dx9_{i}.dll";
        yield return "d3dcompiler_43.dll";
        for (int i = 1; i <= 3; i++) yield return $"xinput1_{i}.dll";
        for (int i = 0; i <= 7; i++) yield return $"xaudio2_{i}.dll";
        for (int i = 0; i <= 7; i++) yield return $"x3daudio1_{i}.dll";
        for (int i = 0; i <= 7; i++) yield return $"xactengine3_{i}.dll";
    }

    /// <param name="is32BitGame">x86 games load from SysWOW64, x64 games from System32.</param>
    public static DependencyReport Check(string gameDir, bool is32BitGame, ISet<string> referenced)
    {
        var report = new DependencyReport();
        var sysDir = is32BitGame
            ? Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)   // C:\Windows\SysWOW64
            : Environment.GetFolderPath(Environment.SpecialFolder.System);
        string sysLabel = is32BitGame ? "SysWOW64" : "System32";

        (bool, string) Locate(string dll)
        {
            if (File.Exists(Path.Combine(gameDir, dll))) return (true, "game folder");
            if (File.Exists(Path.Combine(sysDir, dll))) return (true, sysLabel);
            return (false, "");
        }

        foreach (var dll in DirectXDlls())
        {
            var (ok, where) = Locate(dll);
            report.Rows.Add(new DependencyRow { Dll = dll, Package = DxPkg, Present = ok, Where = where, UsedByGame = referenced.Contains(dll) });
        }

        // Anything the game references that isn't in our list but matches DirectX-family names (e.g. xactengine2_x).
        foreach (var dll in referenced.Where(r => (r.StartsWith("d3dx") || r.StartsWith("xact") || r.StartsWith("xaudio") || r.StartsWith("x3daudio") || r.StartsWith("xinput1_"))
                                                && report.Rows.All(x => !x.Dll.Equals(r, StringComparison.OrdinalIgnoreCase))))
        {
            var (ok, where) = Locate(dll);
            report.Rows.Add(new DependencyRow { Dll = dll, Package = DxPkg, Present = ok, Where = where, UsedByGame = true });
        }

        // Visual C++ 2005 / 2008 runtimes live in WinSxS, not System32.
        foreach (var (dll, ver, label) in new[] { ("msvcr80.dll", "vc80", "Visual C++ 2005 x86 Redistributable"),
                                                   ("msvcp80.dll", "vc80", "Visual C++ 2005 x86 Redistributable"),
                                                   ("msvcr90.dll", "vc90", "Visual C++ 2008 x86 Redistributable"),
                                                   ("msvcp90.dll", "vc90", "Visual C++ 2008 x86 Redistributable") })
        {
            if (!referenced.Contains(dll)) continue;
            bool inGame = File.Exists(Path.Combine(gameDir, dll));
            bool inSxs = false;
            try
            {
                var sxs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS");
                var prefix = (is32BitGame ? "x86" : "amd64") + $"_microsoft.{ver}.crt_";
                inSxs = Directory.EnumerateDirectories(sxs, prefix + "*").Any();
            }
            catch { }
            report.Rows.Add(new DependencyRow { Dll = dll, Package = label, Present = inGame || inSxs, Where = inGame ? "game folder" : inSxs ? "WinSxS" : "", UsedByGame = true });
        }

        foreach (var dll in new[] { "msvcr71.dll", "msvcp71.dll", "msvcr70.dll" })
        {
            if (!referenced.Contains(dll)) continue;
            var (ok, where) = Locate(dll);
            report.Rows.Add(new DependencyRow { Dll = dll, Package = "Visual C++ .NET 2003 runtime (usually shipped in the game folder)", Present = ok, Where = where, UsedByGame = true });
        }

        if (referenced.Contains("openal32.dll"))
        {
            var (ok, where) = Locate("openal32.dll");
            report.Rows.Add(new DependencyRow { Dll = "openal32.dll", Package = "OpenAL (Creative) runtime", Present = ok, Where = where, UsedByGame = true });
        }

        if (referenced.Contains("dplayx.dll"))
            report.Advice.Add("The game references DirectPlay (dplayx.dll). If multiplayer or startup fails, enable it in " +
                              "'Turn Windows features on or off' > Legacy Components > DirectPlay (needs admin; Windows may offer this automatically).");

        var missingDx = report.Rows.Where(r => r.Package == DxPkg && !r.Present).ToList();
        if (missingDx.Any(r => r.UsedByGame))
            report.Advice.Add($"Install the official {DxPkg}: download directx_Jun2010_redist.exe from Microsoft, extract it to a folder, " +
                              "then run DXSETUP.exe from that folder (or use the 'Run DXSETUP.exe' button). It adds the legacy DLLs alongside modern DirectX and is safe on Windows 11.");
        else if (missingDx.Count > 0)
            report.Advice.Add($"{missingDx.Count} legacy DirectX DLLs are missing but this game does not appear to reference them. " +
                              "Installing the June 2010 runtime is still harmless and recommended if the game crashes on start (some references are hidden in packed/protected exes).");
        if (report.Rows.Any(r => r.Package.StartsWith("Visual C++ 200") && !r.Present))
            report.Advice.Add("Install the matching x86 Visual C++ Redistributable from Microsoft's 'Latest supported Visual C++ Redistributable downloads' page (older versions are listed further down that page).");
        if (report.Rows.Any(r => r.Dll == "openal32.dll" && !r.Present))
            report.Advice.Add("Install the OpenAL runtime (oalinst.exe) - it is usually on the game disc too.");
        return report;
    }
}
