namespace LegacyGameLauncher.Models;

/// <summary>Built-in or user-supplied description of a known game.</summary>
public sealed class GameProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Executable file names that identify the game (case-insensitive).</summary>
    public List<string> ExeNames { get; set; } = new();
    /// <summary>Known SHA-256 hashes of the game executable (exact match = highest confidence).</summary>
    public List<string> Sha256 { get; set; } = new();
    /// <summary>Substrings searched in the exe's ProductName / FileDescription version info.</summary>
    public List<string> ProductNameContains { get; set; } = new();
    /// <summary>Substrings searched in the install folder name (lowest confidence).</summary>
    public List<string> FolderNameContains { get; set; } = new();
    /// <summary>Folder names to look for under Documents / AppData / LocalAppData / VirtualStore.</summary>
    public List<string> ConfigFolderNames { get; set; } = new();
    /// <summary>Registry keys (e.g. HKLM\SOFTWARE\WOW6432Node\EA SPORTS\Rugby 08) to show in the config view.</summary>
    public List<string> RegistryKeys { get; set; } = new();
    public CompatSettings Recommended { get; set; } = new();
    public WrapperSettings RecommendedWrapper { get; set; } = new();
    public string InputApiHint { get; set; } = "";
    public string Notes { get; set; } = "";
    public string DrmNotes { get; set; } = "";
    /// <summary>Where the profile's facts come from (shown in the Overview).</summary>
    public List<string> Sources { get; set; } = new();
    public bool BuiltIn { get; set; }
}

public sealed class CompatSettings
{
    /// <summary>"", WIN7RTM, VISTASP2, WINXPSP3, WIN8RTM ...</summary>
    public string OsMode { get; set; } = "";
    /// <summary>DISABLEDXMAXIMIZEDWINDOWEDMODE (= "Disable fullscreen optimisations").</summary>
    public bool DisableFullscreenOptimizations { get; set; }
    public bool HighDpiAware { get; set; }
    public bool RunAsAdmin { get; set; }
    public bool ReducedColor16 { get; set; }
    public bool Res640x480 { get; set; }
    public bool DisableThemes { get; set; }
    /// <summary>0 = no limit, otherwise number of logical CPUs the game may use.</summary>
    public int CoreLimit { get; set; }
    /// <summary>Normal, AboveNormal, High, BelowNormal.</summary>
    public string Priority { get; set; } = "Normal";
    public string Arguments { get; set; } = "";
    /// <summary>
    /// SafeDisc loader mode: "Auto" (use loader behaviour when SafeDiscLoader2's version.dll is found next to the exe),
    /// "On" (always), "Off" (never). The launcher only cooperates with a loader the user installed; it never installs one.
    /// </summary>
    public string SafeDiscLoader { get; set; } = "Auto";
    /// <summary>When the SafeDisc loader is active, CPU limit and priority are skipped unless this is ticked.</summary>
    public bool CpuSettingsWithLoader { get; set; }
    /// <summary>Disc image (.iso) of the user's own game disc, mounted read-only before launch when MountDiscImage is on.</summary>
    public string DiscImagePath { get; set; } = "";
    public bool MountDiscImage { get; set; }
    /// <summary>Unmount after the game exits - only if the launcher mounted it (an image you mounted yourself is left alone).</summary>
    public bool UnmountAfterExit { get; set; } = true;

    public CompatSettings Clone() => (CompatSettings)MemberwiseClone();
}

public sealed class WrapperSettings
{
    /// <summary>None, dgVoodoo2, DXVK.</summary>
    public string Kind { get; set; } = "None";
    public string SourceFolder { get; set; } = "";
    public bool Installed { get; set; }
    public string InstalledKind { get; set; } = "";
    /// <summary>DLL names placed in the game folder (e.g. d3d8.dll, or d3d8.dll + d3d9.dll for DXVK).</summary>
    public List<string> InstalledDlls { get; set; } = new();
    public int ForcedWidth { get; set; }
    public int ForcedHeight { get; set; }
    public int RefreshCap { get; set; } = 60;
    public bool Windowed { get; set; }
    public bool Borderless { get; set; }
    public bool VSync { get; set; } = true;
    public int FpsLimit { get; set; }
}

public sealed class InputWrapperSettings
{
    /// <summary>x360ce (DirectInput pad -> XInput game) or dinputto8 (DirectInput 1-7 game -> DirectInput 8).</summary>
    public string Kind { get; set; } = "x360ce";
    public string SourceFolder { get; set; } = "";
    public bool Installed { get; set; }
    public string InstalledKind { get; set; } = "";
}

/// <summary>One reversible change the launcher made. Used for backup/restore and "Undo".</summary>
public sealed class ChangeRecord
{
    public DateTime When { get; set; } = DateTime.Now;
    /// <summary>compat, wrapper, input, config</summary>
    public string Group { get; set; } = "";
    /// <summary>Registry, FileAdded, FileReplaced</summary>
    public string Kind { get; set; } = "";
    public string Target { get; set; } = "";
    public string? RegistryName { get; set; }
    public bool PreviousExisted { get; set; }
    public string? PreviousValue { get; set; }
    public string? BackupPath { get; set; }
    public string Description { get; set; } = "";

    public override string ToString() => $"[{Group}] {When:yyyy-MM-dd HH:mm}  {Description}";
}

public sealed class CustomFileEntry
{
    public string RelativePath { get; set; } = "";
    public string Source { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public DateTime Added { get; set; } = DateTime.Now;
    public bool ReplacedOriginal { get; set; }
    public override string ToString() => $"{RelativePath}   ({Size / 1024.0:0} KB, SHA-256 {Sha256[..Math.Min(12, Sha256.Length)]}…){(ReplacedOriginal ? "  - original backed up" : "")}";
}

public sealed class LaunchRecord
{
    public DateTime Started { get; set; }
    public DateTime? Ended { get; set; }
    public long? ExitCode { get; set; }
    public string Summary { get; set; } = "";
    public string Report { get; set; } = "";
    public override string ToString() => $"{Started:yyyy-MM-dd HH:mm:ss}  {Summary}";
}

public sealed class GameEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ExePath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? ProfileId { get; set; }
    public string DetectionNote { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public CompatSettings Compat { get; set; } = new();
    public WrapperSettings Wrapper { get; set; } = new();
    public InputWrapperSettings Input { get; set; } = new();
    public List<ChangeRecord> Changes { get; set; } = new();
    public List<LaunchRecord> Launches { get; set; } = new();
    /// <summary>Files the user chose to copy into the game folder ("Your own files"), journaled under group "custom".</summary>
    public List<CustomFileEntry> CustomFiles { get; set; } = new();
    /// <summary>DS4Windows face-button layout for this game: Default, PlayStation, SwapCrossCircle.</summary>
    public string PadLayout { get; set; } = "Default";
    public string PadLayoutDs4wDir { get; set; } = "";

    public string GameDir => Path.GetDirectoryName(ExePath) ?? "";
    public override string ToString() => DisplayName;
}

public sealed class LibraryData
{
    public int Version { get; set; } = 1;
    public List<GameEntry> Games { get; set; } = new();
}
