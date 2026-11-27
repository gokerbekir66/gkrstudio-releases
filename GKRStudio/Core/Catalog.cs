using System.IO;

namespace GKRStudio.Core;

public enum InputKind { None, Image, Video, Audio, Pdf, Document, Spreadsheet, Presentation, Epub, Subtitle, Archive, Mixed, Unsupported }

public enum LinkKind { Image, YouTube, Media }

public sealed record LinkInfo(string Url, LinkKind Kind);

/// <summary>A wheel entry. Pure data: wheels are rebuilt from scratch on every invocation.
/// An entry with <see cref="Children"/> opens a sub-wheel instead of running.</summary>
public sealed record MenuAction(string Id, string Title, string Glyph, bool NeedsFfmpeg = false, bool NeedsYtDlp = false,
                                string? Short = null, IReadOnlyList<MenuAction>? Children = null)
{
    public string Label => Short ?? Title;
    public bool IsGroup => Children is { Count: > 0 };
}

/// <summary>
/// Stateless decision logic: given only the current input (file extensions or current clipboard link),
/// returns the actions to show. Three wheels: Dönüştür (Shift), Araçlar (Ctrl), İşlemler (Alt).
/// </summary>
public static class Catalog
{
    private static HashSet<string> Set(params string[] e) => new(e, StringComparer.OrdinalIgnoreCase);

