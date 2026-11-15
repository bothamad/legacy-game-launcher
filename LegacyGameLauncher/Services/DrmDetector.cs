namespace LegacyGameLauncher.Services;

public sealed class DrmFinding
{
    public string Scheme { get; set; } = "";
    public string Evidence { get; set; } = "";
    public string Impact { get; set; } = "";
}

/// <summary>
/// DETECTION ONLY. Identifies copy-protection schemes so the user can be told clearly why a game might
/// not start. This class never modifies, removes or bypasses any protection.
/// </summary>
public static class DrmDetector
{
    const string SafeDiscImpact =
        "SafeDisc relies on the secdrv.sys driver, which Microsoft disabled on Windows 10 and 11 for security reasons. " +
        "A SafeDisc disc check cannot run on this PC. The only supported fixes are an official publisher patch that " +
        "removes the disc check, or an official digital/re-release of the game.";
    const string SecuRomImpact =
        "SecuROM disc checks often fail on Windows 10/11 (modern or virtual drives, missing driver behaviour). " +
        "Try: the original disc in a physical optical drive, closing virtual-drive software, and the publisher's latest official patch. " +
        "If it still fails, look for an official digital/re-release.";
    const string StarForceImpact =
        "StarForce installs kernel drivers that are not supported on Windows 10/11. Many publishers released official " +
        "patches removing StarForce - check the publisher's support site.";

    public const string ModifiedScheme = "Modified executable";
    const string ModifiedImpact =
        "This exe does not look like the original retail release: its copy protection has been removed. That is fine if it came " +
        "from an official publisher patch; if it came from a third-party 'no-CD' tool, this launcher cannot vouch for it and " +
        "will not help work around copy protection. Third-party modified exes from that era " +
        "are a common cause of crashes (and an unsigned tool from an unknown source can carry malware - scan it). " +
        "Supported options: reinstall from your original disc and apply only the publisher's official patches, or use an " +
        "official digital/DRM-free re-release if one exists. Note that the original SafeDisc exe itself cannot run on Windows 10/11.";

    // File names used by well-known 'no-CD' release groups / tools. Detection only.
    static readonly System.Text.RegularExpressions.Regex CrackToolName = new(
        @"^(rld|razor|rzr|reloaded|fairlight|flt|deviance|class)[-_ ].*\.(exe|dll)$|(no-?cd|nodvd|crack)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static List<DrmFinding> Scan(string exePath, PeInfo pe, string exeLowerText, Models.GameProfile? profile = null)
    {
        var findings = new List<DrmFinding>();
        void Add(string scheme, string evidence, string impact)
        {
            var existing = findings.FirstOrDefault(f => f.Scheme == scheme);
            if (existing != null) existing.Evidence += "; " + evidence;
            else findings.Add(new DrmFinding { Scheme = scheme, Evidence = evidence, Impact = impact });
        }

        foreach (var s in pe.SectionNames.Select(s => s.ToLowerInvariant()))
        {
            if (s is ".securom" or ".cms_t" or ".cms_d") Add("SecuROM", $"exe section '{s}'", SecuRomImpact);
            if (s.StartsWith("stxt")) Add("SafeDisc", $"exe section '{s}'", SafeDiscImpact);
            if (s.StartsWith(".sforce")) Add("StarForce", $"exe section '{s}'", StarForceImpact);
            if (s == ".bind") Add("Steam DRM stub", "exe section '.bind'", "Requires the Steam client to be running and signed in; normally works on Windows 11.");
        }

        if (exeLowerText.Contains("securom")) Add("SecuROM", "'SecuROM' string in exe", SecuRomImpact);
        if (exeLowerText.Contains("bog_ *90.0&!!")) Add("SafeDisc", "SafeDisc signature in exe", SafeDiscImpact);
        if (exeLowerText.Contains("protected by starforce"))Add("StarForce", "StarForce string in exe", StarForceImpact);

        var dir = Path.GetDirectoryName(exePath)!;
        var fileMarkers = new (string file, string scheme, string impact)[]
        {
            ("secdrv.sys", "SafeDisc", SafeDiscImpact), ("drvmgt.dll", "SafeDisc", SafeDiscImpact),
            ("dplayerx.dll", "SafeDisc", SafeDiscImpact), ("clcd32.dll", "SafeDisc", SafeDiscImpact),
            ("clokspl.exe", "SafeDisc", SafeDiscImpact), ("00000001.tmp", "SafeDisc", SafeDiscImpact),
            ("paul.dll", "SecuROM", SecuRomImpact), ("protect.dll", "StarForce", StarForceImpact),
            ("protect.exe", "StarForce", StarForceImpact),
        };
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var name = Path.GetFileName(f).ToLowerInvariant();
                foreach (var (file, scheme, impact) in fileMarkers)
                    if (name == file) Add(scheme, $"file '{Path.GetFileName(f)}' in game folder", impact);
                if (name.EndsWith(".icd")) Add("SafeDisc", $"file '{Path.GetFileName(f)}' (SafeDisc .icd main executable)", SafeDiscImpact);
                if (CrackToolName.IsMatch(name)) Add(ModifiedScheme, $"file '{Path.GetFileName(f)}' in game folder (name used by a 'no-CD' release group)", ModifiedImpact);
            }
        }
        catch { }

        // The profile says the retail exe is SafeDisc-wrapped, but this exe has no SafeDisc sections.
        bool profileSafeDisc = profile?.DrmNotes.Contains("SafeDisc", StringComparison.OrdinalIgnoreCase) == true;
        bool hasStxt = pe.SectionNames.Any(s => s.StartsWith("stxt", StringComparison.OrdinalIgnoreCase));
        if (profileSafeDisc && !hasStxt && pe.SectionNames.Count > 0)
            Add(ModifiedScheme, "the retail exe is SafeDisc-protected, but this exe has no SafeDisc (stxt*) sections" +
                (exeLowerText.Contains("bog_") ? " although SafeDisc marker text remains" : ""), ModifiedImpact);

        // A modified exe makes the SafeDisc finding from leftover strings misleading - report the modification instead.
        if (findings.Any(f => f.Scheme == ModifiedScheme) && !hasStxt)
            findings.RemoveAll(f => f.Scheme == "SafeDisc" && !f.Evidence.Contains("section"));
        return findings;
    }

    /// <summary>After a failed launch, is DRM a plausible cause?</summary>
    public static string? PostLaunchHint(List<DrmFinding> findings, TimeSpan runTime, long? exitCode)
    {
        if (findings.Count == 0) return null;
        if (runTime > TimeSpan.FromSeconds(45) && exitCode == 0) return null;
        if (findings.Any(f => f.Scheme == ModifiedScheme))
            return $"The game crashed after {runTime.TotalSeconds:0}s, and its exe has been modified by a third-party tool (see Overview). " +
                   "Crashes from such exes can't be fixed reliably by compatibility settings, and this launcher won't work around copy protection. " +
                   "Supported route: reinstall from the original disc with official patches only, or an official re-release.";
        var schemes = string.Join(", ", findings.Select(f => f.Scheme).Distinct());
        return $"The game exited after {runTime.TotalSeconds:0}s and copy protection ({schemes}) was detected. " +
               "A quick exit, a 'please insert the correct disc' message, or a silent exit is typical of a failed DRM check. " +
               "This launcher will not bypass DRM. Options: original disc in a physical drive; the publisher's official patch; " +
               "an official digital/DRM-free re-release (none is known for EA's Rugby 08 / Cricket 07 at the time of writing).";
    }
}
