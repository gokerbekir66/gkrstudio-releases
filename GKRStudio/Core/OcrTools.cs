using System.IO;
using System.Text;
using System.Threading.Channels;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace GKRStudio.Core;

/// <summary>
/// OCR with the engine built into Windows (Windows.Media.Ocr) — no download needed.
/// PDF → searchable PDF (page image + invisible text layer) + TXT; image → TXT.
/// The recognizer language is picked from the document itself (see <see cref="PickLanguageAsync"/>).
/// </summary>
public static class OcrTools
{
    private const double Dpi = 200;

    private static string Prefix(Language l) => l.LanguageTag.Split('-')[0].ToLowerInvariant();

    private static OcrEngine Create(Language language) =>
        OcrEngine.TryCreateFromLanguage(language) ?? throw new InvalidOperationException("Windows OCR: " + language.DisplayName);

    /// <summary>
    /// Chooses the recognizer for this document: the language of the text itself when it can be detected, otherwise the
    /// app's own language. Windows' Turkish recognizer reads other languages badly ("Hello" → "Heııo") and the others miss
    /// Turkish letters, so the sample pages are first read with a non-Turkish recognizer and that text's language is detected.
    /// </summary>
    private static async Task<Language> PickLanguageAsync(IReadOnlyList<byte[]> samples, Action<string>? log)
    {
        var installed = OcrEngine.AvailableRecognizerLanguages.ToList();
        if (installed.Count == 0)
            throw new InvalidOperationException("Windows OCR için dil paketi yok. Ayarlar → Saat ve dil → Dil'den Türkçe (veya İngilizce) paketini ekleyin.");
        Language chosen = installed[0];
        if (installed.Count > 1)
        {
            var appLanguage = installed.FirstOrDefault(l => Prefix(l) == L.Lang);
            var probeLanguage = appLanguage is { } a && Prefix(a) != "tr" ? a : OcrEngine.TryCreateFromUserProfileLanguages()?.RecognizerLanguage;
            if (probeLanguage == null || Prefix(probeLanguage) == "tr")
                probeLanguage = installed.FirstOrDefault(l => Prefix(l) != "tr") ?? installed[0];
            var probe = Create(probeLanguage);
            var text = new StringBuilder();
            foreach (var image in samples)
            {
                var (result, _, _) = await RecognizeAsync(probe, new MemoryStream(image));
                foreach (var line in result.Lines) text.AppendLine(line.Text);
            }
            string? code = LanguagePacks.Detect(text.ToString());
            var detected = installed.FirstOrDefault(l => Prefix(l) == code);
            if (detected != null) chosen = detected;
            else if (code != null) chosen = appLanguage ?? probeLanguage; // detected, but that recognizer isn't installed
            else
            {
                // too little text to tell: Turkish only if its recognizer finds letters that exist only in Turkish,
                // otherwise the app's language
                chosen = appLanguage ?? probeLanguage;
                var turkish = installed.FirstOrDefault(l => Prefix(l) == "tr");
                if (turkish != null && Prefix(chosen) != "tr")
                {
                    var trEngine = Create(turkish);
                    foreach (var image in samples)
                    {
                        var (result, _, _) = await RecognizeAsync(trEngine, new MemoryStream(image));
                        if (result.Lines.Any(l => l.Text.IndexOfAny(['ş', 'Ş', 'ğ', 'Ğ', 'İ']) >= 0)) { chosen = turkish; break; }
                    }
                }
            }
        }
        log?.Invoke("OCR dili: " + chosen.DisplayName);
        return chosen;
    }

    private static async Task<(OcrResult Result, uint Width, uint Height)> RecognizeAsync(OcrEngine engine, Stream image)
    {
        var decoder = await BitmapDecoder.CreateAsync(image.AsRandomAccessStream());
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        return (await engine.RecognizeAsync(bitmap), decoder.PixelWidth, decoder.PixelHeight);
    }

