using System.Windows;
using LegacyGameLauncher.Services;

namespace LegacyGameLauncher;

/// <summary>Sidebar: version, "Check for updates", and the update banner.</summary>
public partial class MainWindow
{
    UpdateInfo? _update;

    void InitUpdates()
    {
        UpdateService.CleanupOld();
        VersionText.Text = $"Version {UpdateService.Current}";
        // Quiet check shortly after start; failures (offline etc.) are ignored.
        Loaded += async (_, _) => { await Task.Delay(1500); await CheckForUpdates(quiet: true); };
    }

    async Task CheckForUpdates(bool quiet)
    {
        try
        {
            if (!quiet) VersionText.Text = "Checking…";
            var u = await UpdateService.CheckAsync();
            VersionText.Text = $"Version {UpdateService.Current}";
            if (u.IsNewer)
            {
                _update = u;
                UpdateText.Text = $"Version {u.Latest} is available (you have {UpdateService.Current}).";
                UpdateBanner.Visibility = Visibility.Visible;
                if (!quiet && !string.IsNullOrWhiteSpace(u.Notes))
                    MessageBox.Show(this, $"What's new in {u.Tag}:\n\n{Shorten(u.Notes, 1500)}", "Update available", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (!quiet)
                MessageBox.Show(this, $"You have the latest version ({UpdateService.Current}).", "No update", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            VersionText.Text = $"Version {UpdateService.Current}";
            if (!quiet) MessageBox.Show(this, "Couldn't check for updates: " + ex.Message, "Update check", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdates(quiet: false);

    async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_update == null) return;
        if (MessageBox.Show(this, $"Download and install version {_update.Latest}? The launcher will restart. Your games and settings are kept.",
                            "Update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        UpdateNowButton.IsEnabled = false;
        try
        {
            var exe = await UpdateService.DownloadAndSwapAsync(_update, new Progress<string>(s => UpdateText.Text = s));
            UpdateService.Restart(exe);
        }
        catch (Exception ex)
        {
            UpdateText.Text = "Update failed: " + ex.Message;
            UpdateNowButton.IsEnabled = true;
        }
    }
}
