using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GKRStudio.Core;

/// <summary>Image tools on the built-in WIC/WPF codecs; ffmpeg is used for WEBP output and as decode fallback.
/// GIF input uses its first frame. Outputs never overwrite existing files.</summary>
public static class ImageTools
{
    public static async Task<string> RunAsync(string id, string input, IReadOnlyDictionary<string, string> o, Action<string> log, CancellationToken ct)
    {
        if (id is "img:webp" or "img:avif") return await ViaFfmpegAsync(id[4..], input, log, ct);
        try
        {
            return await Sta.Run(() => RunWpf(id, input, o));
        }
        catch (Exception ex) when (IsCodecError(ex))
        {
            if (Tools.Ffmpeg == null || id is not ("img:png" or "img:jpg" or "img:bmp" or "img:tiff" or "img:gif" or "img:ico"))
                throw new InvalidOperationException($"Bu görsel Windows codec'leriyle açılamadı ({ex.Message}).");
            log("Windows codec'i yok, ffmpeg ile deneniyor…");
            return await ViaFfmpegAsync(id["img:".Length..], input, log, ct);
        }
    }

    public static bool IsCodecError(Exception ex) => ex is NotSupportedException or FileFormatException or COMException or ArgumentException;

    private static string RunWpf(string id, string input, IReadOnlyDictionary<string, string> o)
    {
        switch (id)
        {
            case "img:strip":
                return StripMetadata(input);
            case "img:adjust":
            {
                BitmapSource img = Load(input);
                if (int.TryParse(o.GetValueOrDefault("percent"), out int pct) && pct > 0 && pct != 100)
                    img = new TransformedBitmap(img, new ScaleTransform(pct / 100.0, pct / 100.0));
                double angle = o.GetValueOrDefault("rotate") switch { "90° sağa" => 90, "90° sola" => 270, "180°" => 180, _ => 0 };
                if (angle != 0) img = new TransformedBitmap(img, new RotateTransform(angle));
                if (o.GetValueOrDefault("flip") == "Yatay") img = new TransformedBitmap(img, new ScaleTransform(-1, 1));
                int bri = Int(o, "brightness"), con = Int(o, "contrast"), sat = Int(o, "saturation");
                if (bri != 0 || con != 0 || sat != 0) img = AdjustColors(img, bri, con, sat);
                string ext = SameFormat(input);
                string output = OutputPaths.Beside(input, ext, "(düzenlendi)");
                Save(img, ext, output);
                return output;
            }
            case "img:frame":
            {
                string output = OutputPaths.Beside(input, "png", "(çerçeve)");
                Save(Frame(Load(input), o), "png", output);
                return output;
            }
            case "img:redact":
            {
                var src = Load(input);
                var rect = new Int32Rect(Int(o, "x"), Int(o, "y"), Int(o, "w"), Int(o, "h"));
                string ext = SameFormat(input);
                string output = OutputPaths.Beside(input, ext, "(sansürlü)");
                Save(Redact(src, rect, o.GetValueOrDefault("style", "Bulanık")), ext, output);
                return output;
            }
            case "img:ico":
            {
                string output = OutputPaths.Beside(input, "ico");
                WriteIco(Load(input), output);
                return output;
            }
            case "img:jpeg":
            {
                int q = Math.Clamp(int.TryParse(o.GetValueOrDefault("quality"), out int v) ? v : 75, 1, 100);
                string output = OutputPaths.Beside(input, "jpg", $"(q{q})");
                Save(Load(input), "jpg", output, q);
                return output;
            }
            case "img:resize":
            {
                var src = Load(input);
                var (w, h) = TargetSize(src.PixelWidth, src.PixelHeight, o);
                var scaled = new TransformedBitmap(src, new ScaleTransform((double)w / src.PixelWidth, (double)h / src.PixelHeight));
                string ext = SameFormat(input);
                string output = OutputPaths.Beside(input, ext, $"({w}x{h})");
                Save(scaled, ext, output);
                return output;
            }
            case "img:rotate":
            {
                double angle = o.GetValueOrDefault("angle") switch { "90° sola" => 270, "180°" => 180, _ => 90 };
                var rotated = new TransformedBitmap(Load(input), new RotateTransform(angle));
                string ext = SameFormat(input);
                string output = OutputPaths.Beside(input, ext, "(döndürüldü)");
                Save(rotated, ext, output);
                return output;
            }
            case "img:flip":
            {
                var flipped = new TransformedBitmap(Load(input), new ScaleTransform(-1, 1));
                string ext = SameFormat(input);
                string output = OutputPaths.Beside(input, ext, "(yansıtıldı)");
                Save(flipped, ext, output);
                return output;
            }
            default:
            {
                string format = id["img:".Length..];
                string output = OutputPaths.Beside(input, format == "tiff" ? "tif" : format);
                Save(Load(input), format, output);
                return output;
            }
        }
    }

