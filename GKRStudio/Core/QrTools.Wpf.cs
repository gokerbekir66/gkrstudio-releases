using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GKRStudio.Core;

/// <summary>Windows (WPF/WIC) image decoding for QrTools.</summary>
public static partial class QrTools
{
    private static List<string> DecodeFile(string path) => Decode(ImageTools.Load(path));

    private static List<string> DecodeEncoded(byte[] png)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(png);
        bmp.EndInit();
        bmp.Freeze();
        return Decode(bmp);
    }

    private static List<string> Decode(BitmapSource src)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var pixels = new byte[stride * h];
        bgra.CopyPixels(pixels, stride, 0);
        return DecodeBgra(pixels, w, h);
    }
}
