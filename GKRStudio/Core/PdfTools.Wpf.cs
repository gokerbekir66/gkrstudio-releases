using System.IO;
using System.Windows.Media.Imaging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace GKRStudio.Core;

/// <summary>Windows (WPF) part of PdfTools: images → PDF.</summary>
public static partial class PdfTools
{
    /// <summary>One page per image, page size = image size at 96 DPI. Must run on an STA thread (WPF decoding).</summary>
    public static string ImagesToPdf(IReadOnlyList<string> images)
    {
        var streams = new List<MemoryStream>();
        try
        {
            using var doc = new PdfDocument();
            foreach (var path in images)
            {
                BitmapSource src = ImageTools.Load(path);
                var enc = new JpegBitmapEncoder { QualityLevel = 92 };
                enc.Frames.Add(BitmapFrame.Create(ImageTools.Flatten(src)));
                var ms = new MemoryStream();
                enc.Save(ms);
                ms.Position = 0;
                streams.Add(ms);

                using var image = XImage.FromStream(ms);
                var page = doc.AddPage();
                page.Width = XUnit.FromPoint(src.PixelWidth * 0.75);
                page.Height = XUnit.FromPoint(src.PixelHeight * 0.75);
                using var gfx = XGraphics.FromPdfPage(page);
                gfx.DrawImage(image, 0, 0, page.Width.Point, page.Height.Point);
            }
            string output = images.Count == 1
                ? OutputPaths.Beside(images[0], "pdf")
                : OutputPaths.Beside(images[0], "pdf", $"(+{images.Count - 1} görsel)");
            Save(doc, output);
            return output;
        }
        finally
        {
            foreach (var s in streams) s.Dispose();
        }
    }

}
