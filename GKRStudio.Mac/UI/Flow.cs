using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using GKRStudio.Core;
using Path = System.IO.Path;

namespace GKRStudio.Mac;

public sealed record WheelContext(IReadOnlyList<string> Inputs, IReadOnlyList<string> AllPaths, string OutputDir, LinkInfo? Link);

/// <summary>Builds every wheel from the current input only (file types, clipboard now, settings).</summary>
public static class MenuHost
{
    public static WheelWindow? Current { get; private set; }

    public static void ShowForFiles(IReadOnlyList<string> paths, Catalog.Mode mode = Catalog.Mode.All)
    {
        var prefs = Preferences.Load();
        var kind = Catalog.Classify(paths);
        var inputs = paths.Where(p => Catalog.KindOf(p) == kind).ToList();
        string first = paths[0];
        string outputDir = (Directory.Exists(first) ? Path.GetDirectoryName(first.TrimEnd('/')) : Path.GetDirectoryName(first)) ?? OutputPaths.Downloads;
        var link = mode is Catalog.Mode.Actions or Catalog.Mode.All ? ClipboardReader.ReadLink() : null;

        IReadOnlyList<MenuAction> actions = Catalog.ForMode(mode, kind, inputs.Count, inputs.Select(Path.GetExtension).Distinct().Count() == 1 ? Path.GetExtension(inputs.FirstOrDefault() ?? "") : null);
        if (kind is InputKind.Mixed or InputKind.Unsupported && mode is not Catalog.Mode.Actions)
            actions = mode == Catalog.Mode.All ? Catalog.ForOther() : [];
        if (link != null)
        {
            var downloads = Catalog.ForLink(link);
            actions = mode == Catalog.Mode.Actions ? [.. actions, .. downloads]
                : actions.Select(a => a.Id == "group:actions" ? a with { Children = [.. a.Children!, .. downloads] } : a).ToList();
        }
        actions = MacOnly(prefs.Filter(actions));

        string title = kind is InputKind.Mixed or InputKind.Unsupported && mode == Catalog.Mode.All ? "GKR Studio" : Catalog.ModeTitle(mode);
        string subtitle = Catalog.Describe(kind, kind is InputKind.Mixed or InputKind.Unsupported ? paths.Count : inputs.Count);
        if (actions.Count == 0) subtitle += "\n" + L.F("Bu tür için {0} yok", Catalog.ModeTitle(mode).ToLowerInvariant());
        Show(title, subtitle, paths.Count, actions, new WheelContext(inputs.Count > 0 ? inputs : paths, paths, outputDir, link));
    }

    public static void ShowForBackground(string directory)
    {
        var link = ClipboardReader.ReadLink();
        string title = link == null ? "GKR Studio" : L.T("İndir");
        string subtitle = link == null ? L.T("Panoda link yok") : KindText(link);
        Show(title, subtitle, 0, MacOnly(Preferences.Load().Filter(Catalog.ForBackground(link))), new WheelContext([], [], directory, link));
    }

    public static void ShowForLink(LinkInfo link, string directory) =>
        Show(L.T("İndir"), KindText(link), 0, Catalog.ForLink(link), new WheelContext([], [], directory, link));

    private static string KindText(LinkInfo link) => link.Kind switch
    {
        LinkKind.YouTube => L.T("YouTube linki"), LinkKind.Image => L.T("Resim linki"), _ => L.T("Medya linki"),
    };

    /// <summary>Windows Share has no macOS counterpart here.</summary>
    private static IReadOnlyList<MenuAction> MacOnly(IReadOnlyList<MenuAction> actions) =>
        actions.Where(a => a.Id != "act:share").Select(a => a.IsGroup ? a with { Children = MacOnly(a.Children!) } : a).ToList();

    private static void Show(string title, string subtitle, int badge, IReadOnlyList<MenuAction> actions, WheelContext ctx)
    {
        Program.Log($"wheel show: {title} actions={actions.Count}");
        var window = new WheelWindow(title, subtitle, badge, actions, a => ActionFlow.Run(a, ctx));
        window.Closed += (_, _) => { if (Current == window) Current = null; if (!window.Chosen) AppHost.ShutdownIfIdle(); };
        Current = window;
        window.Show();
    }
}

