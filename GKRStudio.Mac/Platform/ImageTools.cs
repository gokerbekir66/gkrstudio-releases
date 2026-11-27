using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using SkiaSharp;

namespace GKRStudio.Core;

/// <summary>Image tools for macOS on SkiaSharp. PNG/JPG/WEBP are encoded by Skia; BMP/GIF/TIFF/ICO/AVIF go through ffmpeg.
/// HEIC and other formats Skia can't read are decoded with macOS's own <c>sips</c> (or ffmpeg).</summary>
public static class ImageTools
{
    public static async Task<string> RunAsync(string id, string input, IReadOnlyDictionary<string, string> o, Action<string> log, CancellationToken ct)
    {
        return await Task.Run(() => Run(id, input, o, log), ct);
    }

    private static string Run(string id, string input, IReadOnlyDictionary<string, string> o, Action<string> log)
    {
        switch (id)
        {
            case "img:strip": return StripMetadata(input);
            case "img:jpeg":
            {
                int q = Math.Clamp(Int(o, "quality", 75), 1, 100);
                string output = OutputPaths.Beside(input, "jpg", $"(q{q})");
                using var bmp = Load(input);
                Save(bmp, "jpg", output, q);
                return output;
            }
            case "img:resize":
            {
                using var src = Load(input);
                var (w, h) = TargetSize(src.Width, src.Height, o);
                using var scaled = src.Resize(new SKImageInfo(w, h), SKFilterQuality.High);
                string ext = SameFormat(input);
                string output = OutputPaths.Beside(input, ext, $"({w}x{h})");
                Save(scaled, ext, output);
                return output;
            }
            case "img:rotate":
            {
                int angle = o.GetValueOrDefault("angle") switch { "90° sola" => 270, "180°" => 180, _ => 90 };
                using var src = Load(input);
                using var rotated = Rotate(src, angle);
                string ext = SameFormat(input);
                string output = OutputPaths.Beside(input, ext, "(döndürüldü)");
                Save(rotated, ext, output);
                return output;
            }
            case "img:flip":
            {
                using var src = Load(input);
                using var flipped = Flip(src);
                string ext = SameFormat(input);
                string output = OutputPaths.Beside(input, ext, "(yansıtıldı)");
                Save(flipped, ext, output);
                return output;
            }
            case "img:adjust":
            {
                SKBitmap img = Load(input);
                int pct = Int(o, "percent", 100);
                if (pct > 0 && pct != 100) { var r = img.Resize(new SKImageInfo(Math.Max(1, img.Width * pct / 100), Math.Max(1, img.Height * pct / 100)), SKFilterQuality.High); img.Dispose(); img = r; }
                int angle = o.GetValueOrDefault("rotate") switch { "90° sağa" => 90, "90° sola" => 270, "180°" => 180, _ => 0 };
                if (angle != 0) { var r = Rotate(img, angle); img.Dispose(); img = r; }
                if (o.GetValueOrDefault("flip") == "Yatay") { var r = Flip(img); img.Dispose(); img = r; }
                int bri = Int(o, "brightness", 0), con = Int(o, "contrast", 0), sat = Int(o, "saturation", 0);
                if (bri != 0 || con != 0 || sat != 0) { var r = Adjust(img, bri, con, sat); img.Dispose(); img = r; }
                string ext = SameFormat(input);
                string output = OutputPaths.Beside(input, ext, "(düzenlendi)");
                Save(img, ext, output);
                img.Dispose();
                return output;
            }
            case "img:frame":
            {
                using var src = Load(input);
                using var framed = Frame(src, o);
                string output = OutputPaths.Beside(input, "png", "(çerçeve)");
                Save(framed, "png", output);
                return output;
            }
            case "img:redact":
            {
                using var src = Load(input);
                var rect = SKRectI.Create(Int(o, "x", 0), Int(o, "y", 0), Int(o, "w", 1), Int(o, "h", 1));
                using var result = Redact(src, rect, o.GetValueOrDefault("style", "Bulanık"));
                string ext = SameFormat(input);
                string output = OutputPaths.Beside(input, ext, "(sansürlü)");
                Save(result, ext, output);
                return output;
            }
            case "img:ico":
            {
                using var src = Load(input);
                string output = OutputPaths.Beside(input, "ico");
                WriteIco(src, output);
                return output;
            }
            default:
            {
                string format = id["img:".Length..];
                string ext = format == "tiff" ? "tif" : format;
                using var bmp = Load(input);
                string output = OutputPaths.Beside(input, ext);
                Save(bmp, ext, output);
                return output;
            }
        }
    }

    private static int Int(IReadOnlyDictionary<string, string> o, string key, int fallback) =>
        int.TryParse(o.GetValueOrDefault(key)?.Trim().TrimStart('+'), out int v) ? v : fallback;

