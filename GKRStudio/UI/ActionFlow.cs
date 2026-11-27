using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using GKRStudio.Core;
using Microsoft.Win32;

namespace GKRStudio.UI;

/// <summary>Everything a chosen wheel entry needs, captured when the wheel was opened.</summary>
public sealed record WheelContext(IReadOnlyList<string> Inputs, IReadOnlyList<string> AllPaths, string OutputDir, LinkInfo? Link);

/// <summary>What happens after a wheel entry is chosen: ask parameters (if any), then run.</summary>
public static class ActionFlow
{
    public static async void Run(MenuAction action, WheelContext ctx)
    {
        try
        {
            switch (action.Id)
            {
                case Catalog.OpenSettings:
                    App.ShowSettings();
                    return;
                case Catalog.PickFiles:
                    PickAndShow(ctx.OutputDir);
                    return;
                case "img:markup" or "img:crop" or "img:redact":
                    var editor = new ImageEditorWindow(ctx.Inputs[0], cropMode: action.Id == "img:crop", redactMode: action.Id == "img:redact");
                    editor.Closed += (_, _) => Launcher.ShutdownIfIdle();
                    editor.Show();
                    editor.Activate();
                    return;
                case "act:share":
                    ShareHelper.Share(ctx.AllPaths);
                    return;
                case "act:float":
                    FloatShelf.Add(ctx.AllPaths);
                    return;
                case "act:copypath":
                    Clipboard.SetText(string.Join(Environment.NewLine, ctx.AllPaths));
                    Toast(L.T("Yol panoya kopyalandı"));
                    return;
                case "act:terminal":
                    OpenTerminal(ctx.OutputDir);
                    Launcher.ShutdownIfIdle();
                    return;
                case "act:reveal":
                    var first = ctx.AllPaths.FirstOrDefault();
                    var psi = new ProcessStartInfo("explorer.exe");
                    if (first != null) { psi.ArgumentList.Add("/select,"); psi.ArgumentList.Add(first); }
                    else psi.ArgumentList.Add(ctx.OutputDir);
                    Process.Start(psi);
                    Launcher.ShutdownIfIdle();
                    return;
            }

            if (!Launcher.CheckTools(action)) return;

            var inputs = action.Id is "zip:create" or "act:copy" or "act:move" ? ctx.AllPaths : ctx.Inputs;
            var options = await AskAsync(action, inputs, ctx);
            if (options == null) { Launcher.ShutdownIfIdle(); return; }
            Launcher.Start(new Job(action, inputs, ctx.Link, ctx.OutputDir, options), owner: null);
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "GKR Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Launcher.ShutdownIfIdle();
        }
    }

