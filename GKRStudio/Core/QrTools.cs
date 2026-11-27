using System.IO;
using System.Text;
using ZXing;
using ZXing.Common;

namespace GKRStudio.Core;

/// <summary>QR / barcode reading (ZXing.Net) for images and PDF pages. Writes a text report next to the input.
/// Image decoding to BGRA pixels is platform-specific (DecodeFile / DecodeEncoded).</summary>
public static partial class QrTools
{
    public static async Task<string> ScanAsync(string input, Action<string> log, CancellationToken ct)
    {
        var found = new List<string>();
        if (Catalog.KindOf(input) == InputKind.Pdf)
        {
            var pages = await PdfRender.RenderAsync(input, "png", 200, ct);
            for (int i = 0; i < pages.Count; i++)
                foreach (var text in await Sta.Run(() => DecodeEncoded(pages[i])))
                    found.Add($"Sayfa {i + 1}: {text}");
        }
        else
        {
            found.AddRange(await Sta.Run(() => DecodeFile(input)));
        }

        foreach (var f in found) log("QR: " + f);
        if (found.Count == 0) log(L.T("QR kod bulunamadı."));
        string report = OutputPaths.Beside(input, "txt", "(QR)");
        var sb = new StringBuilder($"GKR Studio QR — {Path.GetFileName(input)} — {DateTime.Now:g}\r\n\r\n");
        sb.AppendLine(found.Count == 0 ? L.T("QR kod bulunamadı.") : string.Join("\r\n", found));
        await File.WriteAllTextAsync(report, sb.ToString(), new UTF8Encoding(false), ct);
        return report;
    }

    /// <summary>Decodes QR / Data Matrix / Aztec codes from 32-bit BGRA pixels.</summary>
    public static List<string> DecodeBgra(byte[] pixels, int width, int height)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = [BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC] },
        };
        var source = new RGBLuminanceSource(pixels, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        return (reader.DecodeMultiple(source) ?? []).Select(r => r.Text).Distinct().ToList();
    }
}
