using System.Windows;
using LegacyGameLauncher.Services;

namespace LegacyGameLauncher;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Elevated helper modes: do one approved job, no UI, then exit.
        if (e.Args.Length == 3 && e.Args[0] == "--fileops")
        {
            Shutdown(FileOpRunner.RunHelper(e.Args[1], e.Args[2]));
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--launch")
        {
            Shutdown(GameProcess.RunLaunchHelper(e.Args[1], e.Args[2]));
            return;
        }

        base.OnStartup(e);
        AppPaths.Ensure();
        // Staged config copies are only needed until they've been copied into place.
        foreach (var f in Directory.EnumerateFiles(AppPaths.Staging))
            try { if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-1)) File.Delete(f); } catch { }
        ProfileCatalog.Load();
        ProfileCatalog.ExportExamples();
        DispatcherUnhandledException += (_, ex) =>
        {
            MessageBox.Show(ex.Exception.ToString(), "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };
        new MainWindow().Show();
    }
}