/// <summary>What happens after a slice is chosen: ask parameters, then run.</summary>
public static class ActionFlow
{
    public static readonly string[] VideoQualities = ["1080p", "720p", "480p", "360p", "En yüksek (4K — bazı oynatıcılarda açılmayabilir)"];
    public static readonly string[] AudioQualities = ["320 kbps", "256 kbps", "192 kbps", "128 kbps"];

    public static async void Run(MenuAction action, WheelContext ctx)
    {
        try
        {
            switch (action.Id)
            {
                case Catalog.OpenSettings: App.ShowSettings(); return;
                case Catalog.PickFiles:
                {
                    var files = await Pickers.Files(L.T("GKR Studio — dosya seçin"), ctx.OutputDir);
                    if (files.Length > 0) MenuHost.ShowForFiles(files); else AppHost.ShutdownIfIdle();
                    return;
                }
                case "img:markup" or "img:crop" or "img:redact":
                {
                    var editor = new ImageEditorWindow(ctx.Inputs[0], action.Id == "img:crop", action.Id == "img:redact");
                    editor.Closed += (_, _) => AppHost.ShutdownIfIdle();
                    editor.Show();
                    return;
                }
                case "act:copypath":
                    ClipboardReader.WriteText(string.Join("\n", ctx.AllPaths));
                    Toast.Show(L.T("Yol panoya kopyalandı"));
                    return;
                case "act:terminal":
                    Process.Start("/usr/bin/open", ["-a", "Terminal", ctx.OutputDir]);
                    AppHost.ShutdownIfIdle();
                    return;
                case "act:reveal":
                    AppHost.Open(ctx.AllPaths.FirstOrDefault() ?? ctx.OutputDir, reveal: ctx.AllPaths.Count > 0);
                    AppHost.ShutdownIfIdle();
                    return;
                case "act:float":
                    FloatShelf.Add(ctx.AllPaths);
                    return;
            }
            if (!AppHost.CheckTools(action)) return;
            var inputs = action.Id is "zip:create" or "act:copy" or "act:move" ? ctx.AllPaths : ctx.Inputs;
            var options = await AskAsync(action, inputs, ctx);
            if (options == null) { AppHost.ShutdownIfIdle(); return; }
            AppHost.Start(new Job(action, inputs, ctx.Link, ctx.OutputDir, options));
        }
        catch (Exception ex)
        {
            await Ask.Info(ex.Message);
            AppHost.ShutdownIfIdle();
        }
    }

