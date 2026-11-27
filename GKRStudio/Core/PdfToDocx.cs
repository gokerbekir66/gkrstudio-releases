using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis;
using UglyToad.PdfPig.DocumentLayoutAnalysis.PageSegmenter;
using UglyToad.PdfPig.DocumentLayoutAnalysis.ReadingOrderDetector;
using UglyToad.PdfPig.Core;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace GKRStudio.Core;

/// <summary>
/// Layout-preserving PDF → Word. Every PDF page becomes a Word page of the same size:
/// everything except text (pictures, shapes, backgrounds) is rendered into one picture behind the text,
/// and the text is written as real, editable Word text in positioned frames with the original
/// font, size, colour, bold/italic and line spacing.
/// </summary>
public static partial class PdfToDocx
{
    private const double Dpi = 150;
    // Exact line spacing L: Word puts the baseline about this fraction of L below the top of the line box.
    private const double BaselineRatio = 0.8;

    private sealed record Run(string Text, string Font, double Size, bool Bold, bool Italic, string Color, double Left);
    private sealed record TLine(double Top, double Baseline, double Left, double Right, double Size, List<Run> Runs);
    private sealed record Para(List<TLine> Lines, string Align, bool JustifyLast);
    private sealed record Frame(double X, double Y, double W, List<Para> Paras);
    private sealed record PageInfo(double W, double H, bool Rotated, List<Frame> Frames);

