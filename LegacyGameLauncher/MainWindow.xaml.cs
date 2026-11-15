using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using LegacyGameLauncher.Models;
using LegacyGameLauncher.Services;
using Microsoft.Win32;

namespace LegacyGameLauncher;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    readonly LibraryData _lib;
    GameEntry? _g;
    GameScan? _scan;
    List<DrmFinding> _drm = new();
    LoaderInfo _loader = new();
    DependencyReport? _deps;
    int _loadVersion;

    static readonly (string Label, string Value)[] OsLabels =
    {
        ("(none)", ""), ("Windows 7", "WIN7RTM"), ("Windows 8", "WIN8RTM"), ("Windows Vista SP2", "VISTASP2"), ("Windows Vista SP1", "VISTASP1"),
        ("Windows Vista", "VISTARTM"), ("Windows XP SP3", "WINXPSP3"), ("Windows XP SP2", "WINXPSP2"), ("Windows 98 / ME", "WIN98"), ("Windows 95", "WIN95"),
    };
    static readonly string[] Priorities = { "Normal", "AboveNormal", "High", "BelowNormal" };

    public MainWindow()
    {
        InitializeComponent();
        // Follow Windows' light/dark setting and accent colour, now and when it changes.
        Wpf.Ui.Appearance.ApplicationThemeManager.ApplySystemTheme();
        Loaded += (_, _) => Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        _lib = LibraryStore.Load();
        InitUpdates();
        foreach (var (label, _) in OsLabels) OsModeBox.Items.Add(label);
        CoreBox.Items.Add("No limit (all cores)");
        foreach (var n in new[] { 1, 2, 3, 4, 6, 8 }.Where(n => n < Environment.ProcessorCount)) CoreBox.Items.Add($"{n} core{(n > 1 ? "s" : "")}");
        foreach (var p in Priorities) PriorityBox.Items.Add(p);
        foreach (var s in new[] { "(leave as is)", "Windowed", "Fullscreen" }) CfgWindowed.Items.Add(s);
        CfgWindowed.SelectedIndex = 0;
        AffinityHint.Text = $"This PC has {Environment.ProcessorCount} logical processors. The game is started suspended, limited, then resumed - so it never sees the extra cores. " +
                            "When possible one logical CPU per physical core is used (even-numbered CPUs).";
        ProfileInfo.Text = $"{ProfileCatalog.Profiles.Count} profiles loaded: {string.Join(", ", ProfileCatalog.Profiles.Select(p => p.Name))}" +
                           (ProfileCatalog.LoadErrors.Count > 0 ? "\nProfile errors: " + string.Join("; ", ProfileCatalog.LoadErrors) : "");
        RefreshList();
        if (_lib.Games.Count > 0) GameList.SelectedIndex = 0;
    }

    GameProfile? Profile => ProfileCatalog.Get(_g?.ProfileId);
    bool Game32 => _scan?.Exe.Is32Bit ?? true;
    string GfxApi => _scan == null ? "d3d9" : WrapperManager.GraphicsApi(_scan, out _);

    void Save() { try { LibraryStore.Save(_lib); } catch (Exception ex) { MessageBox.Show(this, "Could not save library: " + ex.Message); } }

    void RefreshList()
    {
        var sel = _g;
        GameList.ItemsSource = null;
        GameList.ItemsSource = _lib.Games;
        if (sel != null) GameList.SelectedItem = sel;
        EmptyState.Visibility = _lib.Games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Tabs.Visibility = _lib.Games.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    static void Open(string target) => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

    // ------------------------------------------------------------------ 1. Library

    void AddGame_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Game executable (*.exe)|*.exe", Title = "Select the game's .exe" };
        if (dlg.ShowDialog(this) != true) return;
        var exe = dlg.FileName;
        var existing = _lib.Games.FirstOrDefault(g => g.ExePath.Equals(exe, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { GameList.SelectedItem = existing; return; }

        var pe = PeReader.Read(exe);
        if (!pe.IsValid) { MessageBox.Show(this, "That file is not a Windows executable: " + pe.Error); return; }
        string sha;
        try { sha = GameDetector.Sha256Of(exe); } catch (Exception ex) { MessageBox.Show(this, "Cannot read the exe: " + ex.Message); return; }
        var (profile, note) = GameDetector.Detect(exe, sha);
        var g = new GameEntry
        {
            ExePath = exe, Sha256 = sha, ProfileId = profile?.Id, DetectionNote = note,
            DisplayName = profile?.Name ?? FileVersionName(exe) ?? Path.GetFileNameWithoutExtension(exe),
        };
        if (profile != null)
        {
            // Pre-fill (not applied until you press Save & apply / Launch).
            g.Compat = profile.Recommended.Clone();
            CopyWrapperNumbers(profile.RecommendedWrapper, g.Wrapper);
        }
        _lib.Games.Add(g);
        Save();
        _g = g;
        RefreshList();
        GameList.SelectedItem = g;
    }

    static string? FileVersionName(string exe)
    {
        try { var v = FileVersionInfo.GetVersionInfo(exe); return string.IsNullOrWhiteSpace(v.ProductName) ? null : v.ProductName.Trim(); } catch { return null; }
    }

    static void CopyWrapperNumbers(WrapperSettings from, WrapperSettings to)
    {
        to.RefreshCap = from.RefreshCap; to.VSync = from.VSync; to.FpsLimit = from.FpsLimit;
        to.Windowed = from.Windowed; to.Borderless = from.Borderless;
        to.ForcedWidth = from.ForcedWidth; to.ForcedHeight = from.ForcedHeight;
        if (!to.Installed && from.Kind != "None") to.Kind = from.Kind;
    }

    async void RemoveGame_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        if (_g.Changes.Count > 0)
        {
            var r = MessageBox.Show(this, $"The launcher still has {_g.Changes.Count} active change(s) for {_g.DisplayName} (compat layers / wrapper files / config edits).\n\n" +
                                          "Yes = undo them all first, then remove.\nNo = remove from the library but leave the changes in place.\nCancel = keep the game.",
                                    "Remove game", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes)
            {
                var (ok, log) = await ChangeJournal.UndoAsync(_g, null, this);
                if (!ok) { MessageBox.Show(this, "Undo failed - game kept in the library.\n\n" + log); Save(); return; }
            }
        }
        _lib.Games.Remove(_g);
        _g = null;
        Save();
        RefreshList();
        if (_lib.Games.Count > 0) GameList.SelectedIndex = 0;
    }

    void OpenProfiles_Click(object sender, RoutedEventArgs e) => Open(AppPaths.Profiles);

    async void GameList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry g) return;
        _g = g;
        int version = ++_loadVersion;
        LoadCompatUi(); LoadWrapperUi(); LoadPadUi(); LoadHistory(); LoadChanges(); LoadLayoutUi();
        GameTitle.Text = g.DisplayName;
        OverviewText.Text = "Inspecting game files…";
        DrmText.Text = ""; DepGrid.ItemsSource = null; DepAdvice.Text = "";
        ConfigGrid.ItemsSource = null; RegistryGrid.ItemsSource = null; ConfigEditor.Text = ""; ConfigDetected.Text = "";
        PadGrid.ItemsSource = null; PadAdvice.Text = "";
        LaunchLog.Text = "";

        if (!File.Exists(g.ExePath))
        {
            OverviewText.Text = $"The exe no longer exists:\n{g.ExePath}\n\nRemove the game and add it again from its new location.";
            return;
        }
        try
        {
            var exe = g.ExePath;
            var prof = Profile;
            var (scan, drm, loader) = await Task.Run(() => { var s = GameScan.Run(exe); return (s, DrmDetector.Scan(exe, s.Exe, s.ExeLowerText, prof), SafeDiscLoaderSupport.Detect(exe)); });
            if (version != _loadVersion) return;
            _scan = scan; _drm = drm; _loader = loader;
            UpdateLoaderHint();
            RunDependencyCheck();
            RenderOverview();
            FixLog.Clear();
            UpdateFixStatus();
            UpdateCustomList();
            UpdateWrapperSourceInfo();
        }
        catch (Exception ex) { OverviewText.Text = "Could not inspect the exe: " + ex.Message; }
    }

    void RenderOverview()
    {
        if (_g == null || _scan == null) return;
        var p = Profile;
        var sb = new StringBuilder();
        sb.AppendLine($"Executable : {_g.ExePath}");
        sb.AppendLine($"Architecture: {_scan.Exe.ArchName}{(_scan.Exe.Is32Bit ? "  - wrappers will use x86 DLLs; system DLLs come from SysWOW64" : "")}");
        sb.AppendLine($"SHA-256    : {_g.Sha256}");
        sb.AppendLine($"Profile    : {(p?.Name ?? "none")} - {_g.DetectionNote}");
        try
        {
            var current = GameDetector.Sha256Of(_g.ExePath);
            if (!current.Equals(_g.Sha256, StringComparison.OrdinalIgnoreCase))
                sb.AppendLine($"NOTE: the exe changed since it was added (patched?). New SHA-256: {current}");
        }
        catch { }
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (_g.ExePath.StartsWith(pf, StringComparison.OrdinalIgnoreCase) || _g.ExePath.StartsWith(pf64, StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("Location   : under Program Files - installing wrappers/editing files there will ask for admin; the game's own writes may be redirected to VirtualStore.");
        if (_scan.Exe.ManifestRequiresAdmin) sb.AppendLine("Manifest   : the exe itself requests administrator rights (launch will ask for UAC).");
        sb.AppendLine($"Imports    : {string.Join(", ", _scan.Exe.Imports.DefaultIfEmpty("(none readable - exe may be packed/protected)"))}");
        sb.AppendLine($"Compat (HKCU): {CompatLayers.ReadHkcu(_g.ExePath) ?? "(none)"}");
        var hklm = CompatLayers.ReadHklm(_g.ExePath);
        if (hklm != null) sb.AppendLine($"Compat (HKLM, all users - set by Windows/installer): {hklm}");
        sb.AppendLine($"Wrapper    : {(_g.Wrapper.Installed ? _g.Wrapper.InstalledKind + " installed" : "none installed")}");
        sb.AppendLine($"Graphics   : Direct3D {(GfxApi == "d3d8" ? "8" : "9")}{(WrapperManager.GraphicsApi(_scan, out var sure) != "" && !sure ? " (not certain - no unambiguous d3d8/d3d9 reference found)" : "")}");
        sb.AppendLine($"Input API  : {ControllerService.GameInputApi(_scan.Referenced, p, out _)}");
        if (p != null)
        {
            sb.AppendLine(); sb.AppendLine(p.Notes);
            if (p.Sources.Count > 0) { sb.AppendLine(); sb.AppendLine("Profile sources:"); foreach (var s in p.Sources) sb.AppendLine("  " + s); }
        }
        OverviewText.Text = sb.ToString();

        DrmText.Text = _drm.Count == 0
            ? "No known copy-protection markers found in the exe or game folder."
            : string.Join("\n\n", _drm.Select(d => $"{d.Scheme}  (evidence: {d.Evidence})\n{d.Impact}")) + (p != null && p.DrmNotes.Length > 0 ? "\n\n" + p.DrmNotes : "");
        if (_loader.Detected)
            DrmText.Text = $"SafeDiscLoader2 found ({_loader.Evidence}).\nLauncher mode: {_g.Compat.SafeDiscLoader} → " +
                           $"{(SafeDiscLoaderSupport.IsActive(_g.Compat, _loader) ? "loader-friendly launch (no compatibility layers, game-folder working directory, no CPU/priority changes unless you opt in)" : "normal launch (loader mode is Off)")}." +
                           "\nThe launcher only cooperates with this loader; it does not install, configure or change it.\n\n" + DrmText.Text;
    }

    void UpdateLoaderHint()
    {
        if (_g == null || LoaderHint == null) return;
        var active = SafeDiscLoaderSupport.IsActive(_g.Compat, _loader);
        LoaderHint.Text = (_loader.Detected ? $"Detected: {_loader.Evidence}." : "Not detected (no version.dll with loader markers or version.json next to the exe).") +
                          (active ? "\nActive: compatibility layers are removed for this exe at launch and CPU limit/priority are skipped unless ticked below." : "");
    }

    // ------------------------------------------------------------------ 2. Dependencies

    void RunDependencyCheck()
    {
        if (_g == null || _scan == null) return;
        _deps = DependencyChecker.Check(_g.GameDir, Game32, _scan.Referenced);
        ShowDeps();
    }

    void ShowDeps()
    {
        if (_deps == null) return;
        DepGrid.ItemsSource = ShowAllDeps.IsChecked == true ? _deps.Rows : _deps.Rows.Where(r => r.UsedByGame || !r.Present).OrderByDescending(r => r.UsedByGame && !r.Present).ToList();
        var used = _deps.Rows.Count(r => r.UsedByGame);
        var head = _deps.HasBlockingIssue ? "⚠ Missing DLLs this game needs." : $"✓ All {used} legacy runtime DLLs this game references are present.";
        DepAdvice.Text = head + (_deps.Advice.Count > 0 ? "\n" + string.Join("\n", _deps.Advice.Select(a => "• " + a)) : "") +
                         $"\n(Scanned: {string.Join(", ", _scan!.ScannedFiles.Take(12))}{(_scan.ScannedFiles.Count > 12 ? ", …" : "")})";
    }

    void ShowAllDeps_Changed(object sender, RoutedEventArgs e) => ShowDeps();
    void RecheckDeps_Click(object sender, RoutedEventArgs e) => RunDependencyCheck();
    void OpenDxPage_Click(object sender, RoutedEventArgs e) => Open(DependencyChecker.DirectXJune2010Url);
    void OpenVcPage_Click(object sender, RoutedEventArgs e) => Open(DependencyChecker.VcRedistUrl);

    async void RunDxSetup_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(this, "1. Download directx_Jun2010_redist.exe from Microsoft (the 'Open DirectX June 2010 download' button).\n" +
                              "2. Run it and extract to an empty folder.\n3. Select DXSETUP.exe from that folder in the next dialog.\n\n" +
                              "Windows will ask for admin rights - DXSETUP installs into System32/SysWOW64.", "Install DirectX June 2010 runtime");
        var dlg = new OpenFileDialog { Filter = "DXSETUP.exe|DXSETUP.exe", Title = "Select DXSETUP.exe from the extracted June 2010 redistributable" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
#pragma warning disable SYSLIB0057
            var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(dlg.FileName);
#pragma warning restore SYSLIB0057
            if (!cert.Subject.Contains("Microsoft Corporation"))
            {
                MessageBox.Show(this, "That file is not signed by Microsoft - not running it.\nSigner: " + cert.Subject); return;
            }
        }
        catch { MessageBox.Show(this, "That file has no valid digital signature - not running it."); return; }
        try
        {
            using var p = Process.Start(new ProcessStartInfo(dlg.FileName) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(dlg.FileName)! });
            if (p != null) await p.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { return; }
        RunDependencyCheck();
    }
}