    private static async Task<Dictionary<string, string>?> AskAsync(MenuAction action, IReadOnlyList<string> inputs, WheelContext ctx)
    {
        string first = inputs.FirstOrDefault() ?? "";
        switch (action.Id)
        {
            case "dl:video":
                return await ParamDialog.Ask("Video kalitesi", "1080p ve altı H.264 olarak iner ve her oynatıcıda açılır. Seçtiğiniz çözünürlük videoda yoksa altındaki en iyisi indirilir.",
                    new ChoiceField("quality", "Çözünürlük", VideoQualities));
            case "dl:audio":
                return await ParamDialog.Ask("MP3 kalitesi", "Yüksek bit hızı = daha iyi ses, daha büyük dosya.", new ChoiceField("quality", "Bit hızı", AudioQualities));
            case "img:adjust":
                return await ParamDialog.Ask("Fotoğrafı düzenle", L.T("Renk ayarları -100 ile 100 arası; 0 = değişiklik yok."),
                    new TextField("percent", "Boyut (%)", "100"), new ChoiceField("rotate", "Döndür", ["Yok", "90° sağa", "90° sola", "180°"]),
                    new ChoiceField("flip", "Yansıt", ["Hayır", "Yatay"]), new TextField("brightness", "Parlaklık", "0"),
                    new TextField("contrast", "Kontrast", "0"), new TextField("saturation", "Doygunluk", "0"));
            case "img:frame":
                return await ParamDialog.Ask("Arka plan ekle", "Kenar boşluğu ve köşe, görselin kısa kenarına göre yüzde.",
                    new TextField("padding", "Kenar boşluğu (%)", "8"), new ChoiceField("background", "Arka plan", ["Beyaz", "Siyah", "Gri", "Mavi degrade", "Gün batımı", "Şeffaf"], 3),
                    new TextField("radius", "Köşe yuvarlaklığı (%)", "4"), new ChoiceField("shadow", "Gölge", ["Var", "Yok"]));
            case "img:redact":
            {
                var region = await RegionPicker.PickImage(first, "Gizlenecek alanı seçin", "Fareyle sürükleyerek yüz, plaka veya yazıyı çizin.");
                if (region is not { } r) return null;
                var style = await ParamDialog.Ask("Sansür biçimi", null, new ChoiceField("style", "Biçim", ["Bulanık", "Piksel", "Siyah"]));
                if (style == null) return null;
                style["x"] = r.X.ToString(); style["y"] = r.Y.ToString(); style["w"] = r.Width.ToString(); style["h"] = r.Height.ToString();
                return style;
            }
            case "img:jpeg":
                return await ParamDialog.Ask("JPEG sıkıştır", "Düşük kalite = küçük dosya. 70-80 çoğu fotoğraf için iyi bir denge.", new TextField("quality", "Kalite (1-100)", "75"));
            case "av:trim":
            {
                var info = await Ffmpeg.ProbeAsync(first);
                return await ParamDialog.Ask("Kes", L.F("Süre: {0}. Biçim: saniye (90) veya dakika:saniye (1:30.5).", Time.Pretty(info.Duration)),
                    new TextField("start", "Başlangıç", "0:00"), new TextField("end", "Bitiş", Time.Pretty(info.Duration)));
            }
            case "av:speed":
                return await ParamDialog.Ask("Hız", "Ses perdesi korunur.", new ChoiceField("factor", "Hız çarpanı", ["0.25x", "0.5x", "0.75x", "1.25x", "1.5x", "2x", "3x", "4x"], 5));
            case "av:frame":
            {
                var info = await Ffmpeg.ProbeAsync(first);
                return await ParamDialog.Ask("Kare al", L.F("Süre: {0}.", Time.Pretty(info.Duration)), new TextField("time", "Zaman", Time.Pretty(info.Duration / 2)));
            }
            case "av:split":
                return await ParamDialog.Ask("Böl", "Kesim anahtar karelerde yapılır (yeniden kodlama yok, hızlı).", new TextField("seconds", "Parça uzunluğu (saniye veya dk:sn)", "60"));
            case "av:crop" or "av:redact":
            {
                bool crop = action.Id == "av:crop";
                var region = await RegionPicker.PickVideo(first, crop ? "Kırpılacak alanı seçin" : "Bulanıklaştırılacak alanı seçin",
                    crop ? "Fareyle sürükleyerek tutulacak alanı çizin." : "Fareyle sürükleyerek gizlenecek alanı (yüz, plaka, yazı) çizin.");
                return region is { } r ? new() { ["x"] = r.X.ToString(), ["y"] = r.Y.ToString(), ["w"] = r.Width.ToString(), ["h"] = r.Height.ToString() } : null;
            }
            case "av:channels":
                return await ParamDialog.Ask("Kanallar", null, new ChoiceField("mode", "Çıktı", ["Mono", "Stereo", "Sol/sağ değiştir", "Sadece sol", "Sadece sağ"]));
            case "av:bleep":
            {
                var info = await Ffmpeg.ProbeAsync(first);
                return await ParamDialog.Ask("Bip ile sansürle", L.F("Süre: {0}. Virgülle ayrılmış aralıklar: 1.5-2.3, 1:10-1:12", Time.Pretty(info.Duration)), new TextField("ranges", "Aralıklar", ""));
            }
            case "tr:translate":
            {
                var (fromIdx, toIdx) = await TranslateTools.DefaultsAsync(first);
                var answer = await ParamDialog.Ask("Çevir (çevrimdışı)", null,
                    new ChoiceField("from", "Kaynak dil", TranslateTools.LanguageOptions, Math.Max(0, fromIdx)),
                    new ChoiceField("to", "Hedef dil", TranslateTools.LanguageOptions, Math.Max(0, toIdx)));
                if (answer == null) return null;
                string from = TranslateTools.CodeOf(answer["from"]), to = TranslateTools.CodeOf(answer["to"]);
                string? notice = TranslateTools.DownloadNotice(from, to);
                if (notice != null && !await Confirm.Ask(notice)) return null;
                return new() { ["from"] = from, ["to"] = to };
            }
            case "pdf:ocr" when inputs.Count == 1:
            {
                int count = PdfTools.PageCount(first);
                return await ParamDialog.Ask("OCR: hangi sayfalar?", L.F("Bu PDF {0} sayfa.", count),
                    new ChoiceField("range", "Sayfalar", ["Tümü", "Belirli sayfalar"]),
                    new TextField("pages", "Sayfa numaraları", count > 1 ? $"1-{Math.Min(count, 20)}" : "1", "Örnek: 1-20, 35, 40-45", EnabledWhen: "range=Belirli sayfalar"),
                    new ChoiceField("text", "Kaydet", ["Yalnızca PDF", "PDF + metin (.txt)"]));
            }
            case "pdf:organize":
            {
                int count = PdfTools.PageCount(first);
                return await ParamDialog.Ask("PDF'i düzenle", L.F("{0} sayfa. Yazdığınız sırayla yeni PDF oluşur; yazmadığınız sayfalar silinir. Örnek: 3,1,2,5-7 veya {0}-1 (ters).", count),
                    new TextField("pages", "Sayfalar", count > 1 ? $"1-{count}" : "1"), new ChoiceField("rotate", "Döndür", ["Yok", "90° sağa", "90° sola", "180°"]));
            }
            case "act:copy" or "act:move":
            {
                string? target = await Pickers.Folder(L.T(action.Id == "act:copy" ? "Nereye kopyalansın?" : "Nereye taşınsın?"), ctx.OutputDir);
                return target != null ? new() { ["target"] = target } : null;
            }
            default:
                return [];
        }
    }
}

