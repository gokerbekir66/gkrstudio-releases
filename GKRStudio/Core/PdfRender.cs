using System.IO;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using WinPdf = Windows.Data.Pdf.PdfDocument;

namespace GKRStudio.Core;

/// <summary>Renders PDF pages with Windows' built-in PDF engine (Windows.Data.Pdf).</summary>
public static class PdfRender
{
    /// <summary>Each page → PNG/JPG (≈150 DPI). One page → a file next to <paramref name="besideInput"/>; more → a folder.</summary>
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

    /// <summary>Encoded page images (PNG or JPG) in page order.</summary>
    public static async Task<List<byte[]>> RenderAsync(string pdf, string format, double dpi, CancellationToken ct)
    {
        var doc = await OpenAsync(pdf);
        var result = new List<byte[]>();
        for (uint i = 0; i < doc.PageCount; i++)
            result.Add(await RenderPageAsync(doc, i, format, dpi, ct));
        return result;
    }

    /// <summary>Renders the chosen pages (0-based) one after another (argument: position in <paramref name="pick"/>); same shape as the Mac version.</summary>
    public static async Task RenderEachAsync(string pdf, IReadOnlyList<int> pick, string format, double dpi, Func<int, byte[], Task> onPage, CancellationToken ct)
    {
        var doc = await OpenAsync(pdf);
        for (int i = 0; i < pick.Count; i++)
            await onPage(i, await RenderPageAsync(doc, (uint)pick[i], format, dpi, ct));
    }

    public static async Task<WinPdf> OpenAsync(string pdf)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(pdf));
        try { return await WinPdf.LoadFromFileAsync(file); }
        catch (Exception ex) { throw new InvalidOperationException("PDF açılamadı (şifreli ya da bozuk olabilir): " + ex.Message); }
    }

    /// <summary>One page → encoded PNG/JPG at <paramref name="dpi"/>.</summary>
    public static async Task<byte[]> RenderPageAsync(WinPdf doc, uint index, string format, double dpi, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var page = doc.GetPage(index);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, page.Size.Width * dpi / 96.0),
            BitmapEncoderId = format == "jpg" ? Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId : Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
            BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
        };
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);
        stream.Seek(0);
        using var ms = new MemoryStream();
        await stream.AsStreamForRead().CopyToAsync(ms, ct);
        return ms.ToArray();
    }
}