    public static async Task<string> ImageAsync(string input, Action<string> log, CancellationToken ct)
    {
        byte[] image = await File.ReadAllBytesAsync(input, ct);
        var engine = Create(await PickLanguageAsync([image], log));
        var (result, _, _) = await RecognizeAsync(engine, new MemoryStream(image));
        string text = string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
        log($"{result.Lines.Count} satır tanındı.");
        string output = OutputPaths.Beside(input, "txt", "(OCR)");
        await File.WriteAllTextAsync(output, text, new UTF8Encoding(false), ct);
        return output;
    }

    /// <summary>
    /// "Clean text": every recognised line is covered with white and written again in black, sharp Arial at the
    /// same place and size (one font size and baseline per line, each word stretched to its original width).
    /// Pictures and anything the OCR did not recognise stay untouched; the text stays selectable/searchable.
    /// </summary>
    private static void DrawClean(XGraphics gfx, OcrResult result, double scale, Dictionary<int, XFont> fonts)
    {
        var black = XBrushes.Black;
        var unit = new XFont("Arial", 10, XFontStyleEx.Regular);
        static double Median(List<double> v) { v.Sort(); return v[v.Count / 2]; }

        // Font size per line from word WIDTHS (justified text stretches only the spaces, never the words)...
        var lines = new List<(List<OcrWord> Words, double Em)>();
        foreach (var line in result.Lines)
        {
            var words = line.Words.Where(w => w.BoundingRect.Width > 0 && w.BoundingRect.Height > 0).ToList();
            if (words.Count == 0) continue;
            var ems = words.Where(w => w.Text.Length >= 3)
                           .Select(w => w.BoundingRect.Width * scale / Math.Max(0.01, gfx.MeasureString(w.Text, unit).Width) * 10)
                           .ToList();
            double em = ems.Count > 0 ? Median(ems) : Median(words.Select(w => w.BoundingRect.Height * scale / 0.74).ToList());
            lines.Add((words, Math.Clamp(em, 3, 200)));
        }
        if (lines.Count == 0) return;
        // ...then one size for the body text of the page; only clearly different lines (headings, notes) keep their own.
        double body = Median(lines.Select(l => l.Em).ToList());

        foreach (var (words, lineEm) in lines)
        {
            double em = Math.Abs(lineEm - body) / body < 0.18 ? body : lineEm;
            var bottoms = words.Select(w => (w.BoundingRect.Y + w.BoundingRect.Height) * scale).ToList();
            bottoms.Sort();
            double baseline = bottoms[bottoms.Count / 4]; // words with tails (ş, ç, ğ, y, p) reach lower; the upper quarter is the baseline
            int key = (int)Math.Round(em * 4);
            if (!fonts.TryGetValue(key, out var font)) fonts[key] = font = new XFont("Arial", key / 4.0, XFontStyleEx.Regular);

            // white-out the old line (a little padding catches the ragged pixels around the letters)
            double x0 = words.Min(w => w.BoundingRect.X) * scale, x1 = words.Max(w => w.BoundingRect.X + w.BoundingRect.Width) * scale;
            double y0 = words.Min(w => w.BoundingRect.Y) * scale, y1 = words.Max(w => w.BoundingRect.Y + w.BoundingRect.Height) * scale;
            double pad = em * 0.12;
            gfx.DrawRectangle(XBrushes.White, x0 - pad, y0 - pad, x1 - x0 + 2 * pad, y1 - y0 + 2 * pad);

            foreach (var word in words)
            {
                var r = word.BoundingRect;
                double natural = gfx.MeasureString(word.Text, font).Width;
                var state = gfx.Save();
                gfx.TranslateTransform(r.X * scale, baseline);
                if (natural > 0) gfx.ScaleTransform(Math.Clamp(r.Width * scale / natural, 0.8, 1.25), 1); // fit the word's width, never distort much
                gfx.DrawString(word.Text, font, black, 0, 0);
                gfx.Restore(state);
            }
        }
    }

