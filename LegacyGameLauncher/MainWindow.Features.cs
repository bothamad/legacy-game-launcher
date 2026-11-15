using System.Text;
using System.Windows;
using System.Windows.Controls;
using LegacyGameLauncher.Models;
using LegacyGameLauncher.Services;
using Microsoft.Win32;

namespace LegacyGameLauncher;

public partial class MainWindow
{
    // ------------------------------------------------------------------ 3. Compatibility

    void LoadCompatUi()
    {
        if (_g == null) return;
        var c = _g.Compat;
        OsModeBox.SelectedIndex = Math.Max(0, Array.FindIndex(OsLabels, o => o.Value == c.OsMode));
        FsoBox.IsChecked = c.DisableFullscreenOptimizations; DpiBox.IsChecked = c.HighDpiAware; AdminBox.IsChecked = c.RunAsAdmin;
        Color16Box.IsChecked = c.ReducedColor16; Res640Box.IsChecked = c.Res640x480; ThemesBox.IsChecked = c.DisableThemes;
        var coreIdx = c.CoreLimit <= 0 ? 0 : CoreBox.Items.IndexOf($"{c.CoreLimit} core{(c.CoreLimit > 1 ? "s" : "")}");
        CoreBox.SelectedIndex = Math.Max(0, coreIdx);
        PriorityBox.SelectedItem = Priorities.Contains(c.Priority) ? c.Priority : "Normal";
        ArgsBox.Text = c.Arguments;
        _loadingLoaderUi = true;
        LoaderBox.SelectedIndex = c.SafeDiscLoader switch { "On" => 1, "Off" => 2, _ => 0 };
        _loadingLoaderUi = false;
        LoaderCpuBox.IsChecked = c.CpuSettingsWithLoader;
        UpdateLoaderHint();
        IsoBox.Text = c.DiscImagePath; IsoMountBox.IsChecked = c.MountDiscImage; IsoUnmountBox.IsChecked = c.UnmountAfterExit;
        UpdateIsoStatus();
        CompatStatus.Text = $"Current HKCU value: {CompatLayers.ReadHkcu(_g.ExePath) ?? "(none)"}";
    }

    bool _loadingLoaderUi;