public abstract record Field(string Key, string Label);
/// <param name="EnabledWhen">"choiceKey=value": the box is only editable while that option is picked.</param>
public sealed record TextField(string Key, string Label, string Default, string? Hint = null, string? EnabledWhen = null) : Field(Key, Label);
public sealed record ChoiceField(string Key, string Label, string[] Options, int Selected = 0) : Field(Key, Label);

/// <summary>Parameter dialog; option texts are translated for display, the internal (Turkish) value is returned.</summary>
public static class ParamDialog
{
    public static Task<Dictionary<string, string>?> Ask(string title, string? description, params Field[] fields)
    {
        var tcs = new TaskCompletionSource<Dictionary<string, string>?>();
        var readers = new Dictionary<string, Func<string>>();
        var choices = new Dictionary<string, List<RadioButton>>();
        var w = new Window { Title = "GKR Studio — " + L.T(title), Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, Icon = Assets.Icon() };
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = L.T(title), FontSize = 16, FontWeight = FontWeight.SemiBold });
        if (description != null) panel.Children.Add(new TextBlock { Text = L.T(description), TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 8) });
        Control? focus = null;
        foreach (var field in fields)
        {
            var label = new TextBlock { Text = L.T(field.Label), Margin = new Thickness(0, 6, 0, 0) };
            panel.Children.Add(label);
            switch (field)
            {
                case TextField t:
                {
                    var box = new TextBox { Text = t.Default };
                    readers[t.Key] = () => (box.Text ?? "").Trim();
                    panel.Children.Add(box);
                    TextBlock? hint = null;
                    if (t.Hint != null)
                        panel.Children.Add(hint = new TextBlock { Text = L.T(t.Hint), FontSize = 11, Opacity = 0.65 });
                    if (t.EnabledWhen?.Split('=', 2) is [var key, var value] && choices.TryGetValue(key, out var group))
                    {
                        // shown only while that option is picked
                        var target = group.First(r => (string)r.Tag! == value);
                        void Sync()
                        {
                            bool on = target.IsChecked == true;
                            label.IsVisible = box.IsVisible = on;
                            if (hint != null) hint.IsVisible = on;
                            if (on) { box.Focus(); box.SelectAll(); }
                        }
                        group.ForEach(r => r.IsCheckedChanged += (_, _) => Sync());
                        Sync();
                    }
                    else focus ??= box;
                    break;
                }
                case ChoiceField c:
                {
                    var wrap = new WrapPanel();
                    var radios = c.Options.Select((o, i) => new RadioButton { Content = L.T(o), Tag = o, IsChecked = i == c.Selected, GroupName = c.Key + title, Margin = new Thickness(0, 0, 14, 2) }).ToList();
                    radios.ForEach(r => wrap.Children.Add(r));
                    choices[c.Key] = radios;
                    readers[c.Key] = () => (string)radios.First(r => r.IsChecked == true).Tag!;
                    panel.Children.Add(wrap);
                    break;
                }
            }
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = L.T("Başlat"), IsDefault = true };
        ok.Classes.Add("accent");
        var cancel = new Button { Content = L.T("İptal"), IsCancel = true };
        ok.Click += (_, _) => { tcs.TrySetResult(readers.ToDictionary(kv => kv.Key, kv => kv.Value())); w.Close(); };
        cancel.Click += (_, _) => w.Close();
        row.Children.Add(ok);
        row.Children.Add(cancel);
        panel.Children.Add(row);
        w.Content = panel;
        w.Closed += (_, _) => tcs.TrySetResult(null);
        w.Opened += (_, _) => { w.Activate(); focus?.Focus(); };
        w.Show();
        return tcs.Task;
    }
}

