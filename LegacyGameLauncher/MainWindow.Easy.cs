using System.Windows;
using System.Windows.Controls;
using LegacyGameLauncher.Services;

namespace LegacyGameLauncher;

/// <summary>Easy / Advanced mode and the Home tab (plain-language status + one-click setup).</summary>
public partial class MainWindow
{
    void ApplyMode()
    {
        AdvancedToggle.IsChecked = _lib.AdvancedMode;
        foreach (TabItem t in TabHost.Items)
            if (t.Tag as string == "advanced") t.Visibility = _lib.AdvancedMode ? Visibility.Visible : Visibility.Collapsed;
        if (TabHost.SelectedItem is TabItem sel && sel.Visibility != Visibility.Visible) TabHost.SelectedIndex = 0;
    }

    void AdvancedToggle_Click(object sender, RoutedEventArgs e)
    {
        _lib.AdvancedMode = AdvancedToggle.IsChecked == true;
        Save(); ApplyMode();
    }

    void AddStatus(string icon, string text, string? detail = null)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        sp.Children.Add(new TextBlock { Text = icon, Width = 26, FontSize = 15 });
        var t = new StackPanel();
        t.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        if (detail != null) t.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Style = (Style)FindResource("Hint"), Margin = new Thickness(0) });
        sp.Children.Add(t);
        HomeStatus.Children.Add(sp);
    }

    void RenderHome()
    {
        if (_g == null || HomeStatus == null) return;
        HomeStatus.Children.Clear();
        AddStatus("✅", "Game found", Path.GetFileName(_g.ExePath) + (Profile != null ? $" - recognised as {Profile.Name}" : ""));

        if (_deps != null)
        {
            if (_deps.HasBlockingIssue) AddStatus("⚠️", "Some old DirectX/VC++ files are missing", "Switch on Advanced mode → Overview to install them from Microsoft.");
            else AddStatus("✅", "Required Windows components are installed");
        }

        var compat = CompatLayers.ReadHkcu(_g.ExePath);
        bool loader = SafeDiscLoaderSupport.IsActive(_g.Compat, _loader);
        if (loader) AddStatus("✅", "Your SafeDisc loader is set up", "The launcher starts the game in a way that works with it.");
        else AddStatus(compat == null ? "➖" : "✅", compat == null ? "No compatibility settings applied yet" : "Compatibility settings applied", compat == null ? "Press 'Apply recommended settings' below." : null);

        if (_drm.Any(d => d.Scheme == DrmDetector.ModifiedScheme)) AddStatus("⚠️", "The game's exe isn't the original", "See Advanced mode → Overview → Copy protection.");
        else if (_drm.Any(d => d.Scheme == "SafeDisc") && !loader) AddStatus("⚠️", "Disc copy protection (SafeDisc)", "SafeDisc can't run on Windows 10/11. See Advanced mode → Overview for details.");

        var pads = ControllerService.Detect();
        AddStatus(pads.Count > 0 ? "🎮" : "⌨️", pads.Count > 0 ? $"Controller connected: {pads[0].Name}" : "No controller connected", pads.Count > 0 ? null : "Plug in a pad if you want to use one.");

        var mods = ModManager.List(_g);
        var on = mods.Count(m => m.Enabled);
        AddStatus("🧩", mods.Count == 0 ? "No mods added" : $"{on} of {mods.Count} mod(s) turned on", "Manage them on the Mods tab.");

        HomeTips.Text = Profile == null
            ? "No built-in tips for this game yet. Press Launch; if it fails, the Launch log explains why."
            : string.Join("\n\n", new[] { Profile.InputApiHint, Profile.Notes }.Where(s => !string.IsNullOrWhiteSpace(s)));
        HomeResult.Text = "";
    }

    void HomeRecommended_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        CompatRecommended_Click(sender, e);
        CompatApply_Click(sender, e);
        HomeResult.Text = "✓ Done. " + CompatStatus.Text.Split('\n').FirstOrDefault();
        RenderHome();
        HomeResult.Text = "✓ Recommended settings applied. Press Launch to play.";
    }
}
