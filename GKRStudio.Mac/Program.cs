using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using GKRStudio.Core;

namespace GKRStudio.Mac;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var invocation = Invocation.Parse(args);
        LegacyCleanup.Run(); // former name: settings + old login agent / Quick Actions
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => Crash(e.Exception);
        GKRStudio.Core.PdfToDocx.Trace = Log;
        try
        {
            AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .With(new MacOSPlatformOptions { ShowInDock = invocation.Mode == InvocationMode.Settings })
                .LogToTrace()
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex) { Crash(ex); throw; }
    }

    /// <summary>Diagnostic line in the same log file.</summary>
    public static void Log(string line)
    {
        try
        {
            string dir = OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs") : Path.GetTempPath();
            File.AppendAllText(Path.Combine(dir, "GKRStudio.log"), $"{DateTime.Now:u} {line}\n");
        }
        catch { }
    }

    /// <summary>Last-resort crash log: ~/Library/Logs/GKRStudio.log (macOS) or %TEMP% (other).</summary>
    public static void Crash(Exception? ex)
    {
        try
        {
            string dir = OperatingSystem.IsMacOS() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs") : Path.GetTempPath();
            File.AppendAllText(Path.Combine(dir, "GKRStudio.log"), $"{DateTime.Now:u} {ex}\n\n");
        }
        catch { }
    }
}

public sealed class App : Application
{
    public override void Initialize()
    {
        Name = "GKR Studio"; // macOS menu bar / About (otherwise "Avalonia Application")
        Styles.Add(new FluentTheme());
        AppTheme.Apply(Preferences.Load());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            Dispatcher.UIThread.Post(async () => await StartAsync(desktop.Args ?? []));
        base.OnFrameworkInitializationCompleted();
    }

    private static async Task StartAsync(string[] args)
    {
        var invocation = Invocation.Parse(args);
        Resident.ApplyMacDefaults();
        try
        {
            switch (invocation.Mode)
            {
                case InvocationMode.Files:
                    var all = await SelectionCollector.CollectAsync(invocation.Paths);
                    if (all == null) { AppHost.Exit(); return; }
                    MenuHost.ShowForFiles(all, invocation.WheelMode);
                    break;
                case InvocationMode.Background:
                    MenuHost.ShowForBackground(invocation.Directory!);
                    break;
                case InvocationMode.Tray:
                    if (!Resident.Start()) AppHost.Exit();
                    break;
                default:
                    ShowSettings();
                    // the login agent only starts at the next login: make sure the menu-bar instance runs now
                    var startPrefs = Preferences.Load();
                    if (OperatingSystem.IsMacOS() && startPrefs.FirstRunDone && startPrefs.StartWithWindows && !Resident.IsRunning())
                    {
                        Program.Log("menu-bar instance not running → starting it");
                        Resident.StartInBackground();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Program.Crash(ex);
            await Ask.Info(ex.Message);
            AppHost.Exit();
        }
    }

    public static void ShowSettings()
    {
        AppTheme.Apply(Preferences.Load());
        var existing = AppHost.Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing != null) { existing.Activate(); return; }
        var w = new SettingsWindow();
        w.Closed += (_, _) => AppHost.ShutdownIfIdle();
        w.Show();
        w.Activate();
    }
}

public enum InvocationMode { Settings, Files, Background, Tray }

public sealed record Invocation(InvocationMode Mode, IReadOnlyList<string> Paths, string? Directory, Catalog.Mode WheelMode)
{
    public static Invocation Parse(string[] args)
    {
        var mode = Catalog.Mode.All;
        var list = args.ToList();
        int m = list.IndexOf("--mode");
        if (m >= 0 && m + 1 < list.Count)
        {
            mode = list[m + 1] switch { "convert" => Catalog.Mode.Convert, "tools" => Catalog.Mode.Tools, "actions" => Catalog.Mode.Actions, _ => Catalog.Mode.All };
            list.RemoveRange(m, 2);
        }
        list.RemoveAll(a => a.StartsWith("-psn_", StringComparison.Ordinal)); // macOS process serial number
        if (list.Count == 0) return new(InvocationMode.Settings, [], null, mode);
        if (list[0] == "--tray") return new(InvocationMode.Tray, [], null, mode);
        if (list[0] == "--background")
        {
            string dir = list.Count > 1 && System.IO.Directory.Exists(list[1]) ? list[1] : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            return new(InvocationMode.Background, [], dir, mode);
        }
        var paths = (list[0] == "--menu" ? list.Skip(1) : list).Where(p => File.Exists(p) || System.IO.Directory.Exists(p)).ToList();
        if (paths.Count == 1 && System.IO.Directory.Exists(paths[0]) && mode == Catalog.Mode.All) return new(InvocationMode.Background, [], paths[0], mode);
        return paths.Count == 0 ? new(InvocationMode.Settings, [], null, mode) : new(InvocationMode.Files, paths, null, mode);
    }
}

/// <summary>App lifetime helpers: the background instance never exits on its own.</summary>
public static class AppHost
{
    public static bool Resident { get; set; }

