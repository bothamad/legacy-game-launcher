using System.Windows;
using LegacyGameLauncher.Services;

namespace LegacyGameLauncher;

/// <summary>Controllers tab: per-game DS4Windows face-button layout.</summary>
public partial class MainWindow
{
    static readonly string[] LayoutKeys = DS4WindowsBridge.Presets.Keys.ToArray();

    void LoadLayoutUi()
    {
        if (_g == null || LayoutBox == null) return;
        if (LayoutBox.Items.Count == 0) foreach (var k in LayoutKeys) LayoutBox.Items.Add(DS4WindowsBridge.Presets[k].Label);
        LayoutBox.SelectedIndex = Math.Max(0, Array.IndexOf(LayoutKeys, _g.PadLayout));
        var inst = DS4WindowsBridge.Find();
        LayoutStatus.Text =
            (inst == null ? "DS4Windows not found (start it once so its settings exist)." :
             $"DS4Windows {(inst.Running ? "is running" : "found (not running)")} · your usual profile: \"{inst.BaseProfile}\"") +
            (_g.PadLayout != "Default" ? $"\nThis game uses: {DS4WindowsBridge.Presets[_g.PadLayout].Label} - profile \"{DS4WindowsBridge.ProfileName(_g)}\"." : "\nThis game uses DS4Windows' normal profile.");
    }

    async void LayoutApply_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        var key = LayoutKeys[Math.Max(0, LayoutBox.SelectedIndex)];
        if (key == "Default") { LayoutRemove_Click(sender, e); return; }
        if (DS4WindowsBridge.Find() is { Running: true } &&
            MessageBox.Show(this, "DS4Windows only reads per-game profiles when it starts, so the launcher will close DS4Windows, save the layout and start it again (a couple of seconds, your controller reconnects).\n\nContinue?",
                            "Button layout", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            LayoutStatus.Text = "Applying…";
            var (ok, log) = await DS4WindowsBridge.ApplyAsync(_g, key, this);
            Save(); LoadChanges();
            LoadLayoutUi();
            LayoutStatus.Text = (ok ? "✓ " : "✗ ") + log + "\n\n" + LayoutStatus.Text;
        }
        catch (Exception ex) { LayoutStatus.Text = "✗ " + ex.Message; }
    }

    async void LayoutRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        if (_g.PadLayout == "Default") { LoadLayoutUi(); LayoutStatus.Text = "This game already uses DS4Windows' normal profile.\n" + LayoutStatus.Text; return; }
        try
        {
            var (ok, log) = await DS4WindowsBridge.RemoveAsync(_g, this);
            Save(); LoadChanges();
            LoadLayoutUi();
            LayoutStatus.Text = (ok ? "✓ " : "✗ ") + log + "\n\n" + LayoutStatus.Text;
        }
        catch (Exception ex) { LayoutStatus.Text = "✗ " + ex.Message; }
    }
}
