using System.Diagnostics;
using System.IO;
using System.Windows;
using GKRStudio.Core;

namespace GKRStudio.UI;

public partial class ProgressWindow : Window
{
    private readonly Job job;
    private readonly CancellationTokenSource cts = new();
    private string? result;
    private bool running;

    public ProgressWindow(Job job)
    {
        InitializeComponent();
        Localizer.Translate(this);
        this.job = job;
        TitleText.Text = job.Action.Title + (job.Inputs.Count > 0 ? " — " + L.F("{0} dosya", job.Inputs.Count) : "");
        Loaded += async (_, _) => await RunAsync();
        Closing += (_, e) =>
        {
            if (!running) return;
            if (MessageBox.Show(this, L.T("İşlem sürüyor. İptal edilsin mi?"), "GKR Studio", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                cts.Cancel();
            else
                e.Cancel = true;
        };
    }

    private async Task RunAsync()
    {
        running = true;
        var eta = new Eta();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => EtaText.Text = eta.Text();
        timer.Start();
        var progress = new Progress<ProgressInfo>(p =>
        {
            StatusText.Text = L.Msg(p.Status);
            Bar.IsIndeterminate = p.Fraction == null;
            if (p.Fraction is double f) { Bar.Value = f; eta.Report(f); EtaText.Text = eta.Text(); }
        });
        void AppendLog(string line) => Dispatcher.BeginInvoke(() =>
        {
            if (Log.Text.Length > 200_000) Log.Text = Log.Text[^100_000..];
            Log.AppendText(L.Msg(line) + Environment.NewLine);
            Log.ScrollToEnd();
        });

        try
        {
            result = await Task.Run(() => ActionRunner.RunAsync(job, progress, AppendLog, cts.Token));
            Bar.IsIndeterminate = false;
            Bar.Value = 1;
            StatusText.Text = L.T("Tamamlandı ✔");
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Ok");
            RevealButton.Visibility = result != null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = L.T("İptal edildi.");
        }
        catch (Exception ex)
        {
            Bar.IsIndeterminate = false;
            StatusText.Text = L.T("Hata: ") + L.Msg(ex is AggregateException { InnerExceptions.Count: 1 } ag ? ag.InnerException!.Message : ex.Message);
            StatusText.Foreground = (System.Windows.Media.Brush)FindResource("Bad");
            AppendLog(ex.ToString());
        }
        finally
        {
            timer.Stop();
            EtaText.Text = "";
            running = false;
            CancelButton.Content = L.T("Kapat");
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (running) cts.Cancel();
        else Close();
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (result == null) return;
        var psi = File.Exists(result)
            ? new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", result } }
            : new ProcessStartInfo("explorer.exe") { ArgumentList = { result } };
        Process.Start(psi);
        Close();
    }
}
