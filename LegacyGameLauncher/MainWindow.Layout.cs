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
        LayoutTouchpadOff.IsChecked = _g.PadTouchpadOff;
        LayoutDpadStick.IsChecked = _g.PadDpadAsStick;
        LayoutTouchArrows.IsChecked = _g.PadTouchArrows;
        var inst = DS4WindowsBridge.Find();
        LayoutStatus.Text =
            (inst == null ? "DS4Windows not found (start it once so its settings exist)." :
             $"DS4Windows {(inst.Running ? "is running" : "found (not running)")} · your usual profile: \"{inst.BaseProfile}\"") +
            (_g.Changes.Any(c => c.Group == DS4WindowsBridge.Group)
                ? $"\nThis game uses its own profile \"{DS4WindowsBridge.ProfileName(_g)}\": {DS4WindowsBridge.Presets[_g.PadLayout].Label}" +
                  $"{(_g.PadTouchpadOff ? ", touchpad off" : "")}{(_g.PadDpadAsStick ? ", D-pad as left stick" : "")}{(_g.PadTouchArrows ? ", touchpad ←/→ = arrow keys" : "")}."
                : "\nThis game uses DS4Windows' normal profile.");
    }

    async void LayoutApply_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        var key = LayoutKeys[Math.Max(0, LayoutBox.SelectedIndex)];
        _g.PadTouchpadOff = LayoutTouchpadOff.IsChecked == true;
        _g.PadDpadAsStick = LayoutDpadStick.IsChecked == true;
        _g.PadTouchArrows = LayoutTouchArrows.IsChecked == true;
        if (key == "Default" && !_g.PadTouchpadOff && !_g.PadDpadAsStick && !_g.PadTouchArrows) { LayoutRemove_Click(sender, e); return; }
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