/// <summary>Shows a still image and lets the user drag a rectangle (returned in image pixels).</summary>
public static class RegionPicker
{
    public static Task<PixelRect?> PickImage(string path, string title, string hint)
    {
        using var bmp = ImageTools.Load(path);
        using var img = SkiaSharp.SKImage.FromBitmap(bmp);
        using var data = img.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        return Pick(new Bitmap(new MemoryStream(data.ToArray())), title, hint);
    }

    public static async Task<PixelRect?> PickVideo(string video, string title, string hint)
    {
        var info = await Ffmpeg.ProbeAsync(video);
        string temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gkrstudio-frame-{Guid.NewGuid():N}.png");
        try
        {
            await Ffmpeg.RunAsync(["-ss", Time.Arg(info.Duration * 0.1), "-i", video, "-frames:v", "1", "-update", "1", temp], _ => { }, _ => { }, default);
            return await Pick(new Bitmap(temp), title, hint);
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    private static Task<PixelRect?> Pick(Bitmap frame, string title, string hint)
    {
        var tcs = new TaskCompletionSource<PixelRect?>();
        int fw = frame.PixelSize.Width, fh = frame.PixelSize.Height;
        var image = new Image { Source = frame, Width = fw, Height = fh, Stretch = Stretch.Fill };
        var selection = new Rectangle { Stroke = Brushes.White, StrokeThickness = Math.Max(2, fw / 400.0), StrokeDashArray = [4, 3], Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x5B, 0x8C, 0xFF)), IsVisible = false };
        var overlay = new Canvas { Width = fw, Height = fh, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Cross) };
        overlay.Children.Add(selection);
        var stack = new Grid();
        stack.Children.Add(image);
        stack.Children.Add(overlay);
        PixelRect? result = null;
        Point start = default;
        bool dragging = false;
        var ok = new Button { Content = L.T("Uygula"), IsEnabled = false };
        ok.Classes.Add("accent");
        Point Clamp(Point p) => new(Math.Clamp(p.X, 0, fw), Math.Clamp(p.Y, 0, fh));
        overlay.PointerPressed += (_, e) => { start = Clamp(e.GetPosition(overlay)); dragging = true; selection.IsVisible = true; };
        overlay.PointerMoved += (_, e) =>
        {
            if (!dragging) return;
            var p = Clamp(e.GetPosition(overlay));
            double x = Math.Min(start.X, p.X), y = Math.Min(start.Y, p.Y), w = Math.Abs(start.X - p.X), h = Math.Abs(start.Y - p.Y);
            Canvas.SetLeft(selection, x); Canvas.SetTop(selection, y); selection.Width = w; selection.Height = h;
            result = new PixelRect((int)x, (int)y, (int)w, (int)h);
            ok.IsEnabled = w >= 16 && h >= 16;
        };
        overlay.PointerReleased += (_, _) => dragging = false;

