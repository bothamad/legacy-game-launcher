using System.Windows;
using System.Windows.Controls;
using LegacyGameLauncher.Services;

namespace LegacyGameLauncher;

/// <summary>Fixes tab: download (official GitHub release, SHA-256 checked) + install with the existing backup/undo installers.</summary>
public partial class MainWindow
{
    static ToolSpec ToolFor(string tag) => tag switch
    {
        "dinputto8" => ToolDownloader.Dinputto8,
        "dgvoodoo2" => ToolDownloader.DgVoodoo2,
        _ => ToolDownloader.Dxvk,
    };

    void FixLogLine(string s) => FixLog.AppendText((FixLog.Text.Length > 0 ? "\n" : "") + $"[{DateTime.Now:HH:mm:ss}] {s}");

    void UpdateFixStatus()
    {
        if (_g == null || FixDinputStatus == null) return;
        string Cache(ToolSpec t) => ToolDownloader.Cached(t) is { } c ? $"downloaded: {c.Manifest.Tag}" : "not downloaded yet";

        bool usesDinput = _scan?.Referenced.Contains("dinput.dll") == true;
        FixDinputStatus.Text = (_g.Input.Installed && _g.Input.InstalledKind == "dinputto8" ? "✓ Applied to this game" : "Not applied") +
                               $" · {Cache(ToolDownloader.Dinputto8)}" +
                               (usesDinput ? " · this game uses legacy dinput.dll, so it applies" : _scan != null ? " · this game doesn't use legacy dinput.dll" : "");
        FixDgStatus.Text = (_g.Wrapper.Installed && _g.Wrapper.InstalledKind == "dgVoodoo2" ? $"✓ Applied ({string.Join(" + ", _g.Wrapper.InstalledDlls)})" : "Not applied") +
                           $" · {Cache(ToolDownloader.DgVoodoo2)} · game uses Direct3D {(GfxApi == "d3d8" ? "8" : "9")}";
        FixDxvkStatus.Text = (_g.Wrapper.Installed && _g.Wrapper.InstalledKind == "DXVK" ? $"✓ Applied ({string.Join(" + ", _g.Wrapper.InstalledDlls)})" : "Not applied") +
                             $" · {Cache(ToolDownloader.Dxvk)}";
    }

    /// <summary>Uses the cached copy, or asks and downloads the latest release.</summary>
    async Task<string?> EnsureTool(ToolSpec t)
    {
        if (ToolDownloader.Cached(t) is { } c)
        {
            FixLogLine($"Using downloaded {t.Name} {c.Manifest.Tag} (SHA-256 {c.Manifest.Sha256[..12]}…).");
            return ToolDownloader.InstallRoot(t, c.Folder);
        }
        FixLogLine($"Checking the latest {t.Name} release on github.com/{t.Repo}…");
        var rel = await ToolDownloader.LatestAsync(t);
        var ok = MessageBox.Show(this,
            $"Download {t.Name} {rel.Tag}?\n\nFile: {rel.AssetName} ({rel.Size / 1024.0 / 1024.0:0.0} MB)\nFrom: github.com/{t.Repo} (official release, {rel.Published:yyyy-MM-dd})\n" +
            $"Licence: {t.Licence}\nChecksum: {(rel.Sha256 != null ? "verified against GitHub's SHA-256" : "none published - recorded locally")}\n\n{t.What}",
            "Download fix", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ok != MessageBoxResult.Yes) { FixLogLine("Download cancelled."); return null; }
        var progress = new Progress<string>(s => { FixLogLine(s); LaunchState.Text = s; });
        var folder = await ToolDownloader.DownloadAsync(t, rel, progress);
        return ToolDownloader.InstallRoot(t, folder);
    }

    async void FixApply_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null || _scan == null) return;
        var t = ToolFor((string)((FrameworkElement)sender).Tag);
        var btn = (UIElement)sender; btn.IsEnabled = false;
        try
        {
            if (t.Id == "dinputto8")
            {
                if (_g.Input.Installed && _g.Input.InstalledKind is not ("" or "dinputto8"))
                { FixLogLine($"✗ {_g.Input.InstalledKind} is installed for this game. Remove it on the Controllers tab first."); return; }
                if (_g.Input.Installed && _g.Input.InstalledKind == "") { FixLogLine("✗ x360ce is installed for this game. Remove it on the Controllers tab first."); return; }
                if (!_scan.Referenced.Contains("dinput.dll") &&
                    MessageBox.Show(this, "This game doesn't appear to use legacy dinput.dll, so dinputto8 will probably have no effect. Apply anyway?", "dinputto8", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
                var folder = await EnsureTool(t);
                if (folder == null) return;
                _g.Input.Kind = "dinputto8"; _g.Input.SourceFolder = folder;
                var (ok, log) = await ControllerService.InstallDinputto8Async(_g, Game32, this);
                FixLogLine((ok ? "✓ " : "✗ ") + log);
            }
            else
            {
                var kind = t.Id == "dgvoodoo2" ? "dgVoodoo2" : "DXVK";
                if (_g.Wrapper.Installed && _g.Wrapper.InstalledKind != kind)
                { FixLogLine($"✗ {_g.Wrapper.InstalledKind} is applied to this game. Press its Remove first, then apply {kind}."); return; }
                var folder = await EnsureTool(t);
                if (folder == null) return;
                ReadWrapperUi();
                _g.Wrapper.Kind = kind; _g.Wrapper.SourceFolder = folder;
                var (ok, log) = await WrapperManager.InstallAsync(_g, Game32, GfxApi, this);
                FixLogLine((ok ? "✓ " : "✗ ") + log);
            }
        }
        catch (Exception ex) { FixLogLine("✗ " + ex.Message); }
        finally
        {
            btn.IsEnabled = true;
            Save(); LoadChanges(); LoadWrapperUi(); LoadPadUi(); RenderOverview(); UpdateFixStatus();
            LaunchState.Text = "Ready.";
        }
    }

    async void FixRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        var t = ToolFor((string)((FrameworkElement)sender).Tag);
        if (t.Id == "dinputto8")
        {
            if (!(_g.Input.Installed && _g.Input.InstalledKind == "dinputto8")) { FixLogLine("dinputto8 isn't applied to this game."); return; }
            await UndoGroup("input", null);
        }
        else
        {
            var kind = t.Id == "dgvoodoo2" ? "dgVoodoo2" : "DXVK";
            if (!(_g.Wrapper.Installed && _g.Wrapper.InstalledKind == kind)) { FixLogLine($"{kind} isn't applied to this game."); return; }
            await UndoGroup("wrapper", null);
        }
        FixLogLine(UndoLog.Text);
        UpdateFixStatus();
    }
}