    private static IClassicDesktopStyleApplicationLifetime? Desktop => Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    public static IReadOnlyList<Window> Windows => Desktop?.Windows ?? [];

    public static void Exit() => Desktop?.Shutdown();

    public static void ShutdownIfIdle()
    {
        if (Resident) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!Resident && !Windows.Any(w => w.IsVisible)) Exit();
        }, DispatcherPriority.Background);
    }

    public static bool CheckTools(MenuAction action)
    {
        string? missing = action.NeedsYtDlp && Tools.YtDlp == null ? "yt-dlp" : action.NeedsFfmpeg && Tools.Ffmpeg == null ? "ffmpeg" : null;
        if (missing == "yt-dlp" && OperatingSystem.IsMacOS())
        {
            _ = Dispatcher.UIThread.InvokeAsync(async () =>
            {
                if (!await Ask.YesNo(L.T("Link indirmek için yt-dlp gerekli. Şimdi otomatik indirilsin mi? (~40 MB, resmi GitHub sürümü)"))) { ShutdownIfIdle(); return; }
                try
                {
                    Toast.Show(L.T("yt-dlp indiriliyor…"));
                    await Task.Run(() => Tools.InstallDownloadToolsAsync(_ => { }));
                    await Ask.Info(L.T("yt-dlp kuruldu. İndirmeyi yeniden başlatın."));
                }
                catch (Exception ex) { Program.Log("yt-dlp install failed: " + ex); await Ask.Info(ex.Message); }
                ShutdownIfIdle();
            });
            return false;
        }
        if (missing == null) return true;
        _ = Dispatcher.UIThread.InvokeAsync(async () =>
        {
            if (await Ask.YesNo(L.F("Bu işlem için {0} gerekli ama bulunamadı.\n\nAyarlar penceresini açıp \"Eksik araçları kur\" düğmesiyle kurmak ister misiniz?", missing)))
                App.ShowSettings();
            else ShutdownIfIdle();
        });
        return false;
    }

    public static void Start(Job job)
    {
        if (!CheckTools(job.Action)) return;
        var window = new ProgressWindow(job);
        window.Closed += (_, _) => ShutdownIfIdle();
        window.Show();
        window.Activate();
    }

    public static void Open(string pathOrUrl, bool reveal = false)
    {
        if (OperatingSystem.IsMacOS()) Process.Start("/usr/bin/open", reveal ? ["-R", pathOrUrl] : [pathOrUrl]);
        else Process.Start(new ProcessStartInfo(reveal ? "explorer.exe" : pathOrUrl) { UseShellExecute = true, ArgumentList = { } });
    }
}

/// <summary>Colours from settings (theme + accent) and the per-action wheel icons.</summary>
public static class AppTheme
{
    public static readonly (string Name, string Hex)[] Accents =
    [
        ("Mavi", "#3A5FD9"), ("Aslan turuncusu", "#E8741E"), ("Mor", "#7B4DDB"), ("Yeşil", "#2E9E6A"),
        ("Pembe", "#D9467A"), ("Kırmızı", "#D93A3A"), ("Turkuaz", "#1FA2B8"), ("Altın", "#C9971E"),
    ];