    /// <summary>True when the OCR picture of this page maps straight onto the page (no rotation, picture ≈ crop box).</summary>
    private static bool Overlayable(PdfPage page, (uint W, uint H) px, out double scale, out double dx, out double dy)
    {
        scale = dx = dy = 0;
        if (px.W == 0 || px.H == 0 || page.Rotate % 360 != 0) return false;
        var media = page.MediaBox;
        var crop = PdfTools.VisibleBox(page);
        double w = crop.Width, h = crop.Height;
        if (w <= 0 || h <= 0) return false;
        scale = w / px.W;
        if (Math.Abs(h / px.H - scale) / scale > 0.02) return false; // aspect differs → not the same box
        // XGraphics' origin is the media box's top-left corner.
        dx = crop.X1 - media.X1;
        dy = media.Y2 - crop.Y2;
        return true;
    }

    public static async Task<string> PdfAsync(string input, string? pageSpec, bool alsoText, bool clean, Action<double> frac, Action<string> log, CancellationToken ct)
    {
        var pdf = await PdfRender.OpenAsync(input);
        var pick = PdfTools.Selection(pageSpec, (int)pdf.PageCount); // 0-based page numbers, in order
        int total = pick.Count;
        // the first (up to) three pages decide the recognizer language; they are kept for the OCR pass below
        var sampled = new byte[Math.Min(3, total)][];
        for (int i = 0; i < sampled.Length; i++) sampled[i] = await PdfRender.RenderPageAsync(pdf, (uint)pick[i], "jpg", Dpi, ct);
        var language = await PickLanguageAsync(sampled, log);
        // Pages are rendered one after another (fast) while several OCR engines read them in parallel (the slow part).
        int workers = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);
        var images = new byte[total][];
        var results = new OcrResult[total];
        var sizes = new (uint W, uint H)[total];
        var queue = Channel.CreateBounded<int>(workers * 2);
        int done = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); // one failing worker stops the others
        log(L.F("{0} sayfa, {1} paralel OCR", total, workers));
        frac(0);

        var producer = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < total; i++)
                {
                    images[i] = i < sampled.Length ? sampled[i] : await PdfRender.RenderPageAsync(pdf, (uint)pick[i], "jpg", Dpi, stop.Token);
                    await queue.Writer.WriteAsync(i, stop.Token);
                }
                queue.Writer.Complete();
            }
            catch (Exception ex) { queue.Writer.Complete(ex); stop.Cancel(); throw; }
        });
        var consumers = Enumerable.Range(0, workers).Select(w => Task.Run(async () =>
        {
            try
            {
                var engine = Create(language);
                await foreach (int i in queue.Reader.ReadAllAsync(stop.Token))
                {
                    (results[i], uint w, uint h) = await RecognizeAsync(engine, new MemoryStream(images[i]));
                    sizes[i] = (w, h);
                    int n = Interlocked.Increment(ref done);
                    log($"Sayfa {pick[i] + 1} ({n}/{total}): {results[i].Lines.Count} satır");
                    frac(0.97 * n / total); // the last 3 % is building the PDF
                }
            }
            catch { stop.Cancel(); throw; }
        })).ToList();
        var all = Task.WhenAll(consumers.Append(producer));
        try { await all; }
        catch when (all.Exception?.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) is { } real && !ct.IsCancellationRequested)
        { throw real; }

        DocTools.EnsureFonts();
        var text = new StringBuilder();
        var streams = new List<MemoryStream>();
        var fonts = new Dictionary<int, XFont>();
        // The text layer goes on top of the ORIGINAL pages, so the file stays about as small (and sharp) as the input.
        // Pages that can't be reused (rotated, odd boxes, unreadable PDF) fall back to the rendered page image.
        PdfDocument? source = null;
        try { source = PdfReader.Open(input, PdfDocumentOpenMode.Import); }
        catch (Exception ex) { log("Orijinal sayfalar kullanılamıyor, görüntü olarak kaydedilecek: " + ex.Message); }
        using var sourceDoc = source;
        int asImage = 0;
        try
        {
            using var doc = new PdfDocument();
            var invisible = new XSolidBrush(Environment.GetEnvironmentVariable("GKR_OCR_DEBUG") == "1" ? XColor.FromArgb(160, 255, 0, 0) : XColor.FromArgb(1, 255, 255, 255)); // alpha 0 as the first brush on a page is written opaque by PDFsharp; 1/255 white is invisible
            for (int i = 0; i < total; i++)
            {
                ct.ThrowIfCancellationRequested();
                var result = results[i];
                PdfPage page;
                XGraphics gfx;
                double scale, dx = 0, dy = 0;
                if (source != null && Overlayable(source.Pages[pick[i]], sizes[i], out scale, out dx, out dy))
                {
                    page = doc.AddPage(source.Pages[pick[i]]);
                    PdfTools.DropEmptyCropBox(page);
                    gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                }
                else
                {
                    asImage++;
                    scale = 72.0 / Dpi; // pixels → points
                    var ms = new MemoryStream(images[i], 0, images[i].Length, writable: false, publiclyVisible: true); // PDFsharp reads the buffer directly
                    streams.Add(ms);
                    using var image = XImage.FromStream(ms);
                    page = doc.AddPage();
                    page.Width = XUnit.FromPoint(image.PixelWidth * scale);
                    page.Height = XUnit.FromPoint(image.PixelHeight * scale);
                    gfx = XGraphics.FromPdfPage(page);
                    gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
                }
                using var g = gfx;
                if (dx != 0 || dy != 0) gfx.TranslateTransform(dx, dy);

                if (clean)
                {
                    DrawClean(gfx, result, scale, fonts);
                    foreach (var line in result.Lines) text.AppendLine(line.Text);
                    text.AppendLine();
                    images[i] = null!;
                    continue;
                }
                // Invisible, selectable/searchable words placed over the scanned text.
                foreach (var line in result.Lines)
                {
                    foreach (var word in line.Words)
                    {
                        var r = word.BoundingRect;
                        double h = Math.Max(4, r.Height * scale);
                        int key = (int)Math.Round(h * 0.85 * 4); // quarter-point steps
                        if (!fonts.TryGetValue(key, out var font)) fonts[key] = font = new XFont("Arial", key / 4.0, XFontStyleEx.Regular);
                        double natural = gfx.MeasureString(word.Text, font).Width;
                        var state = gfx.Save();
                        gfx.TranslateTransform(r.X * scale, (r.Y + r.Height * 0.82) * scale);
                        if (natural > 0) gfx.ScaleTransform(r.Width * scale / natural, 1); // stretch to the word's real width
                        gfx.DrawString(word.Text, font, invisible, 0, 0);
                        gfx.Restore(state);
                    }
                    text.AppendLine(line.Text);
                }
                text.AppendLine();
                images[i] = null!; // free memory as we go
            }
            if (asImage > 0) log(L.F("{0} sayfa görüntü olarak eklendi.", asImage));
            frac(0.99);
            string tag = clean ? "net" : "OCR";
            string suffix = total == pdf.PageCount ? $"({tag})" : $"({tag} {PdfTools.SelectionLabel(pick)})";
            string output = OutputPaths.Beside(input, "pdf", suffix);
            using (var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write)) doc.Save(fs);
            if (alsoText)
            {
                string txt = OutputPaths.Beside(input, "txt", suffix);
                await File.WriteAllTextAsync(txt, text.ToString(), new UTF8Encoding(false), ct);
                log("Metin ayrıca kaydedildi: " + Path.GetFileName(txt));
            }
            return output;
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }
}
