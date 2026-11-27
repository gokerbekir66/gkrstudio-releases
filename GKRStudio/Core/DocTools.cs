using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace GKRStudio.Core;

/// <summary>Simple, format-neutral document: headings, paragraphs and list items.
/// Complex layout is not preserved (the conversion is intentionally heuristic).</summary>
public sealed record Block(int Heading, string Text, bool ListItem = false);

public static partial class DocTools
{
    // ======================= dispatch =======================

    /// <summary>doc:* conversions for documents and EPUB. Must run on an STA thread (RTF/WPF rendering).</summary>
    public static async Task<string> ConvertAsync(string input, string target, Action<string> log, CancellationToken ct)
    {
        string ext = Path.GetExtension(input).ToLowerInvariant();

        // Formatting-sensitive Office inputs → let Office/LibreOffice do it when available.
        if (target is "pdf" or "png" or "jpg" && ext is ".docx" or ".doc" or ".odt" or ".rtf" && OfficeBridge.Available)
        {
            log("Office ile dönüştürülüyor: " + OfficeBridge.Describe);
            if (target == "pdf") return await OfficeBridge.ConvertAsync(input, "pdf", ct);
            string pdf = await OfficeBridge.ConvertToTempPdfAsync(input, ct);
            try { return await PdfRender.PagesToImagesAsync(pdf, target, input, ct); }
            finally { TryDelete(pdf); }
        }
        if (ext is ".doc" or ".odt")
        {
            if (!OfficeBridge.Available) throw new InvalidOperationException($"{ext} için LibreOffice veya Microsoft Office gerekli.");
            return await OfficeBridge.ConvertAsync(input, target == "docx" ? "docx" : target, ct);
        }

        var blocks = await Sta.Run(() => Read(input));
        log($"{blocks.Count} paragraf okundu.");
        string title = Path.GetFileNameWithoutExtension(input);
        return await Sta.Run(() => Write(blocks, title, target, input));
    }

    public static string Write(List<Block> blocks, string title, string target, string besideInput)
    {
        if (target is "png" or "jpg") return RenderImages(blocks, target, besideInput);
        string output = OutputPaths.Beside(besideInput, target);
        switch (target)
        {
            case "txt": File.WriteAllText(output, ToText(blocks), new UTF8Encoding(false)); break;
            case "md": File.WriteAllText(output, ToMarkdown(blocks), new UTF8Encoding(false)); break;
            case "html": File.WriteAllText(output, ToHtml(blocks, title), new UTF8Encoding(false)); break;
            case "docx": WriteDocx(blocks, output); break;
            case "epub": WriteEpub(blocks, title, output); break;
            case "pdf": WritePdf(blocks, output); break;
            default: throw new NotSupportedException("Hedef biçim: " + target);
        }
        return output;
    }

    // ======================= readers =======================