    private static (int W, int H) TargetSize(int w0, int h0, IReadOnlyDictionary<string, string> o)
    {
        int.TryParse(o.GetValueOrDefault("width"), out int w);
        int.TryParse(o.GetValueOrDefault("height"), out int h);
        if (w > 0 && h <= 0) h = (int)Math.Round(h0 * (double)w / w0);
        else if (h > 0 && w <= 0) w = (int)Math.Round(w0 * (double)h / h0);
        else if (w <= 0 && h <= 0)
        {
            double pct = double.TryParse(o.GetValueOrDefault("percent")?.Replace(',', '.'), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double p) ? p : 50;
            pct = Math.Clamp(pct, 1, 1000);
            w = (int)Math.Round(w0 * pct / 100);
            h = (int)Math.Round(h0 * pct / 100);
        }
        return (Math.Clamp(w, 1, 30000), Math.Clamp(h, 1, 30000));
    }

    /// <summary>Output format matching the input when WPF can encode it, PNG otherwise.</summary>
    public static string SameFormat(string input)
    {
        string ext = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
        if (ext is "jpeg" or "jpe" or "jfif") ext = "jpg";
        if (ext == "tiff") ext = "tif";
        return ext is "png" or "jpg" or "bmp" or "tif" or "gif" ? ext : "png";
    }

    /// <summary>Decodes the first frame (GIF: first frame) and applies EXIF orientation.</summary>
    public static BitmapSource Load(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        BitmapFrame frame = decoder.Frames[0];
        BitmapSource result = frame;
        int orientation = Orientation(frame);
        double angle = orientation switch { 3 => 180, 6 => 90, 8 => 270, _ => 0 };
        if (angle != 0) result = new TransformedBitmap(frame, new RotateTransform(angle));
        result.Freeze();
        return result;
    }

    private static int Orientation(BitmapFrame frame)
    {
        try
        {
            if (frame.Metadata is BitmapMetadata meta && meta.GetQuery("System.Photo.Orientation") is ushort o) return o;
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException or COMException) { }
        return 1;
    }

    public static void Save(BitmapSource src, string format, string output, int jpegQuality = 92)
    {
        BitmapEncoder encoder = format switch
        {
            "png" => new PngBitmapEncoder(),
            "jpg" => new JpegBitmapEncoder { QualityLevel = jpegQuality },
            "bmp" => new BmpBitmapEncoder(),
            "tif" or "tiff" => new TiffBitmapEncoder { Compression = TiffCompressOption.Lzw },
            "gif" => new GifBitmapEncoder(),
            _ => throw new NotSupportedException("Desteklenmeyen hedef biçim: " + format),
        };
        if (format is "jpg" or "bmp") src = Flatten(src); // no alpha channel: composite on white instead of black
        encoder.Frames.Add(BitmapFrame.Create(Detach(src)));
        using var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        encoder.Save(fs);
    }

    /// <summary>Copies pixels only, so no metadata from the source frame travels into the output.</summary>
    private static BitmapSource Detach(BitmapSource src)
    {
        var copy = new WriteableBitmap(src);
        copy.Freeze();
        return copy;
    }