        var win = new Window { Title = "GKR Studio — " + L.T(title), Width = 960, Height = 680, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, Icon = Assets.Icon() };
        var cancel = new Button { Content = L.T("İptal") };
        ok.Click += (_, _) => { tcs.TrySetResult(result); win.Close(); };
        cancel.Click += (_, _) => win.Close();
        var bar = new DockPanel { Margin = new Thickness(14, 10, 14, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(new TextBlock { Text = L.T(hint), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 });
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Viewbox { Child = stack, Margin = new Thickness(14), Stretch = Stretch.Uniform });
        win.Content = root;
        win.Closed += (_, _) => tcs.TrySetResult(null);
        win.Show();
        return tcs.Task;
    }
}

/// <summary>Progress for one job: status, bar, details, reveal in Finder.</summary>
public sealed class ProgressWindow : Window
{
    private readonly CancellationTokenSource cts = new();
    private readonly TextBlock status = new() { Text = L.T("Hazırlanıyor…"), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly ProgressBar bar = new() { Minimum = 0, Maximum = 1, IsIndeterminate = true, Height = 8 };
    private readonly TextBlock etaText = new() { FontSize = 12, Opacity = 0.7 };
    private readonly TextBox log = new() { IsReadOnly = true, Height = 160, AcceptsReturn = true, FontFamily = new FontFamily("Menlo, Consolas, monospace"), FontSize = 11, TextWrapping = TextWrapping.NoWrap };
    private readonly Button reveal = new() { Content = L.T("Klasörde göster"), IsVisible = false };
    private readonly Button cancel = new() { Content = L.T("İptal") };
    private string? result;
    private bool running;

    public ProgressWindow(Job job)
    {
        Title = "GKR Studio";
        Icon = Assets.Icon();
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;
        reveal.Classes.Add("accent");
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = job.Action.Title + (job.Inputs.Count > 0 ? " — " + L.F("{0} dosya", job.Inputs.Count) : ""), FontSize = 15, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(status);
        panel.Children.Add(bar);
        panel.Children.Add(etaText);
        panel.Children.Add(new Expander { Header = L.T("Ayrıntılar"), Content = log, HorizontalAlignment = HorizontalAlignment.Stretch });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        row.Children.Add(reveal);
        row.Children.Add(cancel);
        panel.Children.Add(row);
        Content = panel;
        reveal.Click += (_, _) => { if (result != null) AppHost.Open(result, reveal: File.Exists(result)); Close(); };
        cancel.Click += (_, _) => { if (running) cts.Cancel(); else Close(); };
        Closing += (_, _) => { if (running) cts.Cancel(); };
        Opened += async (_, _) => await RunAsync(job);
    }

    private async Task RunAsync(Job job)
    {
        running = true;
        var eta = new Eta();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => etaText.Text = eta.Text();
        timer.Start();
        var progress = new Progress<ProgressInfo>(p =>
        {
            status.Text = L.Msg(p.Status);
            bar.IsIndeterminate = p.Fraction == null;
            if (p.Fraction is double f) { bar.Value = f; eta.Report(f); etaText.Text = eta.Text(); }
        });
        void AppendLog(string line) => Dispatcher.UIThread.Post(() =>
        {
            log.Text = (log.Text ?? "") + L.Msg(line) + "\n";
            log.CaretIndex = log.Text.Length;
        });
        try
        {
            result = await Task.Run(() => ActionRunner.RunAsync(job, progress, AppendLog, cts.Token));
            bar.IsIndeterminate = false;
            bar.Value = 1;
            status.Text = L.T("Tamamlandı ✔");
            reveal.IsVisible = result != null;
        }
        catch (OperationCanceledException) { status.Text = L.T("İptal edildi."); }
        catch (Exception ex)
        {
            bar.IsIndeterminate = false;
            status.Text = L.T("Hata: ") + L.Msg(ex is AggregateException { InnerExceptions.Count: 1 } ag ? ag.InnerException!.Message : ex.Message);
            status.Foreground = Brushes.IndianRed;
            AppendLog(ex.ToString());
        }
        finally
        {
            timer.Stop();
            etaText.Text = "";
            running = false;
            cancel.Content = L.T("Kapat");
        }
    }
}

public static class Toast
{
    public static void Show(string text)
    {
        var w = new Window
        {
            SystemDecorations = SystemDecorations.None, TransparencyLevelHint = [WindowTransparencyLevel.Transparent], Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, SizeToContent = SizeToContent.WidthAndHeight, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#1F232B")), CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 10),
                Child = new TextBlock { Text = "✦  " + text, Foreground = Brushes.White, FontSize = 14 },
            },
        };
        w.Show();
        DispatcherTimer.RunOnce(() => { w.Close(); AppHost.ShutdownIfIdle(); }, TimeSpan.FromSeconds(1.4));
    }
}