    void LoaderBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_g == null || _loadingLoaderUi) return;
        _g.Compat.SafeDiscLoader = LoaderMode();
        Save(); UpdateLoaderHint(); RenderOverview();
    }

    string LoaderMode() => LoaderBox.SelectedIndex switch { 1 => "On", 2 => "Off", _ => "Auto" };

    bool LoaderActive(GameEntry g) => SafeDiscLoaderSupport.IsActive(g.Compat, _loader);

    void ReadCompatUi()
    {
        if (_g == null) return;
        var c = _g.Compat;
        c.OsMode = OsLabels[Math.Max(0, OsModeBox.SelectedIndex)].Value;
        c.DisableFullscreenOptimizations = FsoBox.IsChecked == true; c.HighDpiAware = DpiBox.IsChecked == true; c.RunAsAdmin = AdminBox.IsChecked == true;
        c.ReducedColor16 = Color16Box.IsChecked == true; c.Res640x480 = Res640Box.IsChecked == true; c.DisableThemes = ThemesBox.IsChecked == true;
        var core = CoreBox.SelectedItem as string ?? "";
        c.CoreLimit = int.TryParse(core.Split(' ')[0], out var n) ? n : 0;
        c.Priority = PriorityBox.SelectedItem as string ?? "Normal";
        c.Arguments = ArgsBox.Text.Trim();
        c.SafeDiscLoader = LoaderMode();
        c.CpuSettingsWithLoader = LoaderCpuBox.IsChecked == true;
        ReadIsoUi();
    }

    void ReadIsoUi()
    {
        if (_g == null) return;
        _g.Compat.DiscImagePath = IsoBox.Text.Trim().Trim('"');
        _g.Compat.MountDiscImage = IsoMountBox.IsChecked == true;
        _g.Compat.UnmountAfterExit = IsoUnmountBox.IsChecked == true;
    }

    void UpdateIsoStatus()
    {
        if (_g == null) return;
        var iso = _g.Compat.DiscImagePath;
        if (string.IsNullOrWhiteSpace(iso)) { IsoStatus.Text = "No disc image set. Only needed if the game (or your loader setup) wants the disc in a drive."; return; }
        if (!File.Exists(iso)) { IsoStatus.Text = "✗ File not found."; return; }
        var d = DiscImage.MountedDrive(iso);
        IsoStatus.Text = d != null ? $"Mounted as {d} ({SafeLabel(d)})." : "Not mounted.";
    }

    static string SafeLabel(string drive) { try { return new DriveInfo(drive).VolumeLabel; } catch { return "?"; } }

    void IsoOption_Click(object sender, RoutedEventArgs e) { ReadIsoUi(); Save(); }
    void IsoBox_LostFocus(object sender, RoutedEventArgs e) { ReadIsoUi(); Save(); UpdateIsoStatus(); }

    void IsoBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        var dlg = new OpenFileDialog { Title = "Select the disc image of your game disc", Filter = "Disc images (*.iso)|*.iso|All files (*.*)|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        IsoBox.Text = dlg.FileName;
        if (IsoMountBox.IsChecked != true) IsoMountBox.IsChecked = true;
        ReadIsoUi(); Save(); UpdateIsoStatus();
    }

    async void IsoMount_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        ReadIsoUi(); Save();
        var iso = _g.Compat.DiscImagePath;
        try
        {
            IsoStatus.Text = "Mounting…";
            var (drive, now) = await Task.Run(() => DiscImage.Mount(iso));
            IsoStatus.Text = now ? $"✓ Mounted as {drive} ({SafeLabel(drive)})." : $"Already mounted as {drive}.";
        }
        catch (Exception ex) { IsoStatus.Text = "✗ " + ex.Message; }
    }

    async void IsoUnmount_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        ReadIsoUi();
        var iso = _g.Compat.DiscImagePath;
        try { await Task.Run(() => DiscImage.Unmount(iso)); IsoStatus.Text = "✓ Unmounted."; }
        catch (Exception ex) { IsoStatus.Text = "✗ " + ex.Message; }
    }

    /// <summary>Mounts the game's disc image before launch if configured. Returns the image path if the launcher mounted it now.</summary>
    async Task<string?> MountForLaunch(GameEntry g)
    {
        var iso = g.Compat.DiscImagePath;
        if (!g.Compat.MountDiscImage || string.IsNullOrWhiteSpace(iso)) return null;
        try
        {
            var (drive, now) = await Task.Run(() => DiscImage.Mount(iso));
            Log(now ? $"Disc image mounted as {drive} ({SafeLabel(drive)}): {iso}" : $"Disc image already mounted as {drive} ({SafeLabel(drive)}) - left as it is.");
            return now ? iso : null;
        }
        catch (Exception ex)
        {
            Log($"WARNING: could not mount the disc image ({ex.Message}). Launching anyway.");
            return null;
        }
    }

    void CompatRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        if (Profile == null) { CompatStatus.Text = "No profile for this game - generic suggestion loaded: Disable fullscreen optimisations + High DPI aware + 4 cores."; _g.Compat = new CompatSettings { DisableFullscreenOptimizations = true, HighDpiAware = true, CoreLimit = 4 }; }
        else
        {
            var old = _g.Compat;
            _g.Compat = Profile.Recommended.Clone();
            // Per-machine choices aren't part of a profile's recommendation - keep them.
            _g.Compat.SafeDiscLoader = old.SafeDiscLoader; _g.Compat.CpuSettingsWithLoader = old.CpuSettingsWithLoader;
            _g.Compat.DiscImagePath = old.DiscImagePath; _g.Compat.MountDiscImage = old.MountDiscImage; _g.Compat.UnmountAfterExit = old.UnmountAfterExit;
        }
        LoadCompatUi();
        if (LoaderActive(_g)) CompatStatus.Text += "\nSafeDisc loader is active: the layers below won't be written and the CPU limit/priority won't be applied when you launch (tick the option to apply CPU settings anyway).";
        CompatStatus.Text += "\nRecommended values loaded - press 'Save & apply' to write them.";
    }

    void CompatApply_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        ReadCompatUi();
        try
        {
            if (LoaderActive(_g))
            {
                var removed = CompatLayers.Clear(_g);
                Save(); LoadChanges(); RenderOverview(); UpdateLoaderHint();
                CompatStatus.Text = "Saved. SafeDisc loader is active, so no compatibility layers are written for this exe" +
                                    (removed != null ? $" (removed \"{removed}\"; Undo restores the original)." : ".") +
                                    "\nSet the loader option to Off if you really want layers with this game.";
                return;
            }
            var v = CompatLayers.Apply(_g);
            Save();
            CompatStatus.Text = $"Saved.\nHKCU\\{CompatLayers.SubKey}\n  \"{_g.ExePath}\" = {v ?? "(removed)"}\n" +
                                $"CPU limit: {(_g.Compat.CoreLimit == 0 ? "none" : _g.Compat.CoreLimit + $" cores (mask 0x{CompatLayers.AffinityMask(_g.Compat.CoreLimit):X})")}, priority: {_g.Compat.Priority} - applied when you launch from this app." +
                                (_g.Compat.RunAsAdmin ? "\nRUNASADMIN: Windows will ask for admin each time the game starts (also when started outside this launcher)." : "");
            LoadChanges(); RenderOverview();
        }
        catch (Exception ex) { CompatStatus.Text = "Failed: " + ex.Message; }
    }

    async void CompatUndo_Click(object sender, RoutedEventArgs e) => await UndoGroup("compat", CompatStatus);

    // ------------------------------------------------------------------ 4. Graphics wrapper

    void LoadWrapperUi()
    {
        if (_g == null) return;
        var w = _g.Wrapper;
        (w.Kind switch { "dgVoodoo2" => WrapDg, "DXVK" => WrapDxvk, _ => WrapNone }).IsChecked = true;
        WrapFolder.Text = w.SourceFolder;
        WrapW.Text = w.ForcedWidth.ToString(); WrapH.Text = w.ForcedHeight.ToString();
        WrapHz.Text = w.RefreshCap.ToString(); WrapFps.Text = w.FpsLimit.ToString();
        WrapWindowed.IsChecked = w.Windowed; WrapBorderless.IsChecked = w.Borderless; WrapVsync.IsChecked = w.VSync;
        WrapStatus.Text = w.Installed ? $"{w.InstalledKind} is installed in {_g.GameDir}." : "No wrapper installed.";
    }

    void ReadWrapperUi()
    {
        if (_g == null) return;
        var w = _g.Wrapper;
        w.Kind = WrapDg.IsChecked == true ? "dgVoodoo2" : WrapDxvk.IsChecked == true ? "DXVK" : "None";
        w.SourceFolder = WrapFolder.Text.Trim();
        w.ForcedWidth = Int(WrapW.Text); w.ForcedHeight = Int(WrapH.Text);
        w.RefreshCap = Int(WrapHz.Text); w.FpsLimit = Int(WrapFps.Text);
        w.Windowed = WrapWindowed.IsChecked == true; w.Borderless = WrapBorderless.IsChecked == true; w.VSync = WrapVsync.IsChecked == true;
    }

    static int Int(string s) => int.TryParse(s.Trim(), out var v) && v > 0 ? v : 0;

    void WrapKind_Changed(object sender, RoutedEventArgs e) => UpdateWrapperSourceInfo();

    void UpdateWrapperSourceInfo()
    {
        if (_g == null || WrapSourceInfo == null) return;
        var kind = WrapDg.IsChecked == true ? "dgVoodoo2" : WrapDxvk.IsChecked == true ? "DXVK" : "None";
        if (kind == "None" || string.IsNullOrWhiteSpace(WrapFolder.Text)) { WrapSourceInfo.Text = kind == "None" ? "" : "Select the folder you extracted the download into."; return; }
        try
        {
            var src = WrapperManager.Locate(kind, WrapFolder.Text.Trim(), Game32, GfxApi);
            WrapSourceInfo.Text = $"✓ Will use {src.Description}" + (kind == "dgVoodoo2" ? (src.ConfigTemplate != null ? $", config template {Path.GetFileName(src.ConfigTemplate)}" : " - no dgVoodoo.conf template found (defaults will be used)") : "");
        }
        catch (Exception ex) { WrapSourceInfo.Text = "✗ " + ex.Message; }
    }

    void WrapBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the extracted dgVoodoo2 / DXVK folder" };
        if (dlg.ShowDialog(this) != true) return;
        WrapFolder.Text = dlg.FolderName;
        UpdateWrapperSourceInfo();
    }

    void WrapDesktopRes_Click(object sender, RoutedEventArgs e)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        WrapW.Text = ((int)Math.Round(SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX)).ToString();
        WrapH.Text = ((int)Math.Round(SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY)).ToString();
    }

    void WrapRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        ReadWrapperUi();
        CopyWrapperNumbers(Profile?.RecommendedWrapper ?? new WrapperSettings { Kind = "dgVoodoo2", RefreshCap = 60, VSync = true, FpsLimit = 60 }, _g.Wrapper);
        LoadWrapperUi();
        WrapStatus.Text += "\nRecommended settings loaded. Tip: press 'Use desktop' to force your native resolution - required for the 60 Hz cap to apply with dgVoodoo2.";
    }

    async void WrapInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        ReadWrapperUi();
        if (_g.Wrapper.Kind == "None") { WrapStatus.Text = "Choose dgVoodoo2 or DXVK first."; return; }
        try
        {
            WrapStatus.Text = "Installing…";
            var (ok, log) = await WrapperManager.InstallAsync(_g, Game32, GfxApi, this);
            WrapStatus.Text = (ok ? "✓ " : "✗ ") + log;
        }
        catch (Exception ex) { WrapStatus.Text = "✗ " + ex.Message; }
        Save(); LoadChanges(); RenderOverview();
    }

    async void WrapRemove_Click(object sender, RoutedEventArgs e) => await UndoGroup("wrapper", WrapStatus);

    // ------------------------------------------------------------------ 5. Game config files

    ConfigCandidate? _cfg;
    Encoding _cfgEncoding = Encoding.UTF8;

    async void ConfigScan_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        var g = _g; var p = Profile;
        ConfigDetected.Text = "Scanning…";
        var (files, reg) = await Task.Run(() => (ConfigScanner.Scan(g, p), ConfigScanner.ScanRegistry(p, Path.GetFileName(g.GameDir))));
        ConfigGrid.ItemsSource = files;
        RegistryGrid.ItemsSource = reg;
        ConfigDetected.Text = $"Found {files.Count} candidate file(s) and {reg.Count} registry value(s). Select a file to view it; settings that look like resolution/windowed keys are listed here.";
    }

    void ShowRegistry_Changed(object sender, RoutedEventArgs e)
    {
        bool reg = ShowRegistry.IsChecked == true;
        RegistryGrid.Visibility = reg ? Visibility.Visible : Visibility.Collapsed;
        ConfigGrid.Visibility = reg ? Visibility.Collapsed : Visibility.Visible;
    }

    void ConfigGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _cfg = ConfigGrid.SelectedItem as ConfigCandidate;
        if (_cfg == null) return;
        try
        {
            if (_cfg.IsText)
            {
                var (text, enc) = ConfigScanner.ReadText(_cfg.Path);
                _cfgEncoding = enc;
                ConfigEditor.IsReadOnly = false;
                ConfigEditor.Text = text;
                var found = ConfigScanner.DetectSettings(text);
                ConfigDetected.Text = found.Count == 0
                    ? $"{_cfg.Path}\nNo resolution/windowed-looking keys detected in this file. You can still edit it by hand and Save."
                    : $"{_cfg.Path}\nDetected: " + string.Join("   ", found.Take(12).Select(f => $"L{f.Line} {f.Key} = {f.Value}"));
            }
            else
            {
                ConfigEditor.IsReadOnly = true;
                ConfigEditor.Text = "Binary file - shown read-only (first 1 KB):\n\n" + ConfigScanner.HexPreview(_cfg.Path);
                _resMatches = ConfigScanner.FindResolutionPairs(File.ReadAllBytes(_cfg.Path));
                ConfigDetected.Text = $"{_cfg.Path}\n" + (_resMatches.Count switch
                {
                    0 => "Binary file. No stored resolution pair found. For EA .pro profiles: set 640x480 in the game's options, quit, Scan again, then use Quick set.",
                    1 => $"Binary profile: resolution found at {_resMatches[0]}. Enter a new width/height and press 'Apply to detected keys' (writes the file, with backup).",
                    _ => $"Binary file: {_resMatches.Count} possible resolution values ({string.Join("; ", _resMatches.Take(6))}) - ambiguous, so the launcher won't edit it. " +
                         "Set an unusual resolution in-game (e.g. 640x480), quit, and Scan again.",
                });
            }
        }
        catch (Exception ex) { ConfigEditor.Text = "Cannot read: " + ex.Message; ConfigEditor.IsReadOnly = true; }
    }

    List<ConfigScanner.ResolutionMatch> _resMatches = new();

    async Task PatchBinaryResolution()
    {
        if (_g == null || _cfg == null) return;
        int w = Int(CfgW.Text), h = Int(CfgH.Text);
        if (w == 0 || h == 0) { ConfigDetected.Text = "Enter both width and height."; return; }
        var bytes = File.ReadAllBytes(_cfg.Path);
        var matches = ConfigScanner.FindResolutionPairs(bytes);
        if (matches.Count != 1) { ConfigDetected.Text = $"✗ Need exactly one stored resolution to edit safely; found {matches.Count}. Nothing changed."; return; }
        var m = matches[0];
        if (MessageBox.Show(this, $"Change the resolution stored in\n{_cfg.Path}\nfrom {m.Width}x{m.Height} to {w}x{h} (4 bytes at offset 0x{m.Offset:X})?\n\n" +
                                  "The original file is backed up first. Changing display options in the game afterwards may reset it.",
                            "Edit binary profile", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var staged = Path.Combine(AppPaths.Staging, $"{Guid.NewGuid():N}-{_cfg.Name}");
        File.WriteAllBytes(staged, ConfigScanner.WithResolution(bytes, m.Offset, w, h));
        var ops = new List<FileOp>();
        var rec = ChangeJournal.PlanPlaceFile(_g, "config", staged, _cfg.Path, ops, $"Resolution {m.Width}x{m.Height} -> {w}x{h} in {_cfg.Name}");
        var (ok, err) = await FileOpRunner.RunAsync(ops, $"Edit the resolution in {_cfg.Path}", this);
        if (!ok) { ConfigDetected.Text = "✗ " + err; return; }
        if (rec != null) _g.Changes.Add(rec);
        Save(); LoadChanges();
        ConfigEditor.Text = "Binary file - shown read-only (first 1 KB):\n\n" + ConfigScanner.HexPreview(_cfg.Path);
        ConfigDetected.Text = $"✓ Resolution set to {w}x{h} in {_cfg.Name} (original backed up; 'Undo config edits' restores it). " +
                              "If the game can't display this mode, combine it with dgVoodoo2 or undo.";
    }

    async void ConfigQuickSet_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg != null && !_cfg.IsText) { await PatchBinaryResolution(); return; }
        if (_cfg == null || ConfigEditor.IsReadOnly) { ConfigDetected.Text = "Select a config file first."; return; }
        bool? windowed = CfgWindowed.SelectedIndex switch { 1 => true, 2 => false, _ => null };
        var (text, changes) = ConfigScanner.ApplyQuickSettings(ConfigEditor.Text, Int(CfgW.Text), Int(CfgH.Text), windowed);
        ConfigEditor.Text = text;
        ConfigDetected.Text = changes.Count == 0
            ? "No matching keys were changed (nothing recognised, or values already equal). Edit by hand if you know the right key."
            : "Changed in the editor (not saved yet): " + string.Join("; ", changes) + "  → press 'Save file'.";
    }

    async void ConfigSave_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null || _cfg == null || ConfigEditor.IsReadOnly) return;
        try
        {
            var staged = Path.Combine(AppPaths.Staging, $"{Guid.NewGuid():N}-{_cfg.Name}");
            File.WriteAllText(staged, ConfigEditor.Text, _cfgEncoding);
            var ops = new List<FileOp>();
            var rec = ChangeJournal.PlanPlaceFile(_g, "config", staged, _cfg.Path, ops, $"Edited {_cfg.Name}");
            var (ok, err) = await FileOpRunner.RunAsync(ops, $"Save your edits to {_cfg.Path}", this);
            if (!ok) { ConfigDetected.Text = "✗ Save failed: " + err; return; }
            if (rec != null) _g.Changes.Add(rec);
            Save(); LoadChanges();
            ConfigDetected.Text = $"✓ Saved {_cfg.Path} (the original is backed up; 'Undo config edits' restores it).";
        }
        catch (Exception ex) { ConfigDetected.Text = "✗ " + ex.Message; }
    }

    async void ConfigUndo_Click(object sender, RoutedEventArgs e) => await UndoGroup("config", null);

    void ConfigOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_cfg != null) Open(Path.GetDirectoryName(_cfg.Path)!);
        else if (_g != null) Open(_g.GameDir);
    }

    // ------------------------------------------------------------------ 6. Controllers

    void LoadPadUi()
    {
        if (_g == null) return;
        PadFolder.Text = _g.Input.SourceFolder;
        (_g.Input.Kind == "dinputto8" ? PadDinput : PadX360).IsChecked = true;
        PadStatus.Text = _g.Input.Installed ? $"{(_g.Input.InstalledKind == "" ? "x360ce" : _g.Input.InstalledKind)} is installed in the game folder." : "No input wrapper installed.";
    }

    void DetectPads_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null || _scan == null) return;
        var pads = ControllerService.Detect();
        PadGrid.ItemsSource = pads;
        var api = ControllerService.GameInputApi(_scan.Referenced, Profile, out var xdll);
        PadAdvice.Text = $"This game expects: {api}\n\n{ControllerService.Advice(api, pads)}" +
                         (api.StartsWith("XInput") ? $"\n\n(The game loads {xdll}.)" : "");
    }

    void PadBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = PadDinput.IsChecked == true ? "Select the extracted dinputto8 folder" : "Select the extracted x360ce folder" };
        if (dlg.ShowDialog(this) == true) PadFolder.Text = dlg.FolderName;
    }

    async void PadInstall_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null || _scan == null) return;
        _g.Input.SourceFolder = PadFolder.Text.Trim();
        _g.Input.Kind = PadDinput.IsChecked == true ? "dinputto8" : "x360ce";
        if (_g.Input.Installed && _g.Input.InstalledKind != "" && _g.Input.InstalledKind != _g.Input.Kind)
        {
            PadStatus.Text = $"✗ {_g.Input.InstalledKind} is installed. Remove it first, then install {_g.Input.Kind}.";
            return;
        }
        var api = ControllerService.GameInputApi(_scan.Referenced, Profile, out var xdll);
        if (_g.Input.Kind == "dinputto8" && !_scan.Referenced.Contains("dinput.dll") &&
            MessageBox.Show(this, $"This game doesn't appear to use legacy dinput.dll (detected: {api}), so dinputto8 will probably have no effect. Install anyway?",
                            "dinputto8", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        var (ok, log) = _g.Input.Kind == "dinputto8"
            ? await ControllerService.InstallDinputto8Async(_g, Game32, this)
            : await ControllerService.InstallX360ceAsync(_g, Game32, xdll, this);
        PadStatus.Text = (ok ? "✓ " : "✗ ") + log;
        Save(); LoadChanges();
    }

    async void PadRemove_Click(object sender, RoutedEventArgs e) => await UndoGroup("input", PadStatus);

    // ------------------------------------------------------------------ 7. Launch & diagnostics

    readonly List<string> _launchLines = new();

    void Log(string line)
    {
        var l = $"[{DateTime.Now:HH:mm:ss}] {line}";
        _launchLines.Add(l);
        LaunchLog.AppendText(l + Environment.NewLine);
        LaunchLog.ScrollToEnd();
    }

    void LoadHistory()
    {
        if (_g == null) return;
        LaunchHistory.ItemsSource = null;
        LaunchHistory.ItemsSource = _g.Launches.AsEnumerable().Reverse().ToList();
    }

    void LaunchHistory_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LaunchHistory.SelectedItem is LaunchRecord r) LaunchLog.Text = r.Report;
    }

    async void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        var g = _g;
        if (!File.Exists(g.ExePath)) { MessageBox.Show(this, "The exe no longer exists."); return; }
        LaunchButton.IsEnabled = false;
        TabHost.SelectedIndex = TabHost.Items.Count - 2; // "Launch log"
        LaunchLog.Clear(); _launchLines.Clear();
        string? mountedIso = null;
        try
        {
            ReadCompatUi(); ReadWrapperUi();
            Log($"Preparing {g.DisplayName}");
            // Re-check the folder: the user may have installed or removed the loader since the game was selected.
            _loader = SafeDiscLoaderSupport.Detect(g.ExePath);
            bool loader = LoaderActive(g);
            if (loader)
            {
                Log($"SafeDisc loader mode: {g.Compat.SafeDiscLoader} → active ({(_loader.Detected ? _loader.Evidence : "forced On; loader not detected")}).");
                var removed = CompatLayers.Clear(g);
                Save(); LoadChanges();
                Log(removed != null
                    ? $"Compatibility layers (HKCU): removed \"{removed}\" - layers stop the loader from loading. The original value is in the change journal (Undo restores it)."
                    : "Compatibility layers (HKCU): none (not written while the SafeDisc loader is active).");
            }
            else
            {
                if (_loader.Detected) Log("SafeDisc loader found but loader mode is Off - launching normally.");
                var layers = CompatLayers.Apply(g);
                Save(); LoadChanges();
                Log($"Compatibility layers (HKCU): {layers ?? "(none)"}");
            }
            var hklm = CompatLayers.ReadHklm(g.ExePath);
            if (hklm != null) Log($"Compatibility layers (HKLM, set outside this app - not changed): {hklm}");

            Log($"Game graphics API: Direct3D {(GfxApi == "d3d8" ? "8" : "9")}");
            var wrapDlls = g.Wrapper.InstalledDlls.Count > 0 ? g.Wrapper.InstalledDlls : new List<string> { "d3d9.dll" };
            var missingDlls = wrapDlls.Where(d => !File.Exists(Path.Combine(g.GameDir, d))).ToList();
            if (g.Wrapper.Installed)
                Log(missingDlls.Count == 0
                    ? $"Graphics wrapper: {g.Wrapper.InstalledKind} ({string.Join(" + ", wrapDlls)} present in game folder)"
                    : $"WARNING: {g.Wrapper.InstalledKind} is recorded as installed but {string.Join(", ", missingDlls)} is missing from the game folder.");
            else
            {
                var foreign = new[] { "d3d8.dll", "d3d9.dll", "ddraw.dll", "dinput.dll", "dinput8.dll" }.Where(d => File.Exists(Path.Combine(g.GameDir, d))).ToList();
                Log(foreign.Count > 0 ? $"Note: {string.Join(", ", foreign)} in the game folder were not installed by this launcher (another wrapper or mod)." : "Graphics wrapper: none");
            }
            if (g.Input.Installed) Log($"Input wrapper: {(g.Input.InstalledKind == "" ? "x360ce" : g.Input.InstalledKind)}");

            if (_deps?.HasBlockingIssue == true)
            {
                var missing = string.Join(", ", _deps.Rows.Where(r => r.UsedByGame && !r.Present).Select(r => r.Dll));
                Log("Missing dependencies: " + missing);
                if (MessageBox.Show(this, $"These DLLs the game uses are missing:\n{missing}\n\nThe game will probably fail. Launch anyway?", "Missing dependencies",
                                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) { Log("Launch cancelled."); return; }
            }

            bool cpu = !loader || g.Compat.CpuSettingsWithLoader;
            if (!cpu && (g.Compat.CoreLimit > 0 || g.Compat.Priority != "Normal"))
                Log($"CPU limit ({(g.Compat.CoreLimit == 0 ? "none" : g.Compat.CoreLimit + " cores")}) and priority ({g.Compat.Priority}) skipped while the SafeDisc loader is active (opt in on the Compatibility tab).");
            var req = new LaunchRequest
            {
                ExePath = g.ExePath, Arguments = g.Compat.Arguments, WorkingDirectory = g.GameDir,
                AffinityMask = cpu ? CompatLayers.AffinityMask(g.Compat.CoreLimit) : 0, Priority = cpu ? g.Compat.Priority : "Normal",
            };
            mountedIso = await MountForLaunch(g);
            var launchStarted = DateTime.Now;
            bool needsAdmin = (g.Compat.RunAsAdmin || _scan?.Exe.ManifestRequiresAdmin == true) && !Environment.IsPrivilegedProcess;
            LaunchResult res;
            if (needsAdmin) res = await LaunchElevated(req, g.Compat.RunAsAdmin ? "RUNASADMIN is enabled for this game" : "the game's exe requests administrator rights");
            else
            {
                LaunchState.Text = "Game running… (this window keeps logging until the game and any child processes exit)";
                res = await Task.Run(() => GameProcess.Run(req, s => Dispatcher.Invoke(() => Log(s))));
                if (!res.Started && res.Win32Error == GameProcess.ERROR_ELEVATION_REQUIRED)
                {
                    Log("Windows says this exe requires elevation.");
                    res = await LaunchElevated(req, "Windows reported that the game requires administrator rights");
                }
            }
            await FinishLaunch(g, res, loader ? _loader : null, launchStarted);
        }
        catch (Exception ex) { Log("ERROR: " + ex); }
        finally
        {
            if (mountedIso != null && g.Compat.UnmountAfterExit)
            {
                try { await Task.Run(() => DiscImage.Unmount(mountedIso)); Log("Disc image unmounted (the launcher had mounted it)."); }
                catch (Exception ex) { Log("Could not unmount the disc image: " + ex.Message); }
            }
            UpdateIsoStatus();
            LaunchButton.IsEnabled = true;
            LaunchState.Text = "Ready.";
        }
    }

    async Task<LaunchResult> LaunchElevated(LaunchRequest req, string why)
    {
        var ok = MessageBox.Show(this, $"The game needs administrator rights ({why}).\n\nThe launcher will start a small elevated helper that starts the game " +
                                       "with your CPU/priority settings and reports back when it exits. The launcher itself stays non-admin.\n\nContinue?",
                                 "Admin rights needed", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ok != MessageBoxResult.Yes) return new LaunchResult { Error = "Cancelled - admin permission not granted." };
        Log("Starting via elevated helper…");
        LaunchState.Text = "Game running (elevated)…";
        return await GameProcess.RunElevatedAsync(req);
    }

    async Task FinishLaunch(GameEntry g, LaunchResult res, LoaderInfo? loader, DateTime launchStarted)
    {
        var rec = new LaunchRecord { Started = res.Started ? res.StartedLocal : DateTime.Now, Ended = res.AllExitedLocal, ExitCode = res.ExitCode };
        if (!res.Started)
        {
            Log($"FAILED TO START: {res.Error} (Win32 error {res.Win32Error})");
            rec.Summary = "failed to start: " + res.Error;
        }
        else
        {
            Log($"Elevated: {res.Elevated}. Affinity: {res.AffinityResult}. Priority: {res.PriorityResult}.");
            if (res.ProcessTree.Count > 0) Log("Process tree: " + string.Join(", ", res.ProcessTree));
            var mainTime = (res.MainExitedLocal ?? DateTime.Now) - res.StartedLocal;
            var totalTime = (res.AllExitedLocal ?? res.MainExitedLocal ?? DateTime.Now) - res.StartedLocal;
            // When child processes carried on (SafeDisc's helper, or a game that relaunches itself), the main exe's early
            // exit is expected; judge the session by how long the whole tree ran.
            var runTime = res.ChildrenOutlivedMain ? totalTime : mainTime;
            if (!string.IsNullOrEmpty(res.WorkingDirectory)) Log($"Working directory: {res.WorkingDirectory}");
            if (res.ChildrenOutlivedMain)
                Log($"Main exe exited after {mainTime.TotalSeconds:0.0}s with {ExitCodes.Describe((uint)(res.ExitCode ?? 0))} while its child processes kept running" +
                    $"{(loader != null ? " (expected with SafeDisc)" : "")}; the game tree ran {totalTime.TotalSeconds:0.0}s in total.");
            else
                Log($"Exit code: {ExitCodes.Describe((uint)(res.ExitCode ?? 0))} after {runTime.TotalSeconds:0.0}s");
            if (res.ExitCode != 0) Log($"Crash/exit time: {res.MainExitedLocal:yyyy-MM-dd HH:mm:ss}");
            Log("Waiting a few seconds for Windows Error Reporting to write event log entries…");
            await Task.Delay(3000);
            var events = await Task.Run(() => Diagnostics.EventLogErrors(Path.GetFileName(g.ExePath), res.StartedLocal));
            Log(events.Count == 0 ? "Event log: no application errors for this exe during the session." : $"Event log: {events.Count} entr{(events.Count == 1 ? "y" : "ies")}:");
            foreach (var ev in events) Log("  " + ev);
            foreach (var a in Diagnostics.CrashAdvice(events, g)) Log("SUGGESTION: " + a);
            if (loader != null)
            {
                // The loader's own log is the reliable signal here; the generic DRM guess would be a false alarm.
                bool ok = (res.ExitCode == 0 || res.ChildrenOutlivedMain) && totalTime > TimeSpan.FromSeconds(15) && events.Count == 0;
                // A crash inside an input/graphics/audio DLL happens in the game's own start-up code, i.e. after the
                // disc check - then the loader isn't the suspect and "may not have run" would send you the wrong way.
                var lateModule = new[] { "dinput.dll", "dinput8.dll", "xinput", "d3d8.dll", "d3d9.dll", "dsound.dll", "nvd3dum", "atidxx", "igdumd" }
                    .FirstOrDefault(m => events.Any(ev => ev.Contains("Faulting module name: " + m, StringComparison.OrdinalIgnoreCase)));
                if (!ok && lateModule != null)
                    Log($"SafeDisc loader: not the likely cause - the crash is in {lateModule}, which the game only loads after its start-up checks. Follow the suggestion above.");
                else
                    foreach (var l in SafeDiscLoaderSupport.PostLaunchReport(loader, launchStarted, ok)) Log(l);
            }
            else
            {
                var drmHint = DrmDetector.PostLaunchHint(_drm, runTime, res.ExitCode);
                bool mainFailed = res.ExitCode != 0 && !res.ChildrenOutlivedMain;
                if (drmHint != null && (mainFailed || runTime < TimeSpan.FromSeconds(20)))
                    Log((_drm.Any(d => d.Scheme == DrmDetector.ModifiedScheme) ? "MODIFIED EXE: " : "POSSIBLE DRM FAILURE: ") + drmHint);
            }
            rec.Summary = res.ChildrenOutlivedMain
                ? $"main exe exit {ExitCodes.Describe((uint)(res.ExitCode ?? 0))} after {mainTime.TotalSeconds:0}s, game tree ran {totalTime.TotalSeconds:0}s"
                : $"exit {ExitCodes.Describe((uint)(res.ExitCode ?? 0))}, ran {runTime.TotalSeconds:0}s";
        }
        rec.Report = BuildReport(g);
        g.Launches.Add(rec);
        if (g.Launches.Count > 30) g.Launches.RemoveAt(0);
        Save(); LoadHistory();
        try { File.WriteAllText(Path.Combine(AppPaths.Logs, $"{San(g.DisplayName)}-{rec.Started:yyyyMMdd-HHmmss}.txt"), rec.Report); } catch { }
    }

    static string San(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    string BuildReport(GameEntry g)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Legacy Game Launcher diagnostics ===");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.Append(Diagnostics.SystemSummary());
        sb.AppendLine();
        sb.AppendLine($"Game: {g.DisplayName}");
        sb.AppendLine($"Exe: {g.ExePath}");
        sb.AppendLine($"Arch: {_scan?.Exe.ArchName}   SHA-256: {g.Sha256}");
        sb.AppendLine($"Profile: {Profile?.Name ?? "none"} ({g.DetectionNote})");
        sb.AppendLine();
        sb.AppendLine("-- Active fixes --");
        sb.AppendLine($"Compat layers (HKCU): {CompatLayers.ReadHkcu(g.ExePath) ?? "(none)"}");
        var hklm = CompatLayers.ReadHklm(g.ExePath);
        if (hklm != null) sb.AppendLine($"Compat layers (HKLM): {hklm}");
        sb.AppendLine($"CPU limit: {(g.Compat.CoreLimit == 0 ? "none" : g.Compat.CoreLimit + " cores")}, priority: {g.Compat.Priority}, args: \"{g.Compat.Arguments}\"");
        sb.AppendLine($"SafeDisc loader: mode {g.Compat.SafeDiscLoader}, {(_loader.Detected ? "detected (" + _loader.Evidence + ")" : "not detected")}, " +
                      $"{(LoaderActive(g) ? "active" + (g.Compat.CpuSettingsWithLoader ? ", CPU settings applied" : ", CPU settings skipped") : "inactive")}");
        if (!string.IsNullOrWhiteSpace(g.Compat.DiscImagePath))
            sb.AppendLine($"Disc image: {g.Compat.DiscImagePath} (mount before launch: {g.Compat.MountDiscImage}, unmount after: {g.Compat.UnmountAfterExit})");
        var w = g.Wrapper;
        sb.AppendLine(w.Installed
            ? $"Graphics wrapper: {w.InstalledKind} - res {(w.ForcedWidth > 0 ? $"{w.ForcedWidth}x{w.ForcedHeight}" : "unforced")}, refresh cap {w.RefreshCap}, windowed {w.Windowed}, borderless {w.Borderless}, vsync {w.VSync}, fps limit {w.FpsLimit}"
            : "Graphics wrapper: none");
        sb.AppendLine($"Input wrapper: {(g.Input.Installed ? (g.Input.InstalledKind == "" ? "x360ce" : g.Input.InstalledKind) : "none")}");
        sb.AppendLine($"Graphics API: Direct3D {(GfxApi == "d3d8" ? "8" : "9")}; input API: {(_scan == null ? "?" : ControllerService.GameInputApi(_scan.Referenced, Profile, out _))}");
        sb.AppendLine($"Journaled changes: {g.Changes.Count}");
        sb.AppendLine();
        sb.AppendLine("-- Dependencies --");
        if (_deps == null) sb.AppendLine("(not checked)");
        else
        {
            var bad = _deps.Rows.Where(r => r.UsedByGame && !r.Present).Select(r => r.Dll).ToList();
            sb.AppendLine(bad.Count == 0 ? "All referenced legacy DLLs present." : "MISSING: " + string.Join(", ", bad));
            sb.AppendLine("Referenced by game: " + string.Join(", ", _scan?.Referenced.OrderBy(x => x) ?? Enumerable.Empty<string>()));
        }
        sb.AppendLine();
        sb.AppendLine("-- Copy protection --");
        sb.AppendLine(_drm.Count == 0 ? "none detected" : string.Join("\n", _drm.Select(d => $"{d.Scheme}: {d.Evidence}")));
        sb.AppendLine();
        sb.AppendLine("-- Launch log --");
        foreach (var l in _launchLines) sb.AppendLine(l);
        return sb.ToString();
    }

    void CopyDiag_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        var text = LaunchHistory.SelectedItem is LaunchRecord r ? r.Report
                 : _g.Launches.LastOrDefault()?.Report ?? BuildReport(_g);
        Clipboard.SetText(text);
        LaunchState.Text = "Diagnostics copied to the clipboard.";
    }

    void SaveDiag_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        var dlg = new SaveFileDialog { Filter = "Text file|*.txt", FileName = $"{San(_g.DisplayName)}-diagnostics.txt" };
        if (dlg.ShowDialog(this) != true) return;
        File.WriteAllText(dlg.FileName, LaunchHistory.SelectedItem is LaunchRecord r ? r.Report : _g.Launches.LastOrDefault()?.Report ?? BuildReport(_g));
    }

    // ------------------------------------------------------------------ 8. Changes & undo

    void LoadChanges()
    {
        if (_g == null) return;
        ChangeList.ItemsSource = null;
        ChangeList.ItemsSource = _g.Changes.ToList();
    }

    async Task UndoGroup(string? group, TextBox? status)
    {
        if (_g == null) return;
        var (ok, log) = await ChangeJournal.UndoAsync(_g, group, this);
        Save();
        LoadChanges(); LoadCompatUi(); LoadWrapperUi(); LoadPadUi();
        if (_scan != null) RenderOverview();
        var msg = (ok ? "✓ " : "✗ ") + log;
        if (status != null) status.Text = msg;
        UndoLog.Text = msg;
        if (group == "config") ConfigDetected.Text = msg;
    }

    async void UndoSelected_Click(object sender, RoutedEventArgs e)
    {
        if (ChangeList.SelectedItem is not ChangeRecord r) { UndoLog.Text = "Select a change first; its whole group (compat / wrapper / input / config) will be undone."; return; }
        await UndoGroup(r.Group, null);
    }

    async void UndoAll_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null || _g.Changes.Count == 0) { UndoLog.Text = "Nothing to undo."; return; }
        if (MessageBox.Show(this, $"Restore everything the launcher changed for {_g.DisplayName}? ({_g.Changes.Count} change(s))", "Undo all",
                            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await UndoGroup(null, null);
    }

    void OpenBackups_Click(object sender, RoutedEventArgs e) => Open(AppPaths.Backups);
}