    public static async Task<string> ConvertAsync(string input, Action<double> frac, Action<string> log, CancellationToken ct)
    {
        Step("analiz başlıyor: " + Path.GetFileName(input));
        var pages = await OnBigStack(() => Analyze(input, ct));
        Step($"analiz bitti: {pages.Count} sayfa");
        if (pages.Count == 0) throw new InvalidOperationException("PDF'te sayfa yok.");
        frac(0.15);

        // Background pictures: render the PDF with its text removed (rotated pages: as they are).
        // Pictures go to a temp folder one by one, so big PDFs don't fill the memory.
        string work = Path.Combine(Path.GetTempPath(), "gkrstudio-p2w-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var backgrounds = new string?[pages.Count];
        try
        {
            string stripped = Path.Combine(work, "stripped.pdf");
            bool[] painted, hidden;
            bool stripFailed = false;
            try { (painted, hidden) = await OnBigStack(() => StripText(input, stripped)); Step("metin silindi"); }
            catch (Exception ex)
            {
                // The PDF can't be rewritten: bring every page over as a picture instead of an empty page.
                Step("metin silinemedi: " + ex.Message);
                log(L.T("Bu PDF'te metin ayrılamadı; sayfalar resim olarak aktarılıyor."));
                stripFailed = true;
                painted = new bool[pages.Count]; hidden = new bool[pages.Count];
                foreach (var pg in pages) pg.Frames.Clear();
            }
            // Scanned pages with an invisible OCR layer: the scan already shows the text; keep it as a picture.
            int scanned = 0;
            for (int i = 0; i < pages.Count && i < hidden.Length; i++)
                if (hidden[i] && painted[i]) { pages[i].Frames.Clear(); scanned++; }
            if (scanned > 0) log(L.F("{0} taranmış sayfa resim olarak aktarıldı.", scanned));

            var fromStripped = Enumerable.Range(0, pages.Count).Where(i => !stripFailed && i < painted.Length && painted[i] && !pages[i].Rotated).ToList();
            var fromOriginal = Enumerable.Range(0, pages.Count).Where(i => stripFailed || pages[i].Rotated).ToList();
            int total = Math.Max(1, fromStripped.Count + fromOriginal.Count), done = 0;
            if (fromStripped.Count + fromOriginal.Count > 0) log(L.T("Resimler ve arka plan işleniyor…"));
            async Task Render(string pdf, List<int> pick)
            {
                if (pick.Count == 0) return;
                Step($"{pick.Count} sayfa çiziliyor ({Path.GetFileName(pdf)})");
                await PdfRender.RenderEachAsync(pdf, pick, "jpg", Dpi, async (k, bytes) =>
                {
                    Step($"sayfa {pick[k] + 1} çizildi");
                    string file = Path.Combine(work, $"page{pick[k] + 1}.jpg");
                    await File.WriteAllBytesAsync(file, bytes, ct);
                    backgrounds[pick[k]] = file;
                    frac(0.15 + 0.7 * ++done / total);
                }, ct);
            }
            await Render(stripped, fromStripped);
            await Render(input, fromOriginal);
            frac(0.85);

            string output = OutputPaths.Beside(input, "docx");
            Step("docx yazılıyor");
            await Task.Run(() => Write(pages, backgrounds, output), ct);
            Step("bitti");
            log(L.F("{0} sayfa, {1} metin bloğu, {2} resim.", pages.Count, pages.Sum(p => p.Frames.Count), backgrounds.Count(b => b != null)));
            frac(1);
            return output;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    // ======================= text analysis (PdfPig) =======================

    private static List<PageInfo> Analyze(string input, CancellationToken ct)
    {
        using var doc = PigDocument.Open(input);
        var result = new List<PageInfo>();
        foreach (var page in doc.GetPages())
        {
            ct.ThrowIfCancellationRequested();
            var box = page.CropBox.Bounds;
            double W = box.Width, H = box.Height, ox = box.Left, oy = box.Bottom;
            bool rotated = page.Rotation.Value % 360 != 0;
            var frames = new List<Frame>();
            if (!rotated)
            {
                var words = page.GetWords()
                    .Where(w => w.Letters.Count > 0 && w.TextOrientation == TextOrientation.Horizontal
                                && w.Letters.Any(l => l.RenderingMode != TextRenderingMode.Neither && l.RenderingMode != TextRenderingMode.NeitherClip)
                                && !string.IsNullOrWhiteSpace(w.Text))
                    .ToList();
                if (words.Count > 0)
                {
                    IReadOnlyList<TextBlock> blocks;
                    try
                    {
                        blocks = DocstrumBoundingBoxes.Instance.GetBlocks(words);
                        blocks = UnsupervisedReadingOrderDetector.Instance.Get(blocks).ToList();
                    }
                    catch (Exception ex)
                    {
                        Step($"sayfa {page.Number}: düzen analizi başarısız ({ex.Message}), satır satır");
                        blocks = LineBlocks(words);
                    }
                    foreach (var block in blocks)
                    {
                        try { if (BuildFrame(block, W, H, ox, oy) is { } f) frames.Add(f); }
                        catch (Exception ex) { Step($"sayfa {page.Number}: blok atlandı ({ex.Message})"); }
                    }
                }
            }
            result.Add(new PageInfo(W, H, rotated, frames));
        }
        return result;
    }

    /// <summary>Fallback layout: one block per text line (words with about the same baseline).</summary>
    private static List<TextBlock> LineBlocks(List<Word> words)
    {
        var lines = new List<List<Word>>();
        foreach (var w in words.OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            var last = lines.LastOrDefault();
            if (last != null && Math.Abs(last[0].BoundingBox.Bottom - w.BoundingBox.Bottom) < Math.Max(1, last[0].BoundingBox.Height) * 0.5) last.Add(w);
            else lines.Add([w]);
        }
        return lines.Select(l => new TextBlock([new TextLine(l.OrderBy(w => w.BoundingBox.Left).ToList())])).ToList();
    }

    /// <summary>Optional diagnostic sink (the Mac app writes it to its log file), so a hard crash shows the last step.</summary>
    public static Action<string>? Trace { get; set; }
    private static void Step(string s) { try { Trace?.Invoke("pdf→docx: " + s); } catch { } }

    /// <summary>Deeply nested PDFs and big pages can need more stack than a pool thread has; a stack overflow kills the app.</summary>
    private static Task<T> OnBigStack<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }, 256 * 1024 * 1024) { IsBackground = true, Name = "pdf-to-docx" };
        thread.Start();
        return tcs.Task;
    }