/// <summary>Cheat sheet (3× tap on a modifier, or from the menu bar).</summary>
public sealed class ShortcutsWindow : Window
{
    private static ShortcutsWindow? current;
    private bool closing;

    public static void Toggle()
    {
        if (current != null) { current.SafeClose(); return; }
        current = new ShortcutsWindow();
        current.Closed += (_, _) => { current = null; AppHost.ShutdownIfIdle(); };
        current.Show();
        current.Activate();
    }

    private ShortcutsWindow()
    {
        var p = Preferences.Load();
        string Wheel(string m) => m == "Kapalı" ? L.T("kapalı") : L.F("{0} + sağ tık", Settings.ModifierName(m));
        var rows = new (string, string)[]
        {
            (Wheel(p.ConvertModifier), "Dönüştür tekerleği"), (Wheel(p.ToolsModifier), "Araçlar tekerleği"), (Wheel(p.ActionsModifier), "İşlemler tekerleği"),
            ("Finder → " + L.T("Hızlı İşlemler") + " → GKR Studio", "Hepsi bir arada tekerlek"),
            ("⌃ Control + ⌥ Option + L", "Panodaki linki indir"), ("3× ⌃ / ⌥ / ⇧", "Bu kısayol penceresi"),
            (L.T("Sol tık"), "Seç"), ("1 – 9", "Hızlı seçim"), (L.T("Tuşu bırak"), "Kapat (tuşla açıldıysa)"), ("Esc", "Kapat / geri"),
        };
        Title = "GKR Studio — " + L.T("Kısayollar");
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,18,Auto") };
        for (int i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var key = new Border { Background = new SolidColorBrush(Color.Parse("#272C36")), CornerRadius = new CornerRadius(5), Padding = new Thickness(8, 3), Margin = new Thickness(0, 3), HorizontalAlignment = HorizontalAlignment.Left, Child = new TextBlock { Text = rows[i].Item1, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold } };
            var desc = new TextBlock { Text = L.T(rows[i].Item2), Foreground = new SolidColorBrush(Color.Parse("#9AA3B2")), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(key, i); Grid.SetRow(desc, i); Grid.SetColumn(desc, 2);
            grid.Children.Add(key); grid.Children.Add(desc);
        }
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(new TextBlock { Text = L.T("GKR Studio kısayolları"), FontSize = 17, FontWeight = FontWeight.Bold, Foreground = Brushes.White });
        stack.Children.Add(grid);
        Content = new Border { Background = new SolidColorBrush(Color.Parse("#1F232B")), CornerRadius = new CornerRadius(14), Padding = new Thickness(24, 20), Margin = new Thickness(20), Child = stack };
        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter or Key.Space) SafeClose(); };
        PointerReleased += (_, _) => SafeClose();
        Deactivated += (_, _) => SafeClose();
        Closing += (_, _) => closing = true;
    }

    private void SafeClose()
    {
        if (closing) return;
        closing = true;
        Dispatcher.UIThread.Post(Close);
    }
}