    public static readonly HashSet<string> ImageExtensions = Set(".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic", ".heif", ".avif", ".ico", ".svg");
    public static readonly HashSet<string> VideoExtensions = Set(".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".mts", ".m2ts", ".3gp");
    public static readonly HashSet<string> AudioExtensions = Set(".mp3", ".m4a", ".aac", ".wav", ".flac", ".ogg", ".oga", ".opus", ".wma", ".aif", ".aiff", ".alac");
    public static readonly HashSet<string> DocumentExtensions = Set(".docx", ".doc", ".odt", ".rtf", ".txt", ".md", ".markdown", ".html", ".htm");
    public static readonly HashSet<string> SheetExtensions = Set(".xlsx", ".xls", ".ods", ".csv", ".tsv");
    public static readonly HashSet<string> SlideExtensions = Set(".pptx", ".ppt", ".odp");
    public static readonly HashSet<string> SubtitleExtensions = Set(".srt", ".vtt");
    public static readonly HashSet<string> ArchiveExtensions = Set(".zip", ".rar", ".tar", ".gz", ".tgz", ".7z");

    public static IEnumerable<string> AllExtensions =>
        ImageExtensions.Concat(VideoExtensions).Concat(AudioExtensions).Concat(DocumentExtensions).Concat(SheetExtensions)
            .Concat(SlideExtensions).Concat(SubtitleExtensions).Concat(ArchiveExtensions).Append(".pdf").Append(".epub");

    public static InputKind KindOf(string path)
    {
        string ext = Path.GetExtension(path);
        if (ImageExtensions.Contains(ext)) return InputKind.Image;
        if (VideoExtensions.Contains(ext)) return InputKind.Video;
        if (AudioExtensions.Contains(ext)) return InputKind.Audio;
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return InputKind.Pdf;
        if (ext.Equals(".epub", StringComparison.OrdinalIgnoreCase)) return InputKind.Epub;
        if (DocumentExtensions.Contains(ext)) return InputKind.Document;
        if (SheetExtensions.Contains(ext)) return InputKind.Spreadsheet;
        if (SlideExtensions.Contains(ext)) return InputKind.Presentation;
        if (SubtitleExtensions.Contains(ext)) return InputKind.Subtitle;
        if (ArchiveExtensions.Contains(ext)) return InputKind.Archive;
        return InputKind.Unsupported; // includes folders
    }

    public static InputKind Classify(IReadOnlyCollection<string> paths)
    {
        if (paths.Count == 0) return InputKind.None;
        var kinds = paths.Select(KindOf).Distinct().ToList();
        if (kinds.Count == 1) return kinds[0];
        return kinds.Contains(InputKind.Unsupported) && kinds.Count == 2 ? kinds.First(k => k != InputKind.Unsupported) : InputKind.Mixed;
    }

    private static MenuAction A(string id, string label, string title, bool ffmpeg = false) => new(id, L.T(title), "", ffmpeg, Short: L.T(label));
    private static MenuAction Zip => A("zip:create", "ZIP", "ZIP oluştur");

    public enum Mode { All, Convert, Tools, Actions }

    public static string ModeTitle(Mode mode) => mode switch
    {
        Mode.Convert => L.T("Dönüştür"),
        Mode.Tools => L.T("Araçlar"),
        Mode.Actions => L.T("İşlemler"),
        _ => "GKR Studio",
    };

    /// <summary>Shift: format conversions. <paramref name="sourceExt"/> (when all inputs share it) is left out.</summary>
    public static IReadOnlyList<MenuAction> Convert(InputKind kind, int count = 1, string? sourceExt = null)
    {
        IEnumerable<MenuAction> list = kind switch
        {
            InputKind.Image =>
            [
                A("img:png", "PNG", "PNG'ye dönüştür"), A("img:jpg", "JPG", "JPG'ye dönüştür"),
                A("img:webp", "WEBP", "WEBP'ye dönüştür", true), A("img:tiff", "TIFF", "TIFF'e dönüştür"),
                A("img:avif", "AVIF", "AVIF'e dönüştür", true), A("img:bmp", "BMP", "BMP'ye dönüştür"),
                A("img:gif", "GIF", "GIF'e dönüştür"), A("img:ico", "ICO", "Simge (ICO 256) oluştur"),
                A("img:pdf", "PDF", count > 1 ? "Görsellerden tek PDF" : "PDF olarak dışa aktar"),
            ],
            InputKind.Video =>
            [
                A("av:mp4", "MP4", "MP4'e dönüştür", true), A("av:mov", "MOV", "MOV'a dönüştür", true),
                A("av:mkv", "MKV", "MKV'ye dönüştür", true), A("av:webm", "WEBM", "WEBM'e dönüştür", true),
                A("av:avi", "AVI", "AVI'ye dönüştür", true), A("av:wmv", "WMV", "WMV'ye dönüştür", true),
                A("av:gif", "GIF", "GIF'e dönüştür", true), A("av:mp3", "MP3", "Sesi MP3 olarak çıkar", true),
            ],
            InputKind.Audio =>
            [
                A("av:mp3", "MP3", "MP3'e dönüştür", true), A("av:m4a", "M4A", "M4A'ya dönüştür", true),
                A("av:wav", "WAV", "WAV'a dönüştür", true), A("av:flac", "FLAC", "FLAC'a dönüştür", true),
                A("av:ogg", "OGG", "OGG'ye dönüştür", true), A("av:opus", "OPUS", "OPUS'a dönüştür", true),
                A("av:aiff", "AIFF", "AIFF'e dönüştür", true), A("av:wma", "WMA", "WMA'ya dönüştür", true),
            ],
            InputKind.Pdf =>
            [
                A("pdf:docx", "DOCX", "Word belgesi (DOCX)"), A("pdf:xlsx", "XLSX", "Tablo (XLSX)"),
                A("pdf:jpg", "JPG", "Sayfaları JPG yap"), A("pdf:png", "PNG", "Sayfaları PNG yap"),
                A("pdf:epub", "EPUB", "E-kitap (EPUB)"), A("pdf:txt", "TXT", "Düz metin (TXT)"), A("pdf:md", "MD", "Markdown (MD)"),
            ],
            InputKind.Document =>
            [
                A("doc:docx", "DOCX", "Word belgesi (DOCX)"), A("doc:pdf", "PDF", "PDF'e dönüştür"), A("doc:txt", "TXT", "Düz metin"),
                A("doc:html", "HTML", "Web sayfası (HTML)"), A("doc:epub", "EPUB", "E-kitap (EPUB)"),
                A("doc:png", "PNG", "Sayfaları PNG yap"), A("doc:jpg", "JPG", "Sayfaları JPG yap"), A("doc:md", "MD", "Markdown"),
            ],
            InputKind.Epub =>
            [
                A("doc:pdf", "PDF", "PDF'e dönüştür"), A("doc:docx", "DOCX", "Word belgesi (DOCX)"), A("doc:txt", "TXT", "Düz metin"),
                A("doc:md", "MD", "Markdown"), A("doc:html", "HTML", "Web sayfası (HTML)"),
            ],
            InputKind.Spreadsheet =>
            [
                A("sheet:xlsx", "XLSX", "Excel (XLSX)"), A("sheet:ods", "ODS", "OpenDocument (ODS)"),
                A("sheet:csv", "CSV", "CSV"), A("sheet:tsv", "TSV", "TSV"), A("sheet:pdf", "PDF", "PDF'e dönüştür"),
            ],
            InputKind.Presentation =>
            [
                A("slide:pdf", "PDF", "PDF'e dönüştür"), A("slide:jpg", "JPG", "Slaytları JPG yap"), A("slide:png", "PNG", "Slaytları PNG yap"),
            ],
            InputKind.Subtitle =>
            [
                A("sub:srt", "SRT", "SRT'ye dönüştür"), A("sub:vtt", "VTT", "VTT'ye dönüştür"), A("sub:txt", "TXT", "Sadece metin (TXT)"),
            ],
            InputKind.Archive =>
            [
                A("arc:zip", "ZIP", "ZIP'e dönüştür"), A("arc:tar", "TAR", "TAR'a dönüştür"), A("arc:gz", "GZ", "TAR.GZ'ye dönüştür"),
            ],
            _ => [],
        };
        if (sourceExt != null)
        {
            string ext = sourceExt.TrimStart('.').ToLowerInvariant() switch { "jpeg" or "jpe" or "jfif" => "jpg", "tif" => "tiff", "htm" => "html", "markdown" => "md", "tgz" => "gz", var e => e };
            // av:mp4 stays for MP4 sources: re-encoding to H.264 makes AV1/HEVC/VP9 MP4s playable everywhere.
            list = list.Where(a => a.Id == "av:mp4" || !a.Id.EndsWith(":" + ext, StringComparison.Ordinal))
                       .Select(a => a.Id == "av:mp4" && ext == "mp4" ? a with { Short = "MP4 (H.264)", Title = L.T("Uyumlu MP4'e dönüştür (H.264)") } : a);
        }
        return list.ToList();
    }

    /// <summary>Ctrl: tools.</summary>
    public static IReadOnlyList<MenuAction> Tools(InputKind kind, int count = 1)
    {
        var list = new List<MenuAction>();
        switch (kind)
        {
            case InputKind.Image:
                list.AddRange([
                    A("img:jpeg", "Sıkıştır", "Sıkıştır (JPEG kalite)"),
                    A("img:strip", "Metadata", "Metadata kaldır"),
                    A("img:adjust", "Fotoğrafı düzenle", "Boyut, döndür, yansıt, renk"),
                    A("img:markup", "İşaretle", "Kalem, şekil, ok, metin"),
                    A("img:frame", "Arka plan ekle", "Çerçeve / arka plan ekle"),
                    A("img:crop", "Kırp", "Görseli kırp"),
                    A("img:redact", "Sansürle", "Alanı gizle (blur / piksel / siyah)"),
                    A("img:qr", "QR oku", "QR kodlarını oku"),
                    A("img:ocr", "OCR", "Görseldeki metni oku (OCR)")]);
                break;
            case InputKind.Video:
                list.AddRange([
                    A("av:compress", "Sıkıştır", "Sıkıştır (küçük MP4)", true),
                    A("av:strip", "Metadata", "Metadata kaldır", true),
                    A("av:mute", "Sesi kaldır", "Sesi kaldır (sessiz video)", true),
                    A("av:trim", "Kes", "Videoyu kes (trim)", true),
                    A("av:crop", "Kırp", "Videoyu kırp", true),
                    A("av:speed", "Hız", "Video hızını değiştir", true),
                    A("av:frame", "Kare al", "Videodan kare al", true),
                    A("av:split", "Böl", "Videoyu böl", true),
                    A("av:redact", "Sansürle", "Videoyu sansürle", true)]);
                if (count > 1) list.Add(A("av:merge", "Birleştir", "Videoları birleştir", true));
                list.Add(A("av:loudnorm", "Ses eşitle", "Ses düzeyini eşitle", true));
                break;
            case InputKind.Audio:
                list.AddRange([
                    A("av:compress", "Sıkıştır", "Sıkıştır (AAC 96k)", true),
                    A("av:strip", "Metadata", "Metadata kaldır", true),
                    A("av:loudnorm", "Ses eşitle", "Ses düzeyini eşitle", true),
                    A("av:visualize", "Görselleştir", "Sesi görselleştir", true),
                    A("av:trim", "Kes", "Sesi kes", true),
                    A("av:channels", "Kanallar", "Ses kanallarını dönüştür", true),
                    A("av:bleep", "Bip", "Sese bip ekle", true)]);
                break;
            case InputKind.Pdf:
                list.AddRange([
                    A("pdf:compress", "Sıkıştır", "PDF'i sıkıştır"),
                    A("pdf:strip", "Metadata", "Metadata kaldır"),
                    A("pdf:split", "Böl", "PDF'i sayfalara böl"),
                    A("pdf:organize", "Düzenle", "Sayfaları sırala / sil / döndür"),
                    A("pdf:ocr", "OCR", "OCR: aranabilir PDF + metin"),
                    A("pdf:qr", "QR oku", "QR kodlarını oku")]);
                if (OperatingSystem.IsWindows()) list.Insert(list.Count - 1, A("pdf:ocrclean", "Yazıyı iyileştir", "OCR: bulanık yazıyı keskin yeniden yaz"));
                if (count > 1) list.Add(A("pdf:merge", "Birleştir", "PDF'leri birleştir"));
                break;
            case InputKind.Archive:
                list.Add(A("arc:extract", "Arşivi aç", "Arşivi güvenli aç"));
                break;
        }
        if (kind is InputKind.Document or InputKind.Epub or InputKind.Subtitle or InputKind.Pdf)
            list.Add(A("tr:translate", "Çevir", "Çevir (çevrimdışı)"));
        return list;
    }

    /// <summary>Alt: file operations (any selection, including folders).</summary>
    public static IReadOnlyList<MenuAction> Actions(InputKind kind, int count = 1) =>
    [
        A("act:share", "Paylaş", "Windows Paylaş"),
        A("act:float", "Yüzen pencere", "Dosya rafına koy"),
        A("act:copy", "Kopyala", "Klasöre kopyala"),
        A("act:move", "Taşı", "Klasöre taşı"),
        A("act:copypath", "Yolu kopyala", "Yolu panoya kopyala"),
        A("act:terminal", "Terminal", "Klasörde terminal aç"),
        Zip,
    ];

    public static IReadOnlyList<MenuAction> ForFiles(InputKind kind, int count = 1) => ForMode(Mode.All, kind, count);

    public static IReadOnlyList<MenuAction> ForMode(Mode mode, InputKind kind, int count, string? sourceExt = null)
    {
        switch (mode)
        {
            case Mode.Convert: return Convert(kind, count, sourceExt);
            case Mode.Tools: return Tools(kind, count);
            case Mode.Actions: return Actions(kind, count);
        }
        var groups = new List<MenuAction>();
        var c = Convert(kind, count, sourceExt);
        var t = Tools(kind, count);
        if (c.Count > 0) groups.Add(new("group:convert", L.T("Dönüştür"), "", Short: L.T("Dönüştür ›"), Children: c));
        if (t.Count > 0) groups.Add(new("group:tools", L.T("Araçlar"), "", Short: L.T("Araçlar ›"), Children: t));
        groups.Add(new("group:actions", L.T("İşlemler"), "", Short: L.T("İşlemler ›"), Children: Actions(kind, count)));
        return groups;
    }

    public static IReadOnlyList<MenuAction> ForLink(LinkInfo? link) => link?.Kind switch
    {
        LinkKind.Image => [new("dl:image", L.T("Resmi indir"), "↓", Short: L.T("Resmi indir"))],
        LinkKind.YouTube =>
        [
            new("dl:video", L.T("Videoyu indir (MP4)"), "↓", NeedsYtDlp: true, Short: L.T("Video")),
            new("dl:audio", L.T("Sesi indir (MP3)"), "♫", NeedsFfmpeg: true, NeedsYtDlp: true, Short: L.T("Ses (MP3)")),
            new("dl:thumb", L.T("Küçük resmi indir (JPG)"), "▣", NeedsYtDlp: true, Short: L.T("Kapak")),
        ],
        LinkKind.Media =>
        [
            new("dl:video", L.T("Videoyu indir (MP4)"), "↓", NeedsYtDlp: true, Short: L.T("Video")),
            new("dl:audio", L.T("Sesi indir (MP3)"), "♫", NeedsFfmpeg: true, NeedsYtDlp: true, Short: L.T("Ses (MP3)")),
        ],
        _ => [],
    };

    public const string PickFiles = "app:pick";
    public const string OpenSettings = "app:settings";

    private static MenuAction Pick => new(PickFiles, L.T("Dosya seç ve dönüştür"), "📂", Short: L.T("Dosya seç"));
    private static MenuAction Settings => new(OpenSettings, L.T("Ayarlar"), "⚙", Short: L.T("Ayarlar"));

    public static IReadOnlyList<MenuAction> ForBackground(LinkInfo? link) => [.. ForLink(link), Pick, Settings];

    public static IReadOnlyList<MenuAction> ForOther() => [.. Actions(InputKind.Mixed), Pick, Settings];

    public static string Describe(InputKind kind, int count) => kind switch
    {
        InputKind.Image => L.F("{0} görsel", count),
        InputKind.Video => L.F("{0} video", count),
        InputKind.Audio => L.F("{0} ses dosyası", count),
        InputKind.Pdf => L.F("{0} PDF", count),
        InputKind.Document => L.F("{0} belge", count),
        InputKind.Spreadsheet => L.F("{0} tablo", count),
        InputKind.Presentation => L.F("{0} sunum", count),
        InputKind.Epub => L.F("{0} e-kitap", count),
        InputKind.Subtitle => L.F("{0} altyazı", count),
        InputKind.Archive => L.F("{0} arşiv", count),
        InputKind.Mixed => L.F("{0} dosya (karışık)", count),
        InputKind.Unsupported => L.T("Desteklenmeyen dosya türü"),
        _ => L.T("Dosya yok"),
    };
}