    private static Frame? BuildFrame(TextBlock block, double W, double H, double ox, double oy)
    {
        var lines = new List<TLine>();
        foreach (var tl in block.TextLines)
        {
            var words = tl.Words.OrderBy(w => w.BoundingBox.Left).ToList();
            if (words.Count == 0) continue;
            var runs = new List<Run>();
            foreach (var wd in words)
            {
                var l0 = wd.Letters[0];
                double size = Math.Round(Median(wd.Letters.Select(l => l.PointSize)) * 2) / 2;
                var (font, bold, italic) = FontOf(l0);
                runs.Add(new Run(wd.Text, font, Math.Max(1, size), bold, italic, ColorOf(l0), wd.BoundingBox.Left - ox));
            }
            double baseline = H - (Median(words.Select(w => w.Letters[0].StartBaseLine.Y)) - oy);
            double sz = Median(runs.Select(r => r.Size));
            lines.Add(new TLine(H - (tl.BoundingBox.Top - oy), baseline,
                words[0].BoundingBox.Left - ox, words[^1].BoundingBox.Right - ox, sz, runs));
        }
        if (lines.Count == 0) return null;
        lines.Sort((a, b) => a.Baseline.CompareTo(b.Baseline));

        // Split into paragraphs: a big vertical gap, a size change or a clearly different start.
        var paras = new List<List<TLine>>();
        var cur = new List<TLine> { lines[0] };
        double pitch0 = lines.Count > 1 ? Median(lines.Zip(lines.Skip(1), (a, b) => b.Baseline - a.Baseline).Where(d => d > 0)) : lines[0].Size * 1.2;
        for (int i = 1; i < lines.Count; i++)
        {
            var a = lines[i - 1]; var b = lines[i];
            double gap = b.Baseline - a.Baseline;
            bool split = gap > Math.Max(pitch0, a.Size * 1.1) * 1.45 || Math.Abs(a.Size - b.Size) >= 0.5;
            if (split) { paras.Add(cur); cur = []; }
            cur.Add(b);
        }
        paras.Add(cur);

        double left = lines.Min(l => l.Left), right = lines.Max(l => l.Right);
        var result = new List<Para>();
        foreach (var p in paras)
        {
            string align = "left";
            double minL = p.Min(l => l.Left), maxL = p.Max(l => l.Left);
            if (p.Count >= 2)
            {
                double maxR = p.Max(l => l.Right);
                bool full = p.Take(p.Count - 1).All(l => maxR - l.Right < Math.Max(3, l.Size * 0.4));
                double cSpread = p.Max(l => (l.Left + l.Right) / 2) - p.Min(l => (l.Left + l.Right) / 2);
                double rSpread = maxR - p.Min(l => l.Right);
                if (full && maxL - minL < 3 && p.Count >= 3) align = "both";
                else if (maxL - minL > 4 && cSpread < 3) align = "center";
                else if (maxL - minL > 4 && rSpread < 2) align = "right";
                else if (full && maxL - minL < 3) align = "both";
            }
            else
            {
                var l = p[0];
                double c = (l.Left + l.Right) / 2;
                if (Math.Abs(c - W / 2) < Math.Max(4, l.Size * 0.5) && l.Left > W * 0.12) align = "center";
            }
            // A justified paragraph that runs on to the next page/column: its last line is full width too.
            bool justifyLast = align == "both" && right - p[^1].Right < Math.Max(1.5, p[^1].Size * 0.2);
            result.Add(new Para(p, align, justifyLast));
        }

        bool allCenter = result.All(p => p.Align == "center");
        bool anyFlow = result.Any(p => p.Align == "both");
        double x, w;
        if (allCenter)
        {
            double c = (left + right) / 2, half = Math.Max(Math.Min(c, W - c), (right - left) / 2 + 2);
            x = c - half; w = 2 * half;
        }
        else if (anyFlow) { x = left - 1; w = right - left + 2; }
        else { x = left - 1; w = Math.Max(right - left + 4, W - x - 4); }
        x = Math.Max(0, x);
        double top = lines[0].Baseline - BaselineRatio * LineHeight(result[0]);
        return new Frame(x, Math.Max(0, top), w, result);
    }

