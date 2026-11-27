using System.Windows;
using GKRStudio.Core;

namespace GKRStudio.UI;

public static class Launcher
{
    /// <summary>True when running as the background (tray) instance: never auto-exit.</summary>
    public static bool Resident { get; set; }

    public static bool CheckTools(MenuAction action)
    {
        string? missing = null;
        if (action.NeedsYtDlp && Tools.YtDlp == null) missing = "yt-dlp";
        else if (action.NeedsFfmpeg && Tools.Ffmpeg == null) missing = "ffmpeg";
        if (missing == null) return true;

        var answer = MessageBox.Show(
            L.F("Bu işlem için {0} gerekli ama bulunamadı.\n\nAyarlar penceresini açıp \"Eksik araçları kur\" düğmesiyle kurmak ister misiniz?", missing),
            "GKR Studio", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Yes) App.ShowSettings();
        else ShutdownIfIdle();
        return false;
    }

    /// <summary>Runs the job in a progress window.</summary>
    public static void Start(Job job, Window? owner)
    {
        if (!CheckTools(job.Action)) return;
        var window = new ProgressWindow(job) { Owner = owner is { IsVisible: true } ? owner : null };
        window.Closed += (_, _) => ShutdownIfIdle();
        window.Show();
        window.Activate();
    }

    public static void ShutdownIfIdle()
    {
        if (Resident) return;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!Resident && !Application.Current.Windows.OfType<Window>().Any(w => w.IsVisible))
                Application.Current.Shutdown();
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