    public static readonly string[] Themes = ["Koyu", "Açık", "Sistem"];

    public static Color Accent(Preferences p) => Color.TryParse(p.AccentColor, out var c) ? c : Color.Parse("#3A5FD9");

    public static bool IsDark(Preferences p) => p.Theme switch
    {
        "Açık" => false,
        "Sistem" => SystemIsDark(),
        _ => true,
    };

    private static bool SystemIsDark()
    {
        if (!OperatingSystem.IsMacOS()) return true;
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/defaults") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "read", "-g", "AppleInterfaceStyle" }) psi.ArgumentList.Add(a);
            using var proc = Process.Start(psi)!;
            string s = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1000);
            return s.Contains("Dark", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static void Apply(Preferences p)
    {
        if (Application.Current is not { } app) return;
        app.RequestedThemeVariant = IsDark(p) ? ThemeVariant.Dark : ThemeVariant.Light;
        app.Resources["SystemAccentColor"] = Accent(p);
    }

    public static Color Lighten(Color c, double a) => Color.FromRgb((byte)(c.R + (255 - c.R) * a), (byte)(c.G + (255 - c.G) * a), (byte)(c.B + (255 - c.B) * a));
    public static Color Darken(Color c, double a) => Color.FromRgb((byte)(c.R * (1 - a)), (byte)(c.G * (1 - a)), (byte)(c.B * (1 - a)));

    public sealed record WheelColors(Color Halo, Color Segment, Color Gap, Color Hub, Color HubHot, Color Rim, Color Text, Color Muted, Color Accent);

    public static WheelColors Wheel(Preferences p) => IsDark(p)
        ? new(Color.FromArgb(0xF0, 0x12, 0x14, 0x18), Color.Parse("#24272E"), Color.Parse("#121418"), Color.Parse("#1B1E24"), Color.Parse("#2A2E37"), Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Colors.White, Color.Parse("#9AA3B2"), Accent(p))
        : new(Color.FromArgb(0xF4, 0xF3, 0xF4, 0xF7), Color.Parse("#FFFFFF"), Color.Parse("#E2E5EB"), Color.Parse("#F7F8FA"), Color.Parse("#ECEEF2"), Color.FromArgb(0x40, 0, 0, 0), Color.Parse("#1B1E24"), Color.Parse("#6B7280"), Accent(p));

    public static string Icon(MenuAction a)
    {
        string id = a.Id, target = id[(id.IndexOf(':') + 1)..];
        return id switch
        {
            "group:convert" => "🔁", "group:tools" => "🧰", "group:actions" => "⚡",
            Catalog.PickFiles => "📂", Catalog.OpenSettings => "⚙",
            "img:jpeg" or "av:compress" or "pdf:compress" => "🗜", "img:strip" or "av:strip" or "pdf:strip" => "🧹",
            "img:adjust" => "◐", "img:markup" => "✏", "img:frame" => "🎨", "img:crop" or "av:trim" => "✂", "av:crop" => "🔲",
            "img:redact" or "av:redact" => "🙈", "img:qr" or "pdf:qr" => "▦", "img:ocr" or "pdf:ocr" => "🔤", "tr:translate" => "🌐",
            "av:mute" => "🔇", "av:speed" => "⏩", "av:frame" => "📸", "av:split" or "pdf:split" => "➗", "av:merge" or "pdf:merge" => "🔗",
            "av:loudnorm" => "🔊", "av:visualize" => "〰", "av:channels" => "🎧", "av:bleep" => "📢", "pdf:organize" => "📑",
            "arc:extract" or "zip:extract" => "📂", "zip:create" => "📦", "act:float" => "🪟", "act:copy" => "📋", "act:move" => "🚚",
            "act:copypath" => "📍", "act:terminal" => "💻", "dl:video" => "🎬", "dl:audio" => "🎵", "dl:thumb" or "dl:image" => "🖼",
            _ => target switch
            {
                "png" or "jpg" or "webp" or "tiff" or "avif" or "bmp" or "ico" => "🖼",
                "gif" => id.StartsWith("av:") ? "🎞" : "🖼",
                "pdf" => "📄", "docx" or "txt" or "md" => "📝", "html" => "🌐", "epub" => "📖",
                "mp4" or "mov" or "mkv" or "webm" or "avi" or "wmv" => "🎬",
                "mp3" or "m4a" or "wav" or "flac" or "ogg" or "opus" or "aiff" or "wma" => "🎵",
                "xlsx" or "ods" or "csv" or "tsv" => "📊", "srt" or "vtt" => "💬", "zip" or "tar" or "gz" => "📦",
                _ => "•",
            },
        };
    }
}

/// <summary>Small modal-less prompts (Avalonia has no MessageBox).</summary>
public static class Ask
{
    public static Task Info(string text) => Show(text, [L.T("Tamam")]);