    private static double LineHeight(Para p)
    {
        if (p.Lines.Count > 1)
        {
            double d = Median(p.Lines.Zip(p.Lines.Skip(1), (a, b) => b.Baseline - a.Baseline).Where(v => v > 0.5));
            if (d > 0) return d;
        }
        return p.Lines.Max(l => l.Size) * 1.2;
    }

    private static double Median(IEnumerable<double> values)
    {
        var v = values.OrderBy(x => x).ToList();
        return v.Count == 0 ? 0 : v[v.Count / 2];
    }

    private static string ColorOf(Letter l)
    {
        try
        {
            var (r, g, b) = l.Color.ToRGBValues();
            return $"{(int)Math.Round(r * 255):X2}{(int)Math.Round(g * 255):X2}{(int)Math.Round(b * 255):X2}";
        }
        catch { return "000000"; }
    }

    private static readonly Dictionary<string, string> KnownFonts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Helvetica"] = "Arial", ["Times"] = "Times New Roman", ["TimesNewRoman"] = "Times New Roman",
        ["Courier"] = "Courier New", ["CourierNew"] = "Courier New", ["Symbol"] = "Symbol", ["ZapfDingbats"] = "Wingdings",
    };

    private static (string Font, bool Bold, bool Italic) FontOf(Letter l)
    {
        string name = l.FontName ?? "";
        int plus = name.IndexOf('+');
        if (plus >= 0 && plus <= 7) name = name[(plus + 1)..];
        string family = name, style = "";
        int sep = name.IndexOfAny([',', '-']);
        if (sep > 0) { family = name[..sep]; style = name[(sep + 1)..]; }
        family = Regex.Replace(family, "(PSMT|PS|MT)$", "");
        string all = name.ToLowerInvariant();
        bool bold = all.Contains("bold") || all.Contains("black") || all.Contains("heavy") || (l.FontDetails?.IsBold ?? false);
        bool italic = all.Contains("italic") || all.Contains("oblique") || (l.FontDetails?.IsItalic ?? false);
        if (KnownFonts.TryGetValue(family, out var known)) family = known;
        else if (!family.Contains(' ')) family = Regex.Replace(family, "(?<=[a-z])(?=[A-Z])", " ");
        foreach (var weight in new[] { "Light", "SemiBold", "Semibold", "Medium", "Black", "Thin", "Condensed", "Narrow" })
            if (style.Contains(weight, StringComparison.OrdinalIgnoreCase) && !family.Contains(weight, StringComparison.OrdinalIgnoreCase))
            {
                family += " " + weight;
                if (weight is "SemiBold" or "Semibold" or "Black") bold = false;
            }
        if (family.Length == 0) family = "Calibri";
        return (family, bold, italic);
    }

    // ======================= background (PDFsharp) =======================

    private static readonly HashSet<string> PaintOps = ["Do", "f", "F", "f*", "B", "B*", "b", "b*", "S", "s", "sh", "BI", "EI"];

    private static readonly HashSet<string> TextOps = ["BT", "ET", "Tc", "Tw", "Tz", "TL", "Tf", "Tr", "Ts", "Td", "TD", "Tm", "T*", "Tj", "TJ", "'", "\""];
    private static readonly HashSet<string> ShowOps = ["Tj", "TJ", "'", "\""];

    /// <summary>Writes a copy of the PDF without text. Per page: is anything still painted, and is all of its
    /// text invisible (an OCR layer over a scan: render mode 3/7 or fully transparent)?</summary>
    private static (bool[] Painted, bool[] HiddenText) StripText(string input, string output)
    {
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        var painted = new bool[doc.PageCount];
        var hidden = new bool[doc.PageCount];
        var done = new Dictionary<PdfDictionary, bool>();
        for (int i = 0; i < doc.PageCount; i++)
        {
            var page = doc.Pages[i];
            try
            {
                var seq = ContentReader.ReadContent(page);
                bool paints = Strip(seq, page.Resources, out int visible, out int invisible);
                page.Contents.ReplaceContent(seq);
                paints |= StripXObjects(page.Resources, done);
                painted[i] = paints;
                hidden[i] = invisible > 0 && visible == 0;
            }
            catch (Exception ex)
            {
                // This page keeps its text in the picture; its Word text is left out (hidden = true) so nothing doubles.
                Step($"sayfa {i + 1}: metin silinemedi ({ex.Message})");
                painted[i] = true; hidden[i] = true;
            }
        }
        doc.Save(output);
        return (painted, hidden);
    }

    private static bool StripXObjects(PdfDictionary? resources, Dictionary<PdfDictionary, bool> done)
    {
        bool paints = false;
        var xobjects = resources?.Elements.GetDictionary("/XObject");
        if (xobjects == null) return false;
        foreach (var key in xobjects.Elements.Keys.ToList())
        {
            var x = xobjects.Elements.GetDictionary(key);
            if (x == null) continue;
            if (done.TryGetValue(x, out var known)) { paints |= known; continue; }
            done[x] = true; // guards against cycles
            string subtype = x.Elements.GetName("/Subtype");
            if (subtype == "/Image") { paints = true; continue; }
            if (subtype != "/Form" || x.Stream == null) { done[x] = false; continue; }
            var res = x.Elements.GetDictionary("/Resources");
            var seq = ContentReader.ReadContent(x.Stream.UnfilteredValue);
            bool p = Strip(seq, res, out _, out _);
            x.Stream.Value = seq.ToContent();
            x.Elements.Remove("/Filter");
            x.Elements.Remove("/DecodeParms");
            p |= StripXObjects(res, done);
            done[x] = p;
            paints |= p;
        }
        return paints;
    }

    /// <summary>Removes text operators (graphics-state changes inside BT…ET are kept, they outlive ET).
    /// Returns whether the remaining content paints anything; counts visible and invisible text shows.</summary>
    private static bool Strip(CSequence seq, PdfDictionary? resources, out int visible, out int invisible)
    {
        visible = invisible = 0;
        bool paints = false;
        var stack = new Stack<(bool Clear, int Mode)>();
        bool clear = false; int mode = 0;
        var states = resources?.Elements.GetDictionary("/ExtGState");
        for (int i = 0; i < seq.Count; i++)
        {
            if (seq[i] is not COperator op) continue;
            string name = op.OpCode.Name;
            switch (name)
            {
                case "q": stack.Push((clear, mode)); break;
                case "Q": if (stack.Count > 0) (clear, mode) = stack.Pop(); break;
                case "Tr" when op.Operands.Count > 0 && op.Operands[0] is CInteger tr: mode = tr.Value; break;
                case "gs" when op.Operands.Count > 0 && op.Operands[0] is CName gsName:
                {
                    var gs = states?.Elements.GetDictionary(gsName.Name);
                    if (gs != null && gs.Elements.ContainsKey("/ca")) clear = gs.Elements.GetReal("/ca") <= 0.01;
                    break;
                }
            }
            if (ShowOps.Contains(name))
            {
                if (clear || mode is 3 or 7) invisible++; else visible++;
            }
            if (TextOps.Contains(name)) { seq.RemoveAt(i--); continue; }
            if (PaintOps.Contains(name)) paints = true;
        }
        return paints;
    }

    // ======================= DOCX writer =======================

    private static string Tw(double pt) => ((int)Math.Round(pt * 20)).ToString(CultureInfo.InvariantCulture);
    private static string X(string s) => new XText(s).ToString();

    private static void Write(List<PageInfo> pages, string?[] backgrounds, string output)
    {
        var body = new StringBuilder();
        var rels = new StringBuilder();
        var parts = new Dictionary<string, string>();
        int id = 1;
        for (int p = 0; p < pages.Count; p++)
        {
            var page = pages[p];
            foreach (var f in page.Frames) WriteFrame(body, f, page);

            // One tiny flowing paragraph per page: holds the background picture and the page's section.
            body.Append("<w:p><w:pPr><w:spacing w:before=\"0\" w:after=\"0\" w:line=\"20\" w:lineRule=\"exact\"/>");
            if (p < pages.Count - 1) body.Append(SectPr(page));
            body.Append("<w:rPr><w:sz w:val=\"2\"/></w:rPr></w:pPr>");
            if (backgrounds[p] is { } img)
            {
                string rid = "rIdImg" + id, file = $"media/page{p + 1}.jpg";
                parts["word/" + file] = img;
                rels.Append($"<Relationship Id=\"{rid}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"{file}\"/>");
                long cx = (long)Math.Round(page.W * 12700), cy = (long)Math.Round(page.H * 12700);
                body.Append($"<w:r><w:rPr><w:noProof/><w:sz w:val=\"2\"/></w:rPr><w:drawing><wp:anchor distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\" simplePos=\"0\" relativeHeight=\"{id}\" behindDoc=\"1\" locked=\"1\" layoutInCell=\"1\" allowOverlap=\"1\">"
                    + "<wp:simplePos x=\"0\" y=\"0\"/><wp:positionH relativeFrom=\"page\"><wp:posOffset>0</wp:posOffset></wp:positionH><wp:positionV relativeFrom=\"page\"><wp:posOffset>0</wp:posOffset></wp:positionV>"
                    + $"<wp:extent cx=\"{cx}\" cy=\"{cy}\"/><wp:effectExtent l=\"0\" t=\"0\" r=\"0\" b=\"0\"/><wp:wrapNone/><wp:docPr id=\"{id}\" name=\"Sayfa {p + 1}\"/><wp:cNvGraphicFramePr/>"
                    + "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\"><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">"
                    + $"<pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"page{p + 1}.jpg\"/><pic:cNvPicPr/></pic:nvPicPr><pic:blipFill><a:blip r:embed=\"{rid}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>"
                    + $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:anchor></w:drawing></w:r>");
                id++;
            }
            body.Append("</w:p>");
        }
        body.Append(SectPr(pages[^1]));

        const string ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"";
        var text = new Dictionary<string, string>
        {
            ["[Content_Types].xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Default Extension=\"jpg\" ContentType=\"image/jpeg\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/><Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/></Types>",
            ["_rels/.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>",
            ["word/_rels/document.xml.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" + rels + "</Relationships>",
            ["word/styles.xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\" w:eastAsia=\"Calibri\"/><w:sz w:val=\"22\"/><w:lang w:val=\"tr-TR\"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults><w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/></w:style></w:styles>",
            ["word/document.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document {ns}><w:body>{body}</w:body></w:document>",
        };
        if (File.Exists(output)) File.Delete(output);
        using var zip = System.IO.Compression.ZipFile.Open(output, System.IO.Compression.ZipArchiveMode.Create);
        foreach (var (name, content) in text)
        {
            using var s = zip.CreateEntry(name).Open();
            var bytes = new UTF8Encoding(false).GetBytes(content);
            s.Write(bytes);
        }
        foreach (var (name, file) in parts)
        {
            using var s = zip.CreateEntry(name, System.IO.Compression.CompressionLevel.NoCompression).Open();
            using var f = File.OpenRead(file);
            f.CopyTo(s);
        }
    }

    private static string SectPr(PageInfo page)
    {
        string orient = page.W > page.H ? " w:orient=\"landscape\"" : "";
        return $"<w:sectPr><w:type w:val=\"nextPage\"/><w:pgSz w:w=\"{Tw(page.W)}\" w:h=\"{Tw(page.H)}\"{orient}/><w:pgMar w:top=\"0\" w:right=\"0\" w:bottom=\"0\" w:left=\"0\" w:header=\"0\" w:footer=\"0\" w:gutter=\"0\"/></w:sectPr>";
    }

    private static void WriteFrame(StringBuilder body, Frame f, PageInfo page)
    {
        string frame = $"<w:framePr w:w=\"{Tw(f.W)}\" w:hSpace=\"0\" w:wrap=\"around\" w:vAnchor=\"page\" w:hAnchor=\"page\" w:x=\"{Tw(f.X)}\" w:y=\"{Tw(f.Y)}\"/>";
        double prevBottom = f.Y;
        foreach (var para in f.Paras)
        {
            double L = LineHeight(para);
            double top = para.Lines[0].Baseline - BaselineRatio * L;
            double before = Math.Max(0, top - prevBottom);
            prevBottom = top + L * (para.Lines.Count + (para.JustifyLast ? 1 : 0));

            // Indents put the paragraph where it was inside the frame.
            double minL = para.Lines.Min(l => l.Left), maxR = para.Lines.Max(l => l.Right);
            double indL = 0, indR = 0;
            switch (para.Align)
            {
                case "center":
                {
                    double c = para.Lines.Average(l => (l.Left + l.Right) / 2) - f.X;
                    if (c < f.W / 2) indR = f.W - 2 * c; else indL = 2 * c - f.W;
                    break;
                }
                case "right": indR = Math.Max(0, f.X + f.W - maxR - 1); break;
                default:
                    indL = Math.Max(0, minL - f.X);
                    if (para.Align == "both") indR = Math.Max(0, f.X + f.W - maxR);
                    break;
            }
            body.Append("<w:p><w:pPr>").Append(frame)
                .Append($"<w:spacing w:before=\"{Tw(before)}\" w:after=\"0\" w:line=\"{Tw(L)}\" w:lineRule=\"exact\"/>")
                .Append($"<w:ind w:left=\"{Tw(indL)}\" w:right=\"{Tw(Math.Max(0, indR))}\"/>")
                .Append($"<w:jc w:val=\"{para.Align}\"/></w:pPr>");
            for (int i = 0; i < para.Lines.Count; i++)
            {
                var line = para.Lines[i];
                if (i > 0)
                {
                    body.Append("<w:r><w:br/></w:r>");
                }
                // Justified lines are condensed a hair so they never wrap early; Word stretches them back to full width.
                bool condense = para.Align == "both" && (i < para.Lines.Count - 1 || para.JustifyLast);
                for (int j = 0; j < line.Runs.Count; j++)
                {
                    if (j > 0) AppendRun(body, " ", line.Runs[j - 1], condense);
                    AppendRun(body, line.Runs[j].Text, line.Runs[j], condense);
                }
            }
            if (para.JustifyLast) body.Append("<w:r><w:br/></w:r>");
            body.Append("</w:p>");
        }
    }

    private static void AppendRun(StringBuilder body, string text, Run r, bool condense = false)
    {
        string font = X(r.Font).Replace("\"", "&quot;");
        int half = Math.Max(2, (int)Math.Round(r.Size * 2));
        body.Append("<w:r><w:rPr>")
            .Append($"<w:rFonts w:ascii=\"{font}\" w:hAnsi=\"{font}\" w:cs=\"{font}\" w:eastAsia=\"{font}\"/>")
            .Append(r.Bold ? "<w:b/><w:bCs/>" : "")
            .Append(r.Italic ? "<w:i/><w:iCs/>" : "")
            .Append(r.Color != "000000" ? $"<w:color w:val=\"{r.Color}\"/>" : "")
            .Append(condense ? "<w:spacing w:val=\"-4\"/>" : "")
            .Append($"<w:sz w:val=\"{half}\"/><w:szCs w:val=\"{half}\"/>")
            .Append("</w:rPr><w:t xml:space=\"preserve\">").Append(X(text)).Append("</w:t></w:r>");
    }
}