    public static List<Block> Read(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".txt" => FromText(ReadText(path)),
        ".md" or ".markdown" => FromMarkdown(ReadText(path)),
        ".html" or ".htm" => FromHtml(ReadText(path)),
        ".docx" => FromDocx(path),
        ".rtf" => FromRtf(path),
        ".epub" => FromEpub(path),
        ".pdf" => PdfText.Blocks(path),
        var e => throw new NotSupportedException("Okunamayan belge türü: " + e),
    };

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try { return new UTF8Encoding(false, true).GetString(bytes).TrimStart('﻿'); }
        catch (DecoderFallbackException) { return Encoding.Latin1.GetString(bytes); }
    }

    public static List<Block> FromText(string text) =>
        Regex.Split(text.Replace("\r\n", "\n"), @"\n\s*\n").Select(p => p.Trim()).Where(p => p.Length > 0)
             .Select(p => new Block(0, Regex.Replace(p, @"\s*\n\s*", " "))).ToList();

    public static List<Block> FromMarkdown(string md)
    {
        var blocks = new List<Block>();
        var para = new StringBuilder();
        void Flush() { if (para.Length > 0) { blocks.Add(new Block(0, InlineMd(para.ToString().Trim()))); para.Clear(); } }
        foreach (var raw in md.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            var h = Regex.Match(line, @"^(#{1,6})\s+(.*)$");
            var li = Regex.Match(line, @"^\s*(?:[-*+]|\d+[.)])\s+(.*)$");
            if (h.Success) { Flush(); blocks.Add(new Block(h.Groups[1].Length, InlineMd(h.Groups[2].Value.TrimEnd('#', ' ')))); }
            else if (li.Success) { Flush(); blocks.Add(new Block(0, InlineMd(li.Groups[1].Value), true)); }
            else if (line.Trim().Length == 0 || line.StartsWith("```")) Flush();
            else para.Append(line.Trim()).Append(' ');
        }
        Flush();
        return blocks;
    }

    private static string InlineMd(string s) =>
        Regex.Replace(Regex.Replace(Regex.Replace(s, @"!\[([^\]]*)\]\([^)]*\)", "$1"), @"\[([^\]]+)\]\([^)]*\)", "$1"), @"(\*\*|__|\*|_|`)", "");

    public static List<Block> FromHtml(string html)
    {
        html = Regex.Replace(html, @"<(script|style|head)[^>]*>.*?</\1>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var blocks = new List<Block>();
        foreach (Match m in Regex.Matches(html, @"<(h[1-6]|p|li|div|blockquote|pre|td|th|dt|dd)\b[^>]*>(.*?)</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            string tag = m.Groups[1].Value.ToLowerInvariant();
            string inner = m.Groups[2].Value;
            if (tag == "div" && Regex.IsMatch(inner, @"<(p|div|h[1-6]|li)\b", RegexOptions.IgnoreCase)) continue; // container div
            string text = WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(inner, @"<br\s*/?>", " ", RegexOptions.IgnoreCase), "<[^>]+>", ""));
            text = Regex.Replace(text, @"\s+", " ").Trim();
            if (text.Length == 0) continue;
            blocks.Add(new Block(tag[0] == 'h' && tag.Length == 2 && char.IsDigit(tag[1]) ? tag[1] - '0' : 0, text, tag == "li"));
        }
        if (blocks.Count == 0) // plain body text without block tags
            blocks = FromText(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "\n")));
        return blocks;
    }

    private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static List<Block> FromDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Geçerli bir DOCX değil.");
        using var s = entry.Open();
        var doc = XDocument.Load(s);
        var blocks = new List<Block>();
        foreach (var p in doc.Descendants(W + "p"))
        {
            var sb = new StringBuilder();
            foreach (var node in p.Descendants())
            {
                if (node.Name == W + "t") sb.Append(node.Value);
                else if (node.Name == W + "tab") sb.Append('\t');
                else if (node.Name == W + "br") sb.Append(' ');
            }
            string text = sb.ToString().Trim();
            if (text.Length == 0) continue;
            string style = p.Element(W + "pPr")?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
            var lvl = Regex.Match(style, @"(?:Heading|Kop|Ba[sş]l[ıi]k|berschrift|Titre)\s*(\d)", RegexOptions.IgnoreCase);
            int heading = lvl.Success ? int.Parse(lvl.Groups[1].Value) : style.Equals("Title", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            bool list = p.Element(W + "pPr")?.Element(W + "numPr") != null;
            blocks.Add(new Block(heading, text, list));
        }
        return blocks;
    }

    public static List<Block> FromEpub(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        string? opfPath = null;
        if (zip.GetEntry("META-INF/container.xml") is { } container)
        {
            using var s = container.Open();
            opfPath = XDocument.Load(s).Descendants().FirstOrDefault(e => e.Name.LocalName == "rootfile")?.Attribute("full-path")?.Value;
        }
        var blocks = new List<Block>();
        if (opfPath != null && zip.GetEntry(opfPath) is { } opfEntry)
        {
            XDocument opf;
            using (var s = opfEntry.Open()) opf = XDocument.Load(s);
            string baseDir = Path.GetDirectoryName(opfPath)?.Replace('\\', '/') ?? "";
            var manifest = opf.Descendants().Where(e => e.Name.LocalName == "item")
                .ToDictionary(e => (string)e.Attribute("id")!, e => (string)e.Attribute("href")!);
            foreach (var itemref in opf.Descendants().Where(e => e.Name.LocalName == "itemref"))
            {
                if (!manifest.TryGetValue((string?)itemref.Attribute("idref") ?? "", out var href)) continue;
                string full = (baseDir.Length > 0 ? baseDir + "/" : "") + Uri.UnescapeDataString(href);
                if (zip.GetEntry(full) is not { } chapter) continue;
                using var reader = new StreamReader(chapter.Open(), Encoding.UTF8);
                blocks.AddRange(FromHtml(reader.ReadToEnd()));
            }
        }
        if (blocks.Count == 0)
            foreach (var e in zip.Entries.Where(e => e.FullName.EndsWith(".xhtml") || e.FullName.EndsWith(".html")).OrderBy(e => e.FullName))
            {
                using var reader = new StreamReader(e.Open(), Encoding.UTF8);
                blocks.AddRange(FromHtml(reader.ReadToEnd()));
            }
        return blocks;
    }

    // ======================= writers =======================

    public static string ToText(List<Block> blocks) =>
        string.Join("\n\n", blocks.Select(b => (b.ListItem ? "• " : "") + b.Text)) + "\n";

    public static string ToMarkdown(List<Block> blocks)
    {
        var sb = new StringBuilder();
        foreach (var b in blocks)
        {
            if (b.Heading > 0) sb.Append(new string('#', b.Heading)).Append(' ').AppendLine(b.Text).AppendLine();
            else if (b.ListItem) sb.Append("- ").AppendLine(b.Text);
            else sb.AppendLine(b.Text).AppendLine();
        }
        return sb.ToString();
    }

    public static string ToHtml(List<Block> blocks, string title, bool xhtml = false)
    {
        var sb = new StringBuilder();
        sb.AppendLine(xhtml ? "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<html xmlns=\"http://www.w3.org/1999/xhtml\">" : "<!doctype html>\n<html>");
        sb.AppendLine($"<head><meta charset=\"utf-8\"{(xhtml ? " /" : "")}><title>{WebUtility.HtmlEncode(title)}</title></head><body>");
        bool inList = false;
        foreach (var b in blocks)
        {
            if (b.ListItem != inList) { sb.AppendLine(b.ListItem ? "<ul>" : "</ul>"); inList = b.ListItem; }
            string t = WebUtility.HtmlEncode(b.Text);
            sb.AppendLine(b.Heading > 0 ? $"<h{b.Heading}>{t}</h{b.Heading}>" : b.ListItem ? $"<li>{t}</li>" : $"<p>{t}</p>");
        }
        if (inList) sb.AppendLine("</ul>");
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static string X(string s) => new XText(s).ToString();

    public static void WriteDocx(List<Block> blocks, string output)
    {
        var body = new StringBuilder();
        foreach (var b in blocks)
        {
            string rpr = b.Heading > 0 ? $"<w:rPr><w:b/><w:sz w:val=\"{Math.Max(24, 40 - b.Heading * 4)}\"/></w:rPr>" : "";
            string ppr = b.Heading > 0 ? $"<w:pPr><w:pStyle w:val=\"Heading{b.Heading}\"/><w:spacing w:before=\"240\" w:after=\"120\"/></w:pPr>"
                       : b.ListItem ? "<w:pPr><w:ind w:left=\"360\" w:hanging=\"360\"/></w:pPr>" : "";
            string text = (b.ListItem ? "• " : "") + b.Text;
            body.Append($"<w:p>{ppr}<w:r>{rpr}<w:t xml:space=\"preserve\">{X(text)}</w:t></w:r></w:p>");
        }
        WriteZip(output, new()
        {
            ["[Content_Types].xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>",
            ["_rels/.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            ["word/document.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>{body}<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1440\" w:right=\"1440\" w:bottom=\"1440\" w:left=\"1440\"/></w:sectPr></w:body></w:document>",
        });
    }

    public static void WriteEpub(List<Block> blocks, string title, string output)
    {
        string id = "urn:uuid:" + Guid.NewGuid();
        string chapter = ToHtml(blocks, title, xhtml: true);
        using var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        // "mimetype" must be the first entry and stored uncompressed.
        using (var w = new StreamWriter(zip.CreateEntry("mimetype", CompressionLevel.NoCompression).Open(), new UTF8Encoding(false))) w.Write("application/epub+zip");
        void Add(string name, string content)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        Add("META-INF/container.xml", "<?xml version=\"1.0\"?><container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
        Add("OEBPS/content.opf", $"<?xml version=\"1.0\" encoding=\"utf-8\"?><package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"bookid\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"bookid\">{id}</dc:identifier><dc:title>{X(title)}</dc:title><dc:language>tr</dc:language><meta property=\"dcterms:modified\">{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ}</meta></metadata><manifest><item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/><item id=\"c1\" href=\"chapter1.xhtml\" media-type=\"application/xhtml+xml\"/></manifest><spine><itemref idref=\"c1\"/></spine></package>");
        var nav = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>İçindekiler</title></head><body><nav epub:type=\"toc\"><ol>");
        nav.Append($"<li><a href=\"chapter1.xhtml\">{X(title)}</a></li></ol></nav></body></html>");
        Add("OEBPS/nav.xhtml", nav.ToString());
        Add("OEBPS/chapter1.xhtml", chapter);
    }

    private static int fontsReady;

    public static void EnsureFonts()
    {
        if (Interlocked.Exchange(ref fontsReady, 1) == 0 && GlobalFontSettings.FontResolver == null) GlobalFontSettings.FontResolver = new WindowsFontResolver();
    }

    /// <summary>A4 text layout with word wrap; headings larger and bold. Fonts embedded (Unicode, Turkish safe).</summary>
    public static void WritePdf(List<Block> blocks, string output, bool monospace = false)
    {
        EnsureFonts();
        using var doc = new PdfDocument();
        string family = monospace ? "Consolas" : "Segoe UI";
        const double margin = 56;
        PdfPage page = null!;
        XGraphics gfx = null!;
        double y = 0;
        void NewPage()
        {
            gfx?.Dispose();
            page = doc.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            gfx = XGraphics.FromPdfPage(page);
            y = margin;
        }
        NewPage();
        double width = page.Width.Point - 2 * margin;
        foreach (var b in blocks)
        {
            double size = b.Heading switch { 1 => 20, 2 => 16, 3 => 14, > 3 => 12.5, _ => monospace ? 9.5 : 11 };
            var font = new XFont(family, size, b.Heading > 0 ? XFontStyleEx.Bold : XFontStyleEx.Regular);
            double lineHeight = size * 1.35;
            if (b.Heading > 0) y += size * 0.6;
            string indent = b.ListItem ? "• " : "";
            foreach (var line in Wrap(gfx, indent + b.Text, font, width))
            {
                if (y + lineHeight > page.Height.Point - margin) NewPage();
                gfx.DrawString(line, font, XBrushes.Black, new XPoint(margin, y + size));
                y += lineHeight;
            }
            y += monospace ? 0 : size * 0.55;
        }
        gfx.Dispose();
        using var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        doc.Save(fs);
    }

    private static IEnumerable<string> Wrap(XGraphics gfx, string text, XFont font, double width)
    {
        foreach (var hardLine in text.Replace("\t", "    ").Split('\n'))
        {
            var words = hardLine.Split(' ');
            var line = new StringBuilder();
            foreach (var word in words)
            {
                string candidate = line.Length == 0 ? word : line + " " + word;
                if (gfx.MeasureString(candidate, font).Width <= width) { line.Clear().Append(candidate); continue; }
                if (line.Length > 0) { yield return line.ToString(); line.Clear(); }
                string w = word;
                while (gfx.MeasureString(w, font).Width > width && w.Length > 1) // very long word: hard break
                {
                    int n = w.Length;
                    while (n > 1 && gfx.MeasureString(w[..n], font).Width > width) n--;
                    yield return w[..n];
                    w = w[n..];
                }
                line.Append(w);
            }
            yield return line.ToString();
        }
    }

    public static void WriteZip(string output, Dictionary<string, string> parts)
    {
        using var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in parts)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