    public static BitmapSource Flatten(BitmapSource src)
    {
        var rect = new Rect(0, 0, src.PixelWidth, src.PixelHeight);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, rect);
            dc.DrawImage(src, rect);
        }
        var rtb = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    private static int Int(IReadOnlyDictionary<string, string> o, string key) =>
        int.TryParse(o.GetValueOrDefault(key)?.Trim().TrimStart('+'), out int v) ? v : 0;

    // ---------- colour, frame, redaction ----------

    /// <summary>brightness / contrast / saturation in -100..100.</summary>
    private static BitmapSource AdjustColors(BitmapSource src, int brightness, int contrast, int saturation)
    {
        var bgra = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        double b = Math.Clamp(brightness, -100, 100) * 2.55;
        double c = Math.Clamp(contrast, -100, 100) * 2.55;
        double cf = 259 * (c + 255) / (255 * (259 - c));
        double sf = 1 + Math.Clamp(saturation, -100, 100) / 100.0;
        for (int i = 0; i < px.Length; i += 4)
        {
            double bl = px[i], gr = px[i + 1], rd = px[i + 2];
            double gray = 0.114 * bl + 0.587 * gr + 0.299 * rd;
            bl = gray + (bl - gray) * sf; gr = gray + (gr - gray) * sf; rd = gray + (rd - gray) * sf;
            px[i] = Clamp(cf * (bl + b - 128) + 128);
            px[i + 1] = Clamp(cf * (gr + b - 128) + 128);
            px[i + 2] = Clamp(cf * (rd + b - 128) + 128);
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return result;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);

    /// <summary>Padding + background (solid or gradient) + rounded corners + soft shadow, rendered through WPF.</summary>
    private static BitmapSource Frame(BitmapSource src, IReadOnlyDictionary<string, string> o)
    {
        int w = src.PixelWidth, h = src.PixelHeight;
        int pad = Math.Max(0, (int)(Math.Min(w, h) * Math.Clamp(Int(o, "padding"), 0, 50) / 100.0));
        double radius = Math.Min(w, h) * Math.Clamp(Int(o, "radius"), 0, 50) / 100.0;
        Brush bg = o.GetValueOrDefault("background", "Beyaz") switch
        {
            "Siyah" => Brushes.Black,
            "Gri" => new SolidColorBrush(Color.FromRgb(0xE9, 0xEB, 0xEF)),
            "Mavi degrade" => new LinearGradientBrush(Color.FromRgb(0x5B, 0x8C, 0xFF), Color.FromRgb(0x9B, 0x5B, 0xFF), 45),
            "Gün batımı" => new LinearGradientBrush(Color.FromRgb(0xFF, 0xC2, 0x3D), Color.FromRgb(0xE5, 0x62, 0x1C), 45),
            "Şeffaf" => Brushes.Transparent,
            _ => Brushes.White,
        };
        var image = new System.Windows.Controls.Image { Source = src, Width = w, Height = h, Stretch = Stretch.Fill };
        if (radius > 0) image.Clip = new RectangleGeometry(new Rect(0, 0, w, h), radius, radius);
        var holder = new System.Windows.Controls.Border { Width = w, Height = h, Child = image };
        if (o.GetValueOrDefault("shadow", "Var") == "Var" && pad > 0)
            holder.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = Math.Max(8, pad * 0.6), ShadowDepth = pad * 0.15, Opacity = 0.45, Direction = 270 };
        var root = new System.Windows.Controls.Border { Background = bg, Padding = new Thickness(pad), Child = holder, Width = w + 2 * pad, Height = h + 2 * pad };
        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();
        var rtb = new RenderTargetBitmap(w + 2 * pad, h + 2 * pad, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        rtb.Freeze();
        return rtb;
    }

    private static BitmapSource Redact(BitmapSource src, Int32Rect r, string style)
    {
        r.X = Math.Clamp(r.X, 0, src.PixelWidth - 1);
        r.Y = Math.Clamp(r.Y, 0, src.PixelHeight - 1);
        r.Width = Math.Clamp(r.Width, 1, src.PixelWidth - r.X);
        r.Height = Math.Clamp(r.Height, 1, src.PixelHeight - r.Y);
        var area = new Rect(r.X, r.Y, r.Width, r.Height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(src, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
            if (style == "Siyah")
            {
                dc.DrawRectangle(Brushes.Black, null, area);
            }
            else
            {
                // shrink the area a lot, then draw it back stretched: nearest = pixelate, smooth = blur
                double factor = style == "Piksel" ? Math.Max(8, Math.Min(r.Width, r.Height) / 12.0) : Math.Max(12, Math.Min(r.Width, r.Height) / 6.0);
                var small = new TransformedBitmap(new CroppedBitmap(src, r), new ScaleTransform(Math.Max(1.0 / r.Width, 1 / factor), Math.Max(1.0 / r.Height, 1 / factor)));
                var group = new DrawingGroup();
                RenderOptions.SetBitmapScalingMode(group, style == "Piksel" ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
                using (var g = group.Open()) g.DrawImage(small, area);
                dc.DrawDrawing(group);
            }
        }
        var rtb = new RenderTargetBitmap(src.PixelWidth, src.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    // ---------- metadata removal ----------

    /// <summary>JPEG/PNG: lossless removal of EXIF/XMP/IPTC/comment/text chunks. Other formats: pixel-only re-encode.</summary>
    private static string StripMetadata(string input)
    {
        string ext = SameFormat(input);
        string output = OutputPaths.Beside(input, ext, "(metadata yok)");
        byte[] data = File.ReadAllBytes(input);

        if (ext == "jpg" && data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            using var fs0 = new FileStream(input, FileMode.Open, FileAccess.Read, FileShare.Read);
            var frame = BitmapDecoder.Create(fs0, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
            if (Orientation(frame) == 1) // removing EXIF would otherwise change how a rotated photo is displayed
            {
                File.WriteAllBytes(output, StripJpeg(data));
                return output;
            }
        }
        else if (ext == "png" && data.Length > 8 && data[0] == 0x89 && data[1] == (byte)'P')
        {
            File.WriteAllBytes(output, StripPng(data));
            return output;
        }
        Save(Load(input), ext, output, 95);
        return output;
    }

    private static byte[] StripJpeg(byte[] d)
    {
        using var ms = new MemoryStream(d.Length);
        ms.Write(d, 0, 2);
        int i = 2;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) throw new FileFormatException("Bozuk JPEG.");
            byte marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; } // fill byte
            if (marker == 0xDA) { ms.Write(d, i, d.Length - i); return ms.ToArray(); } // start of scan: copy the rest
            int len = (d[i + 2] << 8) | d[i + 3];
            if (len < 2 || i + 2 + len > d.Length) throw new FileFormatException("Bozuk JPEG segmenti.");
            bool drop = (marker >= 0xE1 && marker <= 0xEF && marker != 0xE2 && marker != 0xEE) || marker == 0xFE; // keep ICC (APP2) + Adobe (APP14)
            if (!drop) ms.Write(d, i, 2 + len);
            i += 2 + len;
        }
        throw new FileFormatException("JPEG görüntü verisi bulunamadı.");
    }

    private static byte[] StripPng(byte[] d)
    {
        string[] dropped = ["tEXt", "zTXt", "iTXt", "eXIf", "tIME"];
        using var ms = new MemoryStream(d.Length);
        ms.Write(d, 0, 8);
        int i = 8;
        while (i + 12 <= d.Length)
        {
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(i));
            if (len < 0 || i + 12 + len > d.Length) throw new FileFormatException("Bozuk PNG.");
            string type = System.Text.Encoding.ASCII.GetString(d, i + 4, 4);
            if (!dropped.Contains(type)) ms.Write(d, i, 12 + len);
            i += 12 + len;
            if (type == "IEND") break;
        }
        return ms.ToArray();
    }

    // ---------- ICO / ffmpeg ----------

    private static void WriteIco(BitmapSource src, string output)
    {
        const int size = 256;
        double scale = Math.Min((double)size / src.PixelWidth, (double)size / src.PixelHeight);
        double w = src.PixelWidth * scale, h = src.PixelHeight * scale;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawImage(src, new Rect((size - w) / 2, (size - h) / 2, w, h));
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        png.Save(ms);
        byte[] data = ms.ToArray();

        using var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)1);
        bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0);
        bw.Write((ushort)1); bw.Write((ushort)32);
        bw.Write(data.Length); bw.Write(6 + 16);
        bw.Write(data);
    }

    private static async Task<string> ViaFfmpegAsync(string format, string input, Action<string> log, CancellationToken ct)
    {
        Ffmpeg.Require();
        string ext = format == "tiff" ? "tif" : format;
        string output = OutputPaths.Beside(input, ext);
        var args = new List<string> { "-i", input };
        switch (format)
        {
            case "webp": args.AddRange(["-c:v", "libwebp", "-quality", "85"]); break;
            case "avif": args.AddRange(["-c:v", "libaom-av1", "-still-picture", "1", "-crf", "30", "-b:v", "0", "-pix_fmt", "yuv420p"]); break;
            case "jpg": args.AddRange(["-q:v", "2"]); break;
            case "ico": args.AddRange(["-vf", "scale=256:256:force_original_aspect_ratio=decrease"]); break;
        }
        args.AddRange(["-frames:v", "1", "-update", "1", output]);
        await Ffmpeg.RunAsync(args, _ => { }, log, ct);
        return output;
    }
}

/// <summary>WPF imaging types are happiest on an STA thread.</summary>
public static class Sta
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