    /// <summary>Returns the options for the action, an empty dictionary when nothing is needed, or null when cancelled.</summary>
    private static async Task<Dictionary<string, string>?> AskAsync(MenuAction action, IReadOnlyList<string> inputs, WheelContext ctx)
    {
        string first = inputs.FirstOrDefault() ?? "";
        switch (action.Id)
        {
            case "dl:video":
                return ParamDialog.Ask("Video kalitesi", "1080p ve altı H.264 olarak iner ve her oynatıcıda açılır. Seçtiğiniz çözünürlük videoda yoksa altındaki en iyisi indirilir.",
                    new ChoiceField("quality", "Çözünürlük", VideoQualities, 0));
            case "dl:audio":
                return ParamDialog.Ask("MP3 kalitesi", "Yüksek bit hızı = daha iyi ses, daha büyük dosya.",
                    new ChoiceField("quality", "Bit hızı", AudioQualities, 0));
            case "img:adjust":
            {
                string size = "";
                try { var b = ImageTools.Load(first); size = L.F("Orijinal: {0} × {1} px. ", b.PixelWidth, b.PixelHeight); } catch { }
                return ParamDialog.Ask("Fotoğrafı düzenle", size + L.T("Renk ayarları -100 ile 100 arası; 0 = değişiklik yok."),
                    new TextField("percent", "Boyut (%)", "100"),
                    new ChoiceField("rotate", "Döndür", ["Yok", "90° sağa", "90° sola", "180°"]),
                    new ChoiceField("flip", "Yansıt", ["Hayır", "Yatay"]),
                    new TextField("brightness", "Parlaklık", "0"),
                    new TextField("contrast", "Kontrast", "0"),
                    new TextField("saturation", "Doygunluk", "0"));
            }
            case "img:frame":
                return ParamDialog.Ask("Arka plan ekle", "Kenar boşluğu ve köşe, görselin kısa kenarına göre yüzde.",
                    new TextField("padding", "Kenar boşluğu (%)", "8"),
                    new ChoiceField("background", "Arka plan", ["Beyaz", "Siyah", "Gri", "Mavi degrade", "Gün batımı", "Şeffaf"], 3),
                    new TextField("radius", "Köşe yuvarlaklığı (%)", "4"),
                    new ChoiceField("shadow", "Gölge", ["Var", "Yok"]));
            case "img:redact":
            {
                var bmp = ImageTools.Load(first);
                var region = RegionPickerWindow.Pick(bmp, "Gizlenecek alanı seçin", "Fareyle sürükleyerek yüz, plaka veya yazıyı çizin.");
                if (region is not { } r) return null;
                var style = ParamDialog.Ask("Sansür biçimi", null, new ChoiceField("style", "Biçim", ["Bulanık", "Piksel", "Siyah"]));
                if (style == null) return null;
                style["x"] = r.X.ToString(); style["y"] = r.Y.ToString(); style["w"] = r.Width.ToString(); style["h"] = r.Height.ToString();
                return style;
            }
            case "img:resize":
            {
                string size = "";
                try { var b = ImageTools.Load(first); size = L.F("Orijinal: {0} × {1} px. ", b.PixelWidth, b.PixelHeight); } catch { }
                return ParamDialog.Ask("Boyutlandır", size + L.T("Genişlik veya yükseklik girerseniz oran korunur; ikisi de boşsa yüzde kullanılır."),
                    new TextField("percent", "Yüzde (%)", "50"),
                    new TextField("width", "Genişlik (px)", ""),
                    new TextField("height", "Yükseklik (px)", ""));
            }
            case "img:rotate":
                return ParamDialog.Ask("Döndür", null, new ChoiceField("angle", "Yön", ["90° sağa", "90° sola", "180°"]));
            case "img:jpeg":
                return ParamDialog.Ask("JPEG sıkıştır", "Düşük kalite = küçük dosya. 70-80 çoğu fotoğraf için iyi bir denge.",
                    new TextField("quality", "Kalite (1-100)", "75"));

            case "av:trim":
            {
                var info = await Ffmpeg.ProbeAsync(first);
                return ParamDialog.Ask("Kes", L.F("Süre: {0}. Biçim: saniye (90) veya dakika:saniye (1:30.5).", Time.Pretty(info.Duration)),
                    new TextField("start", "Başlangıç", "0:00"),
                    new TextField("end", "Bitiş", Time.Pretty(info.Duration)));
            }
            case "av:speed":
                return ParamDialog.Ask("Hız", "Ses perdesi korunur.",
                    new ChoiceField("factor", "Hız çarpanı", ["0.25x", "0.5x", "0.75x", "1.25x", "1.5x", "2x", "3x", "4x"], 5));
            case "av:frame":
            {
                var info = await Ffmpeg.ProbeAsync(first);
                return ParamDialog.Ask("Kare al", L.F("Süre: {0}.", Time.Pretty(info.Duration)), new TextField("time", "Zaman", Time.Pretty(info.Duration / 2)));
            }
            case "av:split":
                return ParamDialog.Ask("Böl", "Kesim anahtar karelerde yapılır (yeniden kodlama yok, hızlı).",
                    new TextField("seconds", "Parça uzunluğu (saniye veya dk:sn)", "60"));
            case "av:crop" or "av:redact":
            {
                bool crop = action.Id == "av:crop";
                var region = await PickRegionAsync(first, crop ? "Kırpılacak alanı seçin" : "Bulanıklaştırılacak alanı seçin",
                    crop ? "Fareyle sürükleyerek tutulacak alanı çizin." : "Fareyle sürükleyerek gizlenecek alanı (yüz, plaka, yazı) çizin.");
                return region is { } r ? new() { ["x"] = r.X.ToString(), ["y"] = r.Y.ToString(), ["w"] = r.Width.ToString(), ["h"] = r.Height.ToString() } : null;
            }
            case "av:channels":
                return ParamDialog.Ask("Kanallar", null,
                    new ChoiceField("mode", "Çıktı", ["Mono", "Stereo", "Sol/sağ değiştir", "Sadece sol", "Sadece sağ"]));
            case "av:bleep":
            {
                var info = await Ffmpeg.ProbeAsync(first);
                return ParamDialog.Ask("Bip ile sansürle", L.F("Süre: {0}. Virgülle ayrılmış aralıklar: 1.5-2.3, 1:10-1:12", Time.Pretty(info.Duration)),
                    new TextField("ranges", "Aralıklar", ""));
            }
            case "tr:translate":
            {
                var (fromIdx, toIdx) = await TranslateTools.DefaultsAsync(first);
                var answer = ParamDialog.Ask("Çevir (çevrimdışı)", null,
                    new ChoiceField("from", "Kaynak dil", TranslateTools.LanguageOptions, Math.Max(0, fromIdx)),
                    new ChoiceField("to", "Hedef dil", TranslateTools.LanguageOptions, Math.Max(0, toIdx)));
                if (answer == null) return null;
                string from = TranslateTools.CodeOf(answer["from"]), to = TranslateTools.CodeOf(answer["to"]);
                string? notice = TranslateTools.DownloadNotice(from, to); // throws for impossible pairs → shown as an error
                if (notice != null && MessageBox.Show(notice, "GKR Studio", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return null;
                return new() { ["from"] = from, ["to"] = to };
            }
            case "pdf:ocr" or "pdf:ocrclean" when inputs.Count == 1:
            {
                int count = PdfTools.PageCount(first);
                return ParamDialog.Ask("OCR: hangi sayfalar?", L.F("Bu PDF {0} sayfa.", count),
                    new ChoiceField("range", "Sayfalar", ["Tümü", "Belirli sayfalar"]),
                    new TextField("pages", "Sayfa numaraları", count > 1 ? $"1-{Math.Min(count, 20)}" : "1", "Örnek: 1-20, 35, 40-45", EnabledWhen: "range=Belirli sayfalar"),
                    new ChoiceField("text", "Kaydet", ["Yalnızca PDF", "PDF + metin (.txt)"]));
            }
            case "pdf:pages" or "pdf:organize":
            {
                int count = PdfTools.PageCount(first);
                return ParamDialog.Ask("PDF'i düzenle", L.F("{0} sayfa. Yazdığınız sırayla yeni PDF oluşur; yazmadığınız sayfalar silinir. Örnek: 3,1,2,5-7 veya {0}-1 (ters).", count),
                    new TextField("pages", "Sayfalar", count > 1 ? $"1-{count}" : "1"),
                    new ChoiceField("rotate", "Döndür", ["Yok", "90° sağa", "90° sola", "180°"]));
            }
            case "act:copy" or "act:move":
            {
                var dialog = new OpenFolderDialog
                {
                    Title = L.T(action.Id == "act:copy" ? "Nereye kopyalansın?" : "Nereye taşınsın?"),
                    InitialDirectory = ctx.OutputDir,
                };
                return dialog.ShowDialog() == true ? new() { ["target"] = dialog.FolderName } : null;
            }
            default:
                return [];
        }
    }

    public static readonly string[] VideoQualities = ["1080p", "720p", "480p", "360p", "En yüksek (4K — bazı oynatıcılarda açılmayabilir)"];
    public static readonly string[] AudioQualities = ["320 kbps", "256 kbps", "192 kbps", "128 kbps"];

    private static async Task<Int32Rect?> PickRegionAsync(string video, string title, string hint)
    {
        var info = await Ffmpeg.ProbeAsync(video);
        string temp = Path.Combine(Path.GetTempPath(), $"gkrstudio-frame-{Guid.NewGuid():N}.png");
        try
        {
            await Ffmpeg.RunAsync(["-ss", Time.Arg(info.Duration * 0.1), "-i", video, "-frames:v", "1", "-update", "1", temp], _ => { }, _ => { }, default);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(temp);
            bmp.EndInit();
            bmp.Freeze();
            return RegionPickerWindow.Pick(bmp, title, hint);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    private static void PickAndShow(string directory)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            InitialDirectory = directory,
            Title = L.T("GKR Studio — dosya seçin"),
            Filter = "Desteklenen dosyalar|" + string.Join(";", Catalog.AllExtensions.Select(x => "*" + x)) + "|Tüm dosyalar|*.*",
        };
        if (dialog.ShowDialog() == true && dialog.FileNames.Length > 0) MenuHost.ShowForFiles(dialog.FileNames);
        else Launcher.ShutdownIfIdle();
    }

    private static void OpenTerminal(string dir)
    {
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe") { ArgumentList = { "-d", dir }, UseShellExecute = true });
        }
        catch (Exception)
        {
            Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = dir, UseShellExecute = true });
        }
    }

    private static void Toast(string text)
    {
        var w = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, SizeToContent = SizeToContent.WidthAndHeight, ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new System.Windows.Controls.Border
            {
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("Card"),
                BorderBrush = (System.Windows.Media.Brush)Application.Current.FindResource("Accent"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(18, 10, 18, 10),
                Child = new System.Windows.Controls.TextBlock { Text = "✦  " + text, Foreground = System.Windows.Media.Brushes.White, FontSize = 14 },
            },
        };
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        timer.Tick += (_, _) => { timer.Stop(); w.Close(); Launcher.ShutdownIfIdle(); };
        w.Show();
        timer.Start();
    }
}
