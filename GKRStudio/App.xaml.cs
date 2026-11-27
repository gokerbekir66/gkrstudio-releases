using System.IO;
using System.Windows;
using System.Windows.Threading;
using GKRStudio.Core;
using GKRStudio.UI;

namespace GKRStudio;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        LegacyCleanup.Run(); // former name: settings + old registrations
        Theme.Apply(Preferences.Load());

        var invocation = Invocation.Parse(e.Args);
        try
        {
            switch (invocation.Mode)
            {
                case InvocationMode.Files:
                {
                    var all = await SelectionCollector.CollectAsync(invocation.Paths);
                    if (all == null) { Shutdown(); return; } // handed to the leader process
                    MenuHost.ShowForFiles(all);
                    break;
                }
                case InvocationMode.Background:
                    MenuHost.ShowForBackground(invocation.Directory!);
                    break;
                case InvocationMode.Tray:
                    if (!Resident.Start()) Shutdown(); // already running in the background
                    break;
                default:
                    ShowSettings();
                    break;
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "GKR Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    public static void ShowSettings()
    {
        Theme.Apply(Preferences.Load()); // the background instance may have been running since before a theme change
        var existing = Current.Windows.OfType<MainWindow>().FirstOrDefault();
        if (existing != null) { existing.Activate(); return; }
        var window = new MainWindow();
        window.Closed += (_, _) => Launcher.ShutdownIfIdle();
        window.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "GKR Studio — beklenmeyen hata", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

public enum InvocationMode { Settings, Files, Background, Tray }

/// <summary>Parsed command line. The ONLY input that decides which menu appears.</summary>
public sealed record Invocation(InvocationMode Mode, IReadOnlyList<string> Paths, string? Directory)
{
    public static Invocation Parse(string[] args)
    {
        if (args.Length == 0) return new(InvocationMode.Settings, [], null);

        string first = args[0];
        if (first.Equals("--tray", StringComparison.OrdinalIgnoreCase)) return new(InvocationMode.Tray, [], null);
        if (first.Equals("--background", StringComparison.OrdinalIgnoreCase))
        {
            string dir = args.Length > 1 ? args[1].Trim('"') : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!System.IO.Directory.Exists(dir)) dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return new(InvocationMode.Background, [], dir);
        }

        var paths = (first.Equals("--menu", StringComparison.OrdinalIgnoreCase) ? args.Skip(1) : args)
            .Select(a => a.Trim('"'))
            .Where(a => a.Length > 0)
            .ToList();

        if (paths.Count == 1 && System.IO.Directory.Exists(paths[0]))
            return new(InvocationMode.Background, [], paths[0]); // folder itself: download clipboard link into it

        paths = paths.Where(File.Exists).ToList();
        return paths.Count == 0 ? new(InvocationMode.Settings, [], null) : new(InvocationMode.Files, paths, null);
    }
}