    public static async Task<bool> YesNo(string text) => await Show(text, [L.T("Evet"), L.T("Hayır")]) == 0;

    public static Task<int> Show(string text, string[] buttons)
    {
        var tcs = new TaskCompletionSource<int>();
        var w = new Window
        {
            Title = "GKR Studio", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, Topmost = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        var panel = new StackPanel { Margin = new Avalonia.Thickness(22), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var b = new Button { Content = buttons[i], MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            if (i == 0) b.Classes.Add("accent");
            b.Click += (_, _) => { tcs.TrySetResult(index); w.Close(); };
            row.Children.Add(b);
        }
        panel.Children.Add(row);
        w.Content = panel;
        w.Closed += (_, _) => tcs.TrySetResult(buttons.Length - 1);
        w.Show();
        w.Activate();
        return tcs.Task;
    }
}

/// <summary>File/folder pickers without an existing window (a tiny host window provides the StorageProvider).</summary>
public static class Pickers
{
    private static async Task<T> WithHost<T>(Func<TopLevel, Task<T>> work)
    {
        var host = new Window { Width = 1, Height = 1, SystemDecorations = SystemDecorations.None, ShowInTaskbar = false, Opacity = 0, Position = new PixelPoint(-2000, -2000) };
        host.Show();
        try { return await work(host); }
        finally { host.Close(); }
    }

    public static Task<string[]> Files(string title, string? startDir = null) => WithHost(async top =>
    {
        var start = startDir != null ? await top.StorageProvider.TryGetFolderFromPathAsync(startDir) : null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = title, AllowMultiple = true, SuggestedStartLocation = start });
        return files.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToArray();
    });

    public static Task<string?> Folder(string title, string? startDir = null) => WithHost(async top =>
    {
        var start = startDir != null ? await top.StorageProvider.TryGetFolderFromPathAsync(startDir) : null;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, SuggestedStartLocation = start });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    });
}

/// <summary>Frame-based animation helper (keeps the wheel/bite code simple and portable).</summary>
public static class Tween
{
    public static void Run(double ms, Action<double> step, Action? done = null)
    {
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Render, (_, _) => { });
        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, sw.Elapsed.TotalMilliseconds / ms);
            step(t);
            if (t >= 1) { timer.Stop(); done?.Invoke(); }
        };
        step(0);
        timer.Start();
    }

    public static double EaseOut(double t) => 1 - Math.Pow(1 - t, 3);
    public static double EaseIn(double t) => t * t * t;
    public static double Back(double t) { const double s = 1.2; t -= 1; return t * t * ((s + 1) * t + s) + 1; }
}

public static class Assets
{
    public static WindowIcon? Icon() => OperatingSystem.IsMacOS() ? null : TrayIcon();
    public static WindowIcon TrayIcon() => new(AssetLoader.Open(new Uri("avares://GKRStudio/Assets/logo.png")));
    public static Avalonia.Media.Imaging.Bitmap Logo() => new(AssetLoader.Open(new Uri("avares://GKRStudio/Assets/logo.png")));
}