    private static (int W, int H) TargetSize(int w0, int h0, IReadOnlyDictionary<string, string> o)
    {
        int w = Int(o, "width", 0), h = Int(o, "height", 0);
        if (w > 0 && h <= 0) h = (int)Math.Round(h0 * (double)w / w0);
        else if (h > 0 && w <= 0) w = (int)Math.Round(w0 * (double)h / h0);
        else if (w <= 0 && h <= 0)
        {
            double pct = Math.Clamp(Int(o, "percent", 50), 1, 1000);
            w = (int)Math.Round(w0 * pct / 100);
            h = (int)Math.Round(h0 * pct / 100);
        }
        return (Math.Clamp(w, 1, 30000), Math.Clamp(h, 1, 30000));
    }

    public static string SameFormat(string input)
    {
        string ext = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
        if (ext is "jpeg" or "jpe" or "jfif") ext = "jpg";
        if (ext == "tiff") ext = "tif";
        return ext is "png" or "jpg" or "webp" or "bmp" or "tif" or "gif" ? ext : "png";
    }

    // ---------- load / save ----------

    /// <summary>Decodes an image (first frame) with EXIF orientation applied.</summary>
    public static SKBitmap Load(string path)
    {
        using (var codec = SKCodec.Create(path))
        {
            if (codec != null)
            {
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                var bmp = new SKBitmap(info);
                var result = codec.GetPixels(info, bmp.GetPixels());
                if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
                {
                    var oriented = Orient(bmp, codec.EncodedOrigin);
                    if (!ReferenceEquals(oriented, bmp)) bmp.Dispose();
                    return oriented;
                }
                bmp.Dispose();
            }
        }
        // HEIC/TIFF/AVIF…: let the OS (sips) or ffmpeg turn it into PNG first
        string temp = Path.Combine(Path.GetTempPath(), $"gkrstudio-{Guid.NewGuid():N}.png");
        try
        {
            if (OperatingSystem.IsMacOS() && Run("/usr/bin/sips", ["-s", "format", "png", path, "--out", temp]) && File.Exists(temp))
                return SKBitmap.Decode(temp) ?? throw new InvalidOperationException("Görsel açılamadı.");
            if (Tools.Ffmpeg is { } ff && Run(ff, ["-hide_banner", "-nostdin", "-y", "-i", path, "-frames:v", "1", "-update", "1", temp]) && File.Exists(temp))
                return SKBitmap.Decode(temp) ?? throw new InvalidOperationException("Görsel açılamadı.");
            throw new InvalidOperationException("Bu görsel biçimi açılamadı: " + Path.GetExtension(path));
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    private static bool Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p == null) return false;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        return p.ExitCode == 0;
    }

    private static SKBitmap Orient(SKBitmap bmp, SKEncodedOrigin origin) => origin switch
    {
        SKEncodedOrigin.BottomRight => Rotate(bmp, 180),
        SKEncodedOrigin.RightTop => Rotate(bmp, 90),
        SKEncodedOrigin.LeftBottom => Rotate(bmp, 270),
        _ => bmp,
    };

    public static void Save(SKBitmap bmp, string format, string output, int quality = 92)
    {
        switch (format)
        {
            case "png": Encode(bmp, SKEncodedImageFormat.Png, 100, output); return;
            case "jpg": using (var flat = Flatten(bmp)) Encode(flat, SKEncodedImageFormat.Jpeg, quality, output); return;
            case "webp": Encode(bmp, SKEncodedImageFormat.Webp, 85, output); return;
        }
        // bmp / gif / tif / avif: via ffmpeg from a temporary PNG
        string ff = Tools.Ffmpeg ?? throw new InvalidOperationException($"{format.ToUpperInvariant()} için ffmpeg gerekli (Ayarlar → Araçları kur).");
        string temp = Path.Combine(Path.GetTempPath(), $"gkrstudio-{Guid.NewGuid():N}.png");
        try
        {
            Encode(format == "bmp" ? Flatten(bmp) : bmp, SKEncodedImageFormat.Png, 100, temp);
            var args = new List<string> { "-hide_banner", "-nostdin", "-n", "-i", temp };
            if (format == "avif") args.AddRange(["-c:v", "libaom-av1", "-still-picture", "1", "-crf", "30", "-b:v", "0", "-pix_fmt", "yuv420p"]);
            args.AddRange(["-frames:v", "1", "-update", "1", output]);
            if (!Run(ff, [.. args])) throw new InvalidOperationException("ffmpeg görseli kaydedemedi.");
        }
        finally { try { File.Delete(temp); } catch { } }
    }

    private static void Encode(SKBitmap bmp, SKEncodedImageFormat format, int quality, string output)
    {
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(format, quality) ?? throw new InvalidOperationException("Görsel kodlanamadı.");
        using var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        data.SaveTo(fs);
    }

