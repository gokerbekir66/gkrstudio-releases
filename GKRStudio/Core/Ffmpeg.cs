using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GKRStudio.Core;

public sealed record MediaInfo(double Duration, int Width, int Height, bool HasVideo, bool HasAudio);

public static partial class Ffmpeg
{
    [GeneratedRegex(@"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)")]
    private static partial Regex DurationRegex();

    public static string Require() =>
        Tools.Ffmpeg ?? throw new InvalidOperationException("ffmpeg bulunamadı. Ayarlar penceresindeki \"Eksik araçları kur\" düğmesini kullanın.");

    public static string RequireProbe()
    {
        string? probe = Tools.Ffprobe ?? (Tools.Ffmpeg is { } f ? Path.Combine(Path.GetDirectoryName(f)!, "ffprobe.exe") : null);
        return probe != null && File.Exists(probe) ? probe : throw new InvalidOperationException("ffprobe bulunamadı.");
    }

    /// <summary>Runs ffmpeg with machine-readable progress; reports 0..1. <paramref name="duration"/> overrides the input length for progress.</summary>
    public static async Task RunAsync(IEnumerable<string> args, Action<double> fraction, Action<string> log, CancellationToken ct, double? duration = null)
    {
        string exe = Require();
        var full = new List<string> { "-hide_banner", "-nostdin", "-y", "-progress", "pipe:1", "-nostats" };
        full.AddRange(args);

        double total = duration ?? 0;
        var tail = new Queue<string>();
        int code = await ProcessRunner.RunAsync(exe, full, line =>
        {
            var m = DurationRegex().Match(line);
            if (m.Success && total <= 0)
            {
                total = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 +
                        double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            }
            if (line.StartsWith("out_time_us=", StringComparison.Ordinal) || line.StartsWith("out_time_ms=", StringComparison.Ordinal))
            {
                if (total > 0 && long.TryParse(line[(line.IndexOf('=') + 1)..], out long us) && us >= 0)
                    fraction(Math.Clamp(us / 1_000_000.0 / total, 0, 1));
                return;
            }
            if (line.Contains('=') && !line.Contains(' ')) return; // other -progress key=value lines
            log(line);
            tail.Enqueue(line);
            if (tail.Count > 8) tail.Dequeue();
        }, ct).ConfigureAwait(false);

        if (code != 0)
            throw new InvalidOperationException($"ffmpeg hata kodu {code}:\n" + string.Join("\n", tail));
        fraction(1);
    }

    public static async Task<MediaInfo> ProbeAsync(string input, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        int code = await ProcessRunner.RunAsync(RequireProbe(),
            ["-v", "error", "-show_entries", "format=duration:stream=codec_type,width,height:stream_disposition=attached_pic", "-of", "json", input],
            line => sb.AppendLine(line), ct).ConfigureAwait(false);
        if (code != 0) throw new InvalidOperationException("Dosya okunamadı (ffprobe): " + sb.ToString().Trim());

        using var doc = JsonDocument.Parse(sb.ToString());
        double duration = 0;
        if (doc.RootElement.TryGetProperty("format", out var format) && format.TryGetProperty("duration", out var d))
            double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out duration);

        int w = 0, h = 0;
        bool video = false, audio = false;
        if (doc.RootElement.TryGetProperty("streams", out var streams))
        {
            foreach (var s in streams.EnumerateArray())
            {
                string type = s.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "";
                bool cover = s.TryGetProperty("disposition", out var disp) && disp.TryGetProperty("attached_pic", out var ap) && ap.GetInt32() == 1;
                if (type == "video" && !cover && !video)
                {
                    video = true;
                    w = s.TryGetProperty("width", out var ww) ? ww.GetInt32() : 0;
                    h = s.TryGetProperty("height", out var hh) ? hh.GetInt32() : 0;
                }
                if (type == "audio") audio = true;
            }
        }
        return new MediaInfo(duration, w, h, video, audio);
    }
}

public static class Time
{
    /// <summary>Parses "90", "1:30", "01:02:03.5", "1,5".</summary>
    public static double Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new FormatException("Zaman boş.");
        double total = 0;
        foreach (var part in text.Trim().Replace(',', '.').Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || v < 0)
                throw new FormatException($"Geçersiz zaman: \"{text}\" (örnek: 1:23.5)");
            total = total * 60 + v;
        }
        return total;
    }

    public static string Arg(double seconds) => seconds.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Pretty(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        string frac = (t.Milliseconds / 100).ToString(CultureInfo.InvariantCulture);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}.{frac}" : $"{t.Minutes}:{t.Seconds:00}.{frac}";
    }
}