/// <summary>Floating file shelf (double-click shows the file in Finder).</summary>
public sealed class FloatShelf : Window
{
    private static FloatShelf? current;
    private readonly ListBox list = new();

    public static void Add(IEnumerable<string> paths)
    {
        if (current == null)
        {
            current = new FloatShelf();
            current.Closed += (_, _) => { current = null; AppHost.ShutdownIfIdle(); };
            current.Show();
        }
        foreach (var p in paths)
            if (!current.list.Items.OfType<ListBoxItem>().Any(i => (string)i.Tag! == p))
                current.list.Items.Add(new ListBoxItem { Content = (Directory.Exists(p) ? "📁  " : "📄  ") + System.IO.Path.GetFileName(p.TrimEnd('/')), Tag = p });
        current.Activate();
    }

    private FloatShelf()
    {
        Title = L.T("GKR Studio — Raf");
        Icon = Assets.Icon();
        Width = 300; Height = 380; Topmost = true;
        list.DoubleTapped += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: string p }) AppHost.Open(p, reveal: true); };
        list.KeyDown += (_, e) => { if (e.Key is Key.Delete or Key.Back && list.SelectedItem != null) list.Items.Remove(list.SelectedItem); };
        var clear = new Button { Content = L.T("Rafı temizle"), Margin = new Thickness(10) };
        clear.Click += (_, _) => list.Items.Clear();
        var root = new DockPanel();
        DockPanel.SetDock(clear, Dock.Bottom);
        root.Children.Add(clear);
        root.Children.Add(list);
        Content = root;
    }
}

/// <summary>macOS UI around the shared Updater.</summary>
public static class UpdateFlow
{
    private static bool busy;

    public static async Task CheckAsync(bool silent)
    {
        if (busy) return;
        busy = true;
        try
        {
            string feed = Updater.FeedFrom(Preferences.Load());
            if (string.IsNullOrWhiteSpace(feed)) { if (!silent) await Ask.Info(L.T("Güncelleme adresi ayarlanmamış.")); return; }
            UpdateInfo? info;
            try { info = await Updater.CheckAsync(feed); }
            catch (Exception ex) { if (!silent) await Ask.Info(L.T("Güncelleme denetlenemedi: ") + ex.Message); return; }
            if (info == null) { if (!silent) await Ask.Info(L.F("GKR Studio güncel (sürüm {0}).", Updater.Current)); return; }
            string text = L.F("Yeni sürüm hazır: {0} (şu an {1}).", info.Version, Updater.Current) + (info.Notes.Length > 0 ? "\n\n" + info.Notes : "") + "\n\n" + L.T("Şimdi indirip kurulsun mu? Uygulama yeniden başlayacak.");
            if (!await Ask.YesNo(text)) return;
            Toast.Show(L.F("Güncelleme indiriliyor ({0})…", info.Version));
            try
            {
                string zip = await Updater.DownloadAsync(info, null);
                Updater.InstallAndRestart(zip, tray: AppHost.Resident);
                AppHost.Resident = false;
                AppHost.Exit();
            }
            catch (Exception ex) { await Ask.Info(L.T("Güncelleme kurulamadı: ") + ex.Message); }
        }
        finally { busy = false; }
    }
}

/// <summary>Small Yes / No question window.</summary>
public static class Confirm
{
    public static Task<bool> Ask(string text)
    {
        var tcs = new TaskCompletionSource<bool>();
        var w = new Window { Title = "GKR Studio", Width = 440, SizeToContent = SizeToContent.Height, CanResize = false, Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen, Icon = Assets.Icon() };
        var panel = new StackPanel { Margin = new Thickness(22), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        var yes = new Button { Content = L.T("Evet"), IsDefault = true };
        yes.Classes.Add("accent");
        var no = new Button { Content = L.T("Hayır"), IsCancel = true };
        yes.Click += (_, _) => { tcs.TrySetResult(true); w.Close(); };
        no.Click += (_, _) => w.Close();
        row.Children.Add(yes);
        row.Children.Add(no);
        panel.Children.Add(row);
        w.Content = panel;
        w.Closed += (_, _) => tcs.TrySetResult(false);
        w.Show();
        w.Activate();
        return tcs.Task;
    }
}
