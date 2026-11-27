using System.Windows;
using GKRStudio.Core;

namespace GKRStudio.UI;

/// <summary>Windows UI around <see cref="Updater"/>: ask, download with progress, install and restart.</summary>
public static class UpdateFlow
{
    private static bool busy;

    /// <param name="silent">Background check: say nothing when up to date or when the check fails.</param>
    public static async Task CheckAsync(bool silent, Window? owner = null)
    {
        if (busy) return;
        busy = true;
        try
        {
            var prefs = Preferences.Load();
            string feed = Updater.FeedFrom(prefs);
            if (string.IsNullOrWhiteSpace(feed))
            {
                if (!silent) MessageBox.Show(L.T("Güncelleme adresi ayarlanmamış."), "GKR Studio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            UpdateInfo? info;
            try { info = await Updater.CheckAsync(feed); }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show(L.T("Güncelleme denetlenemedi: ") + ex.Message, "GKR Studio", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (info == null)
            {
                if (!silent) MessageBox.Show(L.F("GKR Studio güncel (sürüm {0}).", Updater.Current), "GKR Studio", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string text = L.F("Yeni sürüm hazır: {0} (şu an {1}).", info.Version, Updater.Current)
                          + (info.Notes.Length > 0 ? "\n\n" + info.Notes : "") + "\n\n" + L.T("Şimdi indirip kurulsun mu? Uygulama yeniden başlayacak.");
            if (MessageBox.Show(text, "GKR Studio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var progress = new UpdateProgressWindow(info.Version.ToString());
            progress.Show();
            try
            {
                string zip = await Updater.DownloadAsync(info, f => progress.Dispatcher.BeginInvoke(() => progress.Report(f)));
                progress.Report(1);
                Updater.InstallAndRestart(zip, tray: Launcher.Resident || Resident.IsRunning(), reopenSettings: !Launcher.Resident);
                Launcher.Resident = false;
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                progress.Close();
                MessageBox.Show(L.T("Güncelleme kurulamadı: ") + ex.Message, "GKR Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally { busy = false; }
    }
}

/// <summary>Tiny progress window while the update downloads.</summary>
public sealed class UpdateProgressWindow : Window
{
    private readonly System.Windows.Controls.ProgressBar bar = new() { Minimum = 0, Maximum = 1, Height = 8, Margin = new Thickness(0, 10, 0, 0) };

    public UpdateProgressWindow(string version)
    {
        Style = (Style)Application.Current.FindResource("AppWindow");
        Title = "GKR Studio";
        Width = 360;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = L.F("Güncelleme indiriliyor ({0})…", version), FontWeight = FontWeights.SemiBold });
        panel.Children.Add(bar);
        Content = panel;
    }

    public void Report(double fraction) => bar.Value = fraction;
}
