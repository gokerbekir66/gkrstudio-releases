using System.IO;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace GKRStudio.Core;

/// <summary>PDF merge / split / page selection and image → PDF export (PDFsharp, MIT).</summary>
public static partial class PdfTools
{
    public static int PageCount(string path)
    {
        using var doc = Open(path);
        return doc.PageCount;
    }

    public static string Merge(IReadOnlyList<string> inputs, Action<string> log)
    {
        var files = inputs.OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase).ToList();
        log("Sıra: " + string.Join(" → ", files.Select(Path.GetFileName)));
        using var output = new PdfDocument();
        foreach (var file in files)
        {
            using var doc = Open(file);
            foreach (var page in doc.Pages) output.AddPage(page);
        }
        string path = OutputPaths.Beside(files[0], "pdf", "(birleştirildi)");
        Save(output, path);
        return path;
    }

    public static string Split(string input, Action<string> log)
    {
        using var doc = Open(input);
        string dir = OutputPaths.Unique(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input), "", "(sayfalar)");
        Directory.CreateDirectory(dir);
        string name = Path.GetFileNameWithoutExtension(input);
        for (int i = 0; i < doc.PageCount; i++)
        {
            using var single = new PdfDocument();
            single.AddPage(doc.Pages[i]);
            Save(single, OutputPaths.Unique(dir, $"{name} - {L.T("sayfa")} {i + 1:000}", "pdf"));
        }
        log($"{doc.PageCount} sayfa ayrıldı.");
        return dir;
    }

    /// <summary>Builds a new PDF from the given 1-based page spec, in the given order ("3,1,2,5-7", "9-7").</summary>
    public static string SelectPages(string input, string spec, Action<string> log)
    {
        using var doc = Open(input);
        var pages = ParseSpec(spec, doc.PageCount);
        using var output = new PdfDocument();
        foreach (int p in pages) output.AddPage(doc.Pages[p - 1]);
        string path = OutputPaths.Beside(input, "pdf", "(sayfalar)");
        Save(output, path);
        log("Sayfa sırası: " + string.Join(", ", pages));
        return path;
    }

    /// <summary>Pages in the given order (deleting the ones left out), optionally rotated.</summary>
    public static string Organize(string input, string spec, int rotate, Action<string> log)
    {
        using var doc = Open(input);
        var pages = ParseSpec(spec, doc.PageCount);
        using var output = new PdfDocument();
        foreach (int p in pages)
        {
            var page = output.AddPage(doc.Pages[p - 1]);
            if (rotate != 0) page.Rotate = (page.Rotate + rotate) % 360;
        }
        string path = OutputPaths.Beside(input, "pdf", "(düzenlendi)");
        Save(output, path);
        log($"Sayfalar: {string.Join(", ", pages)}{(rotate != 0 ? $" · {rotate}° döndürüldü" : "")}");
        return path;
    }

    public static string Compress(string input, Action<string> log)
    {
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        doc.Options.NoCompression = false;
        doc.Options.CompressContentStreams = true;
        doc.Options.FlateEncodeMode = PdfFlateEncodeMode.BestCompression;
        doc.Options.UseFlateDecoderForJpegImages = PdfUseFlateDecoderForJpegImages.Automatic;
        doc.Options.EnableCcittCompressionForBilevelImages = true;
        string path = OutputPaths.Beside(input, "pdf", "(küçük)");
        Save(doc, path);
        long before = new FileInfo(input).Length, after = new FileInfo(path).Length;
        log($"{before / 1024} KB → {after / 1024} KB ({(before > 0 ? 100 - after * 100 / before : 0)}% küçük)");
        if (after >= before) log("Not: bu PDF zaten sıkıştırılmış; daha fazla küçültme için görselleri yeniden sıkıştırmak gerekir.");
        return path;
    }

    public static string StripMetadata(string input)
    {
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        doc.Info.Elements.Clear();
        doc.Internals.Catalog.Elements.Remove("/Metadata");
        string path = OutputPaths.Beside(input, "pdf", "(metadata yok)");
        Save(doc, path);
        return path;
    }

    /// <summary>"1-20, 35" → 0-based pages without duplicates; empty → every page.</summary>
    public static List<int> Selection(string? spec, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(spec)) return Enumerable.Range(0, pageCount).ToList();
        var pages = ParseSpec(spec, pageCount).Select(p => p - 1).Distinct().ToList();
        if (pages.Count == 0) throw new InvalidOperationException("Geçerli sayfa yok.");
        return pages;
    }

    /// <summary>Short label for the output name: "s1-20" or "s1-20,35".</summary>
    public static string SelectionLabel(List<int> pages)
    {
        var parts = new List<string>();
        for (int i = 0; i < pages.Count;)
        {
            int j = i;
            while (j + 1 < pages.Count && pages[j + 1] == pages[j] + 1) j++;
            parts.Add(i == j ? $"{pages[i] + 1}" : $"{pages[i] + 1}-{pages[j] + 1}");
            i = j + 1;
        }
        string label = "s" + string.Join(",", parts);
        return label.Length > 40 ? label[..37] + "…" : label;
    }

    /// <summary>
    /// The visible page area: the crop box when the page has a real one, otherwise the media box.
    /// Never touch <c>PdfPage.CropBox</c> for this: its getter silently adds an empty [0 0 0 0] crop box,
    /// which Acrobat shows as a zero-size blank page.
    /// </summary>
    public static PdfRectangle VisibleBox(PdfPage page)
    {
        if (page.Elements.ContainsKey("/CropBox"))
        {
            var crop = page.Elements.GetRectangle("/CropBox");
            if (crop.Width > 1 && crop.Height > 1) return crop;
        }
        return page.MediaBox;
    }

    /// <summary>Removes an empty/degenerate /CropBox (it makes viewers show a zero-size page).</summary>
    public static void DropEmptyCropBox(PdfPage page)
    {
        if (!page.Elements.ContainsKey("/CropBox")) return;
        var crop = page.Elements.GetRectangle("/CropBox");
        if (crop.Width <= 1 || crop.Height <= 1) page.Elements.Remove("/CropBox");
    }

    public static List<int> ParseSpec(string spec, int pageCount)
    {
        var result = new List<int>();
        foreach (var part in spec.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ends = part.Split('-', 2, StringSplitOptions.TrimEntries);
            if (!int.TryParse(ends[0], out int a) || (ends.Length == 2 && !int.TryParse(ends[1], out _)))
                throw new FormatException($"Geçersiz sayfa: \"{part}\" (örnek: 3,1,2,5-7)");
            int b = ends.Length == 2 ? int.Parse(ends[1]) : a;
            if (a < 1 || b < 1 || a > pageCount || b > pageCount)
                throw new FormatException($"Sayfa {part} aralık dışında (1-{pageCount}).");
            int step = b >= a ? 1 : -1;
            for (int p = a; ; p += step) { result.Add(p); if (p == b) break; }
        }
        if (result.Count == 0) throw new FormatException("En az bir sayfa girin.");
        return result;
    }

    private static PdfDocument Open(string path)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Import);
        }
        catch (PdfReaderException ex)
        {
            throw new InvalidOperationException($"{Path.GetFileName(path)} açılamadı (şifreli ya da bozuk olabilir): {ex.Message}");
        }
    }

    private static void Save(PdfDocument doc, string path)
    {
        using var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        doc.Save(fs);
    }
}
