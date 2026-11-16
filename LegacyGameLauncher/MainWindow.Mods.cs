using System.Windows;
using System.Windows.Controls;
using LegacyGameLauncher.Services;

namespace LegacyGameLauncher;

/// <summary>Mods tab: per-game mod library with on/off switching through the change journal.</summary>
public partial class MainWindow
{
    void ModLogLine(string s) => ModLog.AppendText((ModLog.Text.Length > 0 ? "\n" : "") + $"[{DateTime.Now:HH:mm:ss}] {s}");

    void LoadMods(string? selectId = null)
    {
        if (_g == null || ModList == null) return;
        var mods = ModManager.List(_g);
        ModList.ItemsSource = mods;
        ModList.SelectedItem = mods.FirstOrDefault(m => m.Id == selectId);
        if (ModList.SelectedItem == null)
            ModDetails.Text = mods.Count == 0 ? "No mods yet. Import a .zip or a folder." : "Select a mod to see its files. Double-click switches it on/off.";
    }

    ModInfo? SelectedMod => ModList.SelectedItem as ModInfo;

    void ModList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_g == null || SelectedMod is not ModInfo m) return;
        var conflicts = m.Enabled ? new List<(ModInfo Mod, List<string> Files)>() : ModManager.Conflicts(_g, m);
        ModDetails.Text = $"{m.Name}\nStatus: {(m.Enabled ? "ON" : "off")}\nAdded: {m.Added:yyyy-MM-dd HH:mm}\nFrom: {m.Source}\n" +
                          $"Installs into: {(m.TargetSubfolder.Length == 0 ? "the game folder" : m.TargetSubfolder)}\n" +
                          (m.Notes.Length > 0 ? $"Note: {m.Notes}\n" : "") +
                          (conflicts.Count > 0 ? $"Conflicts with (on): {string.Join(", ", conflicts.Select(c => c.Mod.Name))}\n" : "") +
                          $"\nFiles ({m.Files.Count}):\n" + string.Join("\n", m.Files.Take(300).Select(f => "  " + f)) + (m.Files.Count > 300 ? "\n  …" : "");
    }

    async Task ImportMod(string path)
    {
        if (_g == null) return;
        var g = _g;
        try
        {
            ModLogLine("Importing " + path + "…");
            var sub = ModSubfolder.Text;
            var m = await Task.Run(() => ModManager.Import(g, path, "", sub));
            ModLogLine($"✓ Imported \"{m.Name}\" ({m.Files.Count} files). Select it and press Turn on.");
            if (m.Notes.Length > 0) ModLogLine("Note: " + m.Notes);
            LoadMods(m.Id);
        }
        catch (Exception ex) { ModLogLine("✗ " + ex.Message); }
    }

    async void ModImportZip_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Choose a mod (.zip)", Filter = "Zip archives (*.zip)|*.zip", Multiselect = true };
        if (dlg.ShowDialog(this) == true) foreach (var f in dlg.FileNames) await ImportMod(f);
    }

    async void ModImportFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Choose the mod's folder (laid out like the game folder)" };
        if (dlg.ShowDialog(this) == true) await ImportMod(dlg.FolderName);
    }

    async Task SetMod(bool on)
    {
        if (_g == null || SelectedMod is not ModInfo m) { ModLogLine("Select a mod first."); return; }
        try
        {
            var (ok, log) = on ? await ModManager.EnableAsync(_g, m, this) : await ModManager.DisableAsync(_g, m, this);
            ModLogLine((ok ? "✓ " : "✗ ") + log);
        }
        catch (Exception ex) { ModLogLine("✗ " + ex.Message); }
        Save(); LoadChanges(); LoadMods(m.Id);
    }

    async void ModEnable_Click(object sender, RoutedEventArgs e) => await SetMod(true);
    async void ModDisable_Click(object sender, RoutedEventArgs e) => await SetMod(false);
    async void ModList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SelectedMod is ModInfo m) await SetMod(!m.Enabled);
    }

    void ModDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null || SelectedMod is not ModInfo m) return;
        if (MessageBox.Show(this, $"Remove \"{m.Name}\" from this game's mod library?", "Remove mod", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        try { ModManager.Delete(_g, m); ModLogLine($"Removed \"{m.Name}\" from the library."); }
        catch (Exception ex) { ModLogLine("✗ " + ex.Message); }
        LoadMods();
    }

    void ModOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_g == null) return;
        Open(SelectedMod is ModInfo m ? ModManager.FilesFolder(_g, m) : Directory.CreateDirectory(ModManager.GameDir(_g)).FullName);
    }
}
