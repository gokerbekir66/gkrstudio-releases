using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Channels;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SkiaSharp;

namespace GKRStudio.Core;

/// <summary>PDF page rendering on macOS with PDFium (PDFtoImage).</summary>
public static class PdfRender
{
    public static async Task<string> PagesToImagesAsync(string pdf, string format, string besideInput, CancellationToken ct)
    {
        var pages = await RenderAsync(pdf, format, 150, ct);
        string dir = Path.GetDirectoryName(besideInput)!;
        string name = Path.GetFileNameWithoutExtension(besideInput);
        if (pages.Count == 1)
        {
            string output = OutputPaths.Beside(besideInput, format);
            await File.WriteAllBytesAsync(output, pages[0], ct);
            return output;
        }
        string folder = OutputPaths.Unique(dir, name, "", $"({format.ToUpperInvariant()})");
        Directory.CreateDirectory(folder);
        for (int i = 0; i < pages.Count; i++)
            await File.WriteAllBytesAsync(OutputPaths.Unique(folder, $"{L.T("sayfa")} {i + 1:000}", format), pages[i], ct);
        return folder;
    }

    /// <summary>Renders the chosen pages (0-based) one after another and hands each over as soon as it is ready (argument: position in <paramref name="pick"/>).</summary>
    public static Task RenderEachAsync(string pdf, IReadOnlyList<int> pick, string format, double dpi, Func<int, byte[], Task> onPage, CancellationToken ct) => Task.Run(async () =>
    {
        byte[] bytes = File.ReadAllBytes(pdf);
        var options = new PDFtoImage.RenderOptions(Dpi: (int)dpi, WithAnnotations: true, BackgroundColor: SKColors.White);
        for (int i = 0; i < pick.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            SKBitmap page;
            try { page = PDFtoImage.Conversion.ToImage(bytes, page: pick[i], options: options); }
            catch (Exception ex) { throw new InvalidOperationException("PDF açılamadı (şifreli ya da bozuk olabilir): " + ex.Message); }
            byte[] data;
            using (page)
            using (var image = SKImage.FromBitmap(page))
            using (var enc = image.Encode(format == "jpg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 90))
                data = enc.ToArray();
            await onPage(i, data);
        }
    }, ct);

    public static Task<List<byte[]>> RenderAsync(string pdf, string format, double dpi, CancellationToken ct) => Task.Run(() =>
    {
        byte[] bytes = File.ReadAllBytes(pdf);
        var result = new List<byte[]>();
        try
        {
            foreach (var page in PDFtoImage.Conversion.ToImages(bytes, options: new PDFtoImage.RenderOptions(Dpi: (int)dpi, WithAnnotations: true, BackgroundColor: SKColors.White)))
            {
                ct.ThrowIfCancellationRequested();
                using (page)
                using (var image = SKImage.FromBitmap(page))
                using (var data = image.Encode(format == "jpg" ? SKEncodedImageFormat.Jpeg : SKEncodedImageFormat.Png, 90))
                    result.Add(data.ToArray());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException("PDF açılamadı (şifreli ya da bozuk olabilir): " + ex.Message);
        }
        return result;
    }, ct);
}

/// <summary>OCR on macOS with Tesseract (brew install tesseract tesseract-lang). PDF → searchable PDF + TXT.</summary>
public static class OcrTools
{
    private static string Exe => Tools.Tesseract ?? throw new InvalidOperationException("OCR için Tesseract gerekli: Ayarlar → Araçları kur (brew install tesseract tesseract-lang).");

    private static async Task<string> LanguagesAsync(Action<string> log)
    {
        var langs = new List<string>();
        await ProcessRunner.RunAsync(Exe, ["--list-langs"], l => langs.Add(l.Trim()), CancellationToken.None);
        string pick = string.Join("+", new[] { "tur", "eng", "nld", "fra" }.Where(langs.Contains));
        if (pick.Length == 0) pick = langs.FirstOrDefault(l => l.Length == 3) ?? "eng";
        log(L.T("OCR dili: ") + pick);
        return pick;
    }

    public static async Task<string> ImageAsync(string input, Action<string> log, CancellationToken ct)
    {
        string lang = await LanguagesAsync(log);
        string png = Path.Combine(Path.GetTempPath(), $"gkrstudio-ocr-{Guid.NewGuid():N}.png");
        try
        {
            using (var bmp = ImageTools.Load(input)) ImageTools.Save(bmp, "png", png); // also handles HEIC etc.
            var text = new StringBuilder();
            int code = await ProcessRunner.RunAsync(Exe, [png, "stdout", "-l", lang], l => text.AppendLine(l), ct);
            if (code != 0) throw new InvalidOperationException("Tesseract hata kodu " + code);
            string output = OutputPaths.Beside(input, "txt", "(OCR)");
            await File.WriteAllTextAsync(output, text.ToString(), new UTF8Encoding(false), ct);
            return output;
        }
        finally { try { File.Delete(png); } catch { } }
    }

    public static async Task<string> PdfAsync(string input, string? pageSpec, bool alsoText, bool clean, Action<double> frac, Action<string> log, CancellationToken ct)
    {
        string lang = await LanguagesAsync(log);
        string work = Path.Combine(Path.GetTempPath(), "gkrstudio-ocr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            // Pages are rendered one after another while several single-threaded Tesseract processes read them in
            // parallel (much faster than one Tesseract using all cores).
            int workers = Math.Clamp(Environment.ProcessorCount - 1, 1, 8);
            var oneThread = new Dictionary<string, string> { ["OMP_THREAD_LIMIT"] = "1" };
            var queue = Channel.CreateBounded<int>(workers * 2);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            int pageCount = PdfTools.PageCount(input);
            var pick = PdfTools.Selection(pageSpec, pageCount);
            int total = pick.Count, done = 0;
            log(L.F("{0} sayfa, {1} paralel OCR", total, workers));
            // Text-only Tesseract output laid over the ORIGINAL pages keeps the file small and sharp;
            // rotated pages or an unreadable PDF fall back to Tesseract's own image + text pages.
            PdfDocument? source = null;
            try { source = PdfReader.Open(input, PdfDocumentOpenMode.Import); }
            catch (Exception ex) { log("Orijinal sayfalar kullanılamıyor, görüntü olarak kaydedilecek: " + ex.Message); }
            using var sourceDoc = source;
            var overlay = pick.Select(p => source != null && source.Pages[p].Rotate % 360 == 0).ToArray();
            string Base(int i) => Path.Combine(work, $"p{i:00000}");
            frac(0);

            var producer = Task.Run(async () =>
            {
                try
                {
                    await PdfRender.RenderEachAsync(input, pick, "png", 200, async (i, png) =>
                    {
                        await File.WriteAllBytesAsync(Base(i) + ".png", png, stop.Token);
                        await queue.Writer.WriteAsync(i, stop.Token);
                    }, stop.Token);
                    queue.Writer.Complete();
                }
                catch (Exception ex) { queue.Writer.Complete(ex); stop.Cancel(); throw; }
            });
            var consumers = Enumerable.Range(0, workers).Select(_ => Task.Run(async () =>
            {
                try
                {
                    await foreach (int i in queue.Reader.ReadAllAsync(stop.Token))
                    {
                        string[] mode = overlay[i] ? ["--dpi", "200", "-c", "textonly_pdf=1"] : [];
                        int code = await ProcessRunner.RunAsync(Exe, [Base(i) + ".png", Base(i), "-l", lang, .. mode, "pdf", "txt"], _ => { }, stop.Token, oneThread);
                        if (code != 0) throw new InvalidOperationException("Tesseract hata kodu " + code);
                        int n = Interlocked.Increment(ref done);
                        log($"Sayfa {pick[i] + 1} ({n}/{total})");
                        frac(0.97 * n / total); // the last 3 % is merging
                    }
                }
                catch { stop.Cancel(); throw; }
            })).ToList();
            var all = Task.WhenAll(consumers.Append(producer));
            try { await all; }
            catch when (all.Exception?.InnerExceptions.FirstOrDefault(e => e is not OperationCanceledException) is { } real && !ct.IsCancellationRequested)
            { throw real; }

            var text = new StringBuilder();
            using var merged = new PdfDocument();
            for (int i = 0; i < done; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (overlay[i])
                {
                    var page = merged.AddPage(source!.Pages[pick[i]]);
                    PdfTools.DropEmptyCropBox(page);
                    var media = page.MediaBox;
                    var crop = PdfTools.VisibleBox(page);
                    var form = XPdfForm.FromFile(Base(i) + ".pdf"); // kept open until the merged file is saved
                    using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                    gfx.DrawImage(form, crop.X1 - media.X1, media.Y2 - crop.Y2, crop.Width, crop.Height); // XGraphics' origin: media box top-left
                }
                else
                {
                    using var pagePdf = PdfReader.Open(Base(i) + ".pdf", PdfDocumentOpenMode.Import);
                    foreach (var p in pagePdf.Pages) merged.AddPage(p);
                }
                if (File.Exists(Base(i) + ".txt")) text.AppendLine(await File.ReadAllTextAsync(Base(i) + ".txt", ct));
            }
            frac(0.99);
            string suffix = total == pageCount ? "(OCR)" : $"(OCR {PdfTools.SelectionLabel(pick)})";
            string output = OutputPaths.Beside(input, "pdf", suffix);
            using (var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write)) merged.Save(fs);
            if (alsoText)
            {
                string txt = OutputPaths.Beside(input, "txt", suffix);
                await File.WriteAllTextAsync(txt, text.ToString(), new UTF8Encoding(false), ct);
                log(L.T("Metin ayrıca kaydedildi: ") + Path.GetFileName(txt));
            }
            return output;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }
}

/// <summary>macOS parts of DocTools: RTF via the system's textutil, page images via PDF rendering.</summary>
public static partial class DocTools
{
    public static List<Block> FromRtf(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            var psi = new ProcessStartInfo("/usr/bin/textutil") { RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var a in new[] { "-convert", "txt", "-stdout", path }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return FromText(text);
        }
        // fallback: strip RTF control words
        string raw = File.ReadAllText(path);
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\\par[d]?", "\n");
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\\'([0-9a-f]{2})", m => ((char)Convert.ToInt32(m.Groups[1].Value, 16)).ToString());
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\\u(-?\d+)\??", m => ((char)int.Parse(m.Groups[1].Value)).ToString());
        raw = System.Text.RegularExpressions.Regex.Replace(raw, @"\\[a-z]+-?\d* ?|[{}]", "");
        return FromText(raw);
    }

    public static string RenderImages(List<Block> blocks, string format, string besideInput)
    {
        string temp = Path.Combine(Path.GetTempPath(), $"gkrstudio-doc-{Guid.NewGuid():N}.pdf");
        try
        {
            WritePdf(blocks, temp);
            return PdfRender.PagesToImagesAsync(temp, format, besideInput, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally { TryDelete(temp); }
    }
}

/// <summary>macOS part of PdfTools: images → PDF.</summary>
public static partial class PdfTools
{
    public static string ImagesToPdf(IReadOnlyList<string> images)
    {
        var streams = new List<MemoryStream>();
        try
        {
            using var doc = new PdfDocument();
            foreach (var path in images)
            {
                using var src = ImageTools.Load(path);
                using var flat = ImageTools.Flatten(src);
                using var img = SKImage.FromBitmap(flat);
                byte[] jpg = img.Encode(SKEncodedImageFormat.Jpeg, 92).ToArray();
                var ms = new MemoryStream(jpg, 0, jpg.Length, writable: false, publiclyVisible: true);
                streams.Add(ms);
                using var image = XImage.FromStream(ms);
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(src.Width * 0.75);
                page.Height = XUnit.FromPoint(src.Height * 0.75);
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
            }
            string output = images.Count == 1 ? OutputPaths.Beside(images[0], "pdf") : OutputPaths.Beside(images[0], "pdf", $"(+{images.Count - 1})");
            using var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
            doc.Save(fs);
            return output;
        }
        finally { foreach (var s in streams) s.Dispose(); }
    }
}

/// <summary>macOS image decoding for QrTools.</summary>
public static partial class QrTools
{
    private static List<string> DecodeFile(string path)
    {
        using var bmp = ImageTools.Load(path);
        return Decode(bmp);
    }

    private static List<string> DecodeEncoded(byte[] png)
    {
        using var bmp = SKBitmap.Decode(png);
        return Decode(bmp);
    }

    private static List<string> Decode(SKBitmap bmp)
    {
        using var bgra = bmp.ColorType == SKColorType.Bgra8888 ? bmp.Copy() : bmp.Copy(SKColorType.Bgra8888);
        return DecodeBgra(bgra.Bytes, bgra.Width, bgra.Height);
    }
}

/// <summary>Clipboard text via pbpaste (works from any thread, no UI needed).</summary>
public static class ClipboardReader
{
    public static string? ReadText()
    {
        try
        {
            if (!OperatingSystem.IsMacOS()) return null;
            var psi = new ProcessStartInfo("/usr/bin/pbpaste") { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(2000);
            return text;
        }
        catch { return null; }
    }

    public static LinkInfo? ReadLink() => LinkParser.Parse(ReadText());

    public static void WriteText(string text)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var psi = new ProcessStartInfo("/usr/bin/pbcopy") { RedirectStandardInput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(text);
        p.StandardInput.Close();
        p.WaitForExit(2000);
    }
}