    public static SKBitmap Flatten(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        canvas.Clear(SKColors.White);
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    public static SKBitmap Rotate(SKBitmap src, int degrees)
    {
        bool swap = degrees is 90 or 270;
        var dst = new SKBitmap(swap ? src.Height : src.Width, swap ? src.Width : src.Height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.Translate(dst.Width / 2f, dst.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-src.Width / 2f, -src.Height / 2f);
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    public static SKBitmap Flip(SKBitmap src)
    {
        var dst = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        canvas.Scale(-1, 1, src.Width / 2f, 0);
        canvas.DrawBitmap(src, 0, 0);
        return dst;
    }

    private static SKBitmap Adjust(SKBitmap src, int brightness, int contrast, int saturation)
    {
        float b = Math.Clamp(brightness, -100, 100) * 2.55f / 255f;
        float c = Math.Clamp(contrast, -100, 100) * 2.55f;
        float cf = 259f * (c + 255f) / (255f * (259f - c));
        float s = 1 + Math.Clamp(saturation, -100, 100) / 100f;
        float lr = 0.299f * (1 - s), lg = 0.587f * (1 - s), lb = 0.114f * (1 - s);
        float t = (b + 0.5f) * cf - 0.5f * cf + 0.5f - 0.5f * cf + 0.5f * cf; // keep mid-grey anchored
        t = cf * (b - 0.5f) + 0.5f;
        float[] m =
        [
            cf * (lr + s), cf * lg, cf * lb, 0, t,
            cf * lr, cf * (lg + s), cf * lb, 0, t,
            cf * lr, cf * lg, cf * (lb + s), 0, t,
            0, 0, 0, 1, 0,
        ];
        var dst = new SKBitmap(src.Width, src.Height, src.ColorType, src.AlphaType);
        using var canvas = new SKCanvas(dst);
        using var paint = new SKPaint { ColorFilter = SKColorFilter.CreateColorMatrix(m) };
        canvas.DrawBitmap(src, 0, 0, paint);
        return dst;
    }

    private static SKBitmap Frame(SKBitmap src, IReadOnlyDictionary<string, string> o)
    {
        int w = src.Width, h = src.Height;
        int pad = (int)(Math.Min(w, h) * Math.Clamp(Int(o, "padding", 8), 0, 50) / 100.0);
        float radius = (float)(Math.Min(w, h) * Math.Clamp(Int(o, "radius", 4), 0, 50) / 100.0);
        var dst = new SKBitmap(w + 2 * pad, h + 2 * pad, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(dst);
        var full = SKRect.Create(dst.Width, dst.Height);
        using (var bg = new SKPaint())
        {
            switch (o.GetValueOrDefault("background", "Beyaz"))
            {
                case "Siyah": bg.Color = SKColors.Black; break;
                case "Gri": bg.Color = new SKColor(0xE9, 0xEB, 0xEF); break;
                case "Mavi degrade": bg.Shader = SKShader.CreateLinearGradient(new(0, 0), new(full.Right, full.Bottom), [new SKColor(0x5B, 0x8C, 0xFF), new SKColor(0x9B, 0x5B, 0xFF)], SKShaderTileMode.Clamp); break;
                case "Gün batımı": bg.Shader = SKShader.CreateLinearGradient(new(0, 0), new(full.Right, full.Bottom), [new SKColor(0xFF, 0xC2, 0x3D), new SKColor(0xE5, 0x62, 0x1C)], SKShaderTileMode.Clamp); break;
                case "Şeffaf": bg.Color = SKColors.Transparent; break;
                default: bg.Color = SKColors.White; break;
            }
            canvas.DrawRect(full, bg);
        }
        var target = SKRect.Create(pad, pad, w, h);
        var rr = new SKRoundRect(target, radius, radius);
        if (o.GetValueOrDefault("shadow", "Var") == "Var" && pad > 0)
        {
            using var shadow = new SKPaint { Color = SKColors.Black, ImageFilter = SKImageFilter.CreateDropShadowOnly(0, pad * 0.15f, pad * 0.3f, pad * 0.3f, new SKColor(0, 0, 0, 115)) };
            canvas.DrawRoundRect(rr, shadow);
        }
        canvas.Save();
        canvas.ClipRoundRect(rr, antialias: true);
        canvas.DrawBitmap(src, target);
        canvas.Restore();
        return dst;
    }

    private static SKBitmap Redact(SKBitmap src, SKRectI r, string style)
    {
        using var path = new SKPath();
        path.AddRect(SKRect.Create(r.Left, r.Top, r.Width, r.Height));
        return Redact(src, path, style);
    }

    /// <summary>Hides the area inside <paramref name="shape"/> (any outline, e.g. a freehand lasso) in image pixels.</summary>
    public static SKBitmap Redact(SKBitmap src, SKPath shape, string style)
    {
        var dst = src.Copy();
        var b = shape.Bounds;
        var r = SKRectI.Intersect(SKRectI.Create((int)Math.Floor(b.Left), (int)Math.Floor(b.Top), (int)Math.Ceiling(b.Width) + 1, (int)Math.Ceiling(b.Height) + 1), SKRectI.Create(src.Width, src.Height));
        if (r.Width < 1 || r.Height < 1) return dst;
        using var canvas = new SKCanvas(dst);
        var area = SKRect.Create(r.Left, r.Top, r.Width, r.Height);
        canvas.Save();
        canvas.ClipPath(shape, antialias: true);
        if (style == "Siyah")
        {
            using var black = new SKPaint { Color = SKColors.Black };
            canvas.DrawRect(area, black);
        }
        else if (style == "Piksel")
        {
            int factor = Math.Max(8, Math.Min(r.Width, r.Height) / 12);
            using var subset = new SKBitmap();
            src.ExtractSubset(subset, r);
            using var small = subset.Resize(new SKImageInfo(Math.Max(1, r.Width / factor), Math.Max(1, r.Height / factor)), SKFilterQuality.None);
            using var paint = new SKPaint { FilterQuality = SKFilterQuality.None };
            canvas.DrawBitmap(small, area, paint);
        }
        else
        {
            float sigma = Math.Max(6, Math.Min(r.Width, r.Height) / 8f);
            using var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(sigma, sigma) };
            canvas.DrawBitmap(src, 0, 0, paint);
        }
        canvas.Restore();
        return dst;
    }

    private static void WriteIco(SKBitmap src, string output)
    {
        const int size = 256;
        using var square = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(square))
        {
            canvas.Clear(SKColors.Transparent);
            float scale = Math.Min((float)size / src.Width, (float)size / src.Height);
            float w = src.Width * scale, h = src.Height * scale;
            canvas.DrawBitmap(src, SKRect.Create((size - w) / 2, (size - h) / 2, w, h), new SKPaint { FilterQuality = SKFilterQuality.High });
        }
        using var image = SKImage.FromBitmap(square);
        byte[] png = image.Encode(SKEncodedImageFormat.Png, 100).ToArray();
        using var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
        using var bw = new BinaryWriter(fs);
        bw.Write((ushort)0); bw.Write((ushort)1); bw.Write((ushort)1);
        bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0);
        bw.Write((ushort)1); bw.Write((ushort)32);
        bw.Write(png.Length); bw.Write(6 + 16);
        bw.Write(png);
    }

    // ---------- metadata removal (lossless for JPEG/PNG, same logic as Windows) ----------

    private static string StripMetadata(string input)
    {
        string ext = SameFormat(input);
        string output = OutputPaths.Beside(input, ext, "(metadata yok)");
        byte[] data = File.ReadAllBytes(input);
        using (var codec = SKCodec.Create(input))
        {
            bool upright = codec == null || codec.EncodedOrigin == SKEncodedOrigin.TopLeft;
            if (ext == "jpg" && upright && data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8) { File.WriteAllBytes(output, StripJpeg(data)); return output; }
        }
        if (ext == "png" && data.Length > 8 && data[0] == 0x89) { File.WriteAllBytes(output, StripPng(data)); return output; }
        using var bmp = Load(input);
        Save(bmp, ext, output, 95);
        return output;
    }

    private static byte[] StripJpeg(byte[] d)
    {
        using var ms = new MemoryStream(d.Length);
        ms.Write(d, 0, 2);
        int i = 2;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) throw new InvalidDataException("Bozuk JPEG.");
            byte marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker == 0xDA) { ms.Write(d, i, d.Length - i); return ms.ToArray(); }
            int len = (d[i + 2] << 8) | d[i + 3];
            if (len < 2 || i + 2 + len > d.Length) throw new InvalidDataException("Bozuk JPEG segmenti.");
            bool drop = (marker >= 0xE1 && marker <= 0xEF && marker != 0xE2 && marker != 0xEE) || marker == 0xFE;
            if (!drop) ms.Write(d, i, 2 + len);
            i += 2 + len;
        }
        throw new InvalidDataException("JPEG görüntü verisi bulunamadı.");
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
            if (len < 0 || i + 12 + len > d.Length) throw new InvalidDataException("Bozuk PNG.");
            string type = System.Text.Encoding.ASCII.GetString(d, i + 4, 4);
            if (!dropped.Contains(type)) ms.Write(d, i, 12 + len);
            i += 12 + len;
            if (type == "IEND") break;
        }
        return ms.ToArray();
    }
}

/// <summary>On macOS there is no STA requirement; work simply runs on the thread pool.</summary>
public static class Sta
{
    public static Task<T> Run<T>(Func<T> work) => Task.Run(work);
}
