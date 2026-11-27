using System.Globalization;
using System.IO;

namespace GKRStudio.Core;

/// <summary>All video/audio tools. Every export goes through ffmpeg; outputs never overwrite existing files.</summary>
public static class MediaTools
{
    private static readonly string[] H264 = ["-c:v", "libx264", "-preset", "medium", "-crf", "20", "-pix_fmt", "yuv420p"];
    private static readonly string[] Aac = ["-c:a", "aac", "-b:a", "192k"];
    private static readonly string[] FastStart = ["-movflags", "+faststart"];

    public static async Task<string> RunAsync(string id, string input, IReadOnlyDictionary<string, string> o,
        Action<double> frac, Action<string> log, CancellationToken ct)
    {
        bool isVideo = Catalog.KindOf(input) == InputKind.Video;
        string ext = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
        var args = new List<string>();
        string output;
        double? duration = null;

        switch (id)
        {
            // ---- simple conversions ----
            case "av:mp4" or "av:mp3" or "av:m4a" or "av:wav" or "av:flac" or "av:ogg" or "av:gif"
                or "av:mov" or "av:mkv" or "av:webm" or "av:avi" or "av:wmv" or "av:opus" or "av:aiff" or "av:wma":
            {
                var (oext, _) = OutputFor(id);
                output = OutputPaths.Beside(input, oext);
                args.AddRange(ConvertArgs(id, input, output));
                break;
            }

            case "av:compress":
                output = OutputPaths.Beside(input, isVideo ? "mp4" : "m4a", "(küçük)");
                args.AddRange(isVideo
                    ? ["-i", input, "-map", "0:v:0", "-map", "0:a?", "-c:v", "libx264", "-preset", "slow", "-crf", "28",
                       "-vf", "scale='min(1920,iw)':-2", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", .. FastStart, output]
                    : ["-i", input, "-vn", "-c:a", "aac", "-b:a", "96k", output]);
                break;

            case "av:mute":
                output = OutputPaths.Beside(input, ext, "(sessiz)");
                args.AddRange(["-i", input, "-map", "0", "-map", "-0:a", "-c", "copy", output]);
                break;

            case "av:strip":
                output = OutputPaths.Beside(input, ext, "(metadata yok)");
                args.AddRange(["-i", input, "-map", "0", "-map_metadata", "-1", "-map_chapters", "-1", "-c", "copy",
                               "-fflags", "+bitexact", "-flags:v", "+bitexact", "-flags:a", "+bitexact", output]);
                break;

            case "av:loudnorm":
                output = OutputPaths.Beside(input, ext, "(eşitlendi)");
                args.AddRange(["-i", input]);
                if (isVideo) args.AddRange(["-map", "0:v?"]);
                args.AddRange(["-map", "0:a", "-af", "loudnorm=I=-16:TP=-1.5:LRA=11", "-ar", "48000"]);
                if (isVideo) args.AddRange(["-c:v", "copy", .. ext == "webm" ? new[] { "-c:a", "libopus", "-b:a", "160k" } : Aac]);
                args.Add(output);
                break;

            // ---- time based ----
            case "av:trim":
            {
                double start = Time.Parse(o.GetValueOrDefault("start", "0"));
                double end = Time.Parse(o.GetValueOrDefault("end"));
                if (end <= start) throw new InvalidOperationException("Bitiş, başlangıçtan sonra olmalı.");
                duration = end - start;
                output = OutputPaths.Beside(input, isVideo ? "mp4" : ext, "(kesit)");
                args.AddRange(["-ss", Time.Arg(start), "-i", input, "-t", Time.Arg(end - start)]);
                args.AddRange(isVideo ? ["-map", "0:v:0", "-map", "0:a?", .. H264, .. Aac, .. FastStart] : ["-vn"]);
                args.Add(output);
                break;
            }

            case "av:speed":
            {
                double f = double.Parse(o.GetValueOrDefault("factor", "2").TrimEnd('x', '×'), NumberStyles.Float, CultureInfo.InvariantCulture);
                if (f is < 0.25 or > 4) throw new InvalidOperationException("Hız 0.25x ile 4x arasında olmalı.");
                var info = await Ffmpeg.ProbeAsync(input, ct);
                duration = info.Duration / f;
                output = OutputPaths.Beside(input, isVideo ? "mp4" : ext, $"({f.ToString(CultureInfo.InvariantCulture)}x)");
                args.AddRange(["-i", input]);
                if (isVideo) args.AddRange(["-map", "0:v:0", "-filter:v", $"setpts=PTS/{f.ToString(CultureInfo.InvariantCulture)}", .. H264]);
                if (info.HasAudio) args.AddRange(["-map", "0:a:0", "-filter:a", Atempo(f), .. (isVideo ? Aac : Array.Empty<string>())]);
                if (isVideo) args.AddRange(FastStart);
                args.Add(output);
                break;
            }

            case "av:frame":
            {
                double t = Time.Parse(o.GetValueOrDefault("time", "0"));
                output = OutputPaths.Beside(input, "png", $"(kare {Time.Pretty(t).Replace(':', '-')})");
                args.AddRange(["-ss", Time.Arg(t), "-i", input, "-frames:v", "1", "-update", "1", output]);
                break;
            }

            case "av:split":
            {
                double seconds = Time.Parse(o.GetValueOrDefault("seconds", "60"));
                if (seconds < 1) throw new InvalidOperationException("Parça uzunluğu en az 1 saniye olmalı.");
                string dir = OutputPaths.Unique(Path.GetDirectoryName(input)!, Path.GetFileNameWithoutExtension(input), "", "(parçalar)");
                Directory.CreateDirectory(dir);
                args.AddRange(["-i", input]);
                // Video: re-encode with forced keyframes so every cut lands exactly on the requested length.
                if (isVideo)
                    args.AddRange(["-map", "0:v:0", "-map", "0:a?", .. H264, "-force_key_frames", $"expr:gte(t,n_forced*{Time.Arg(seconds)})", .. Aac]);
                else
                    args.AddRange(["-map", "0:a", "-c", "copy"]);
                args.AddRange(["-f", "segment", "-segment_time", Time.Arg(seconds), "-reset_timestamps", "1",
                               Path.Combine(dir, L.T("parça") + " %03d." + (isVideo ? "mp4" : ext))]);
                await Ffmpeg.RunAsync(args, frac, log, ct);
                return dir;
            }

            // ---- region based ----
            case "av:crop" or "av:redact":
            {
                var (x, y, w, h) = Region(o);
                if (id == "av:crop")
                {
                    output = OutputPaths.Beside(input, "mp4", "(kırpıldı)");
                    args.AddRange(["-i", input, "-map", "0:v:0", "-map", "0:a?", "-vf", $"crop={w}:{h}:{x}:{y}", .. H264, .. Aac, .. FastStart, output]);
                }
                else
                {
                    int r = Math.Clamp(Math.Min(w, h) / 5, 2, 40);
                    output = OutputPaths.Beside(input, "mp4", "(redakte)");
                    args.AddRange(["-i", input, "-filter_complex",
                        $"[0:v]crop={w}:{h}:{x}:{y},boxblur=luma_radius={r}:luma_power=3:chroma_radius={Math.Max(1, r / 2)}:chroma_power=3[b];[0:v][b]overlay={x}:{y}[v]",
                        "-map", "[v]", "-map", "0:a?", .. H264, .. Aac, .. FastStart, output]);
                }
                break;
            }

            // ---- audio ----
            case "av:channels":
            {
                string mode = o.GetValueOrDefault("mode", "Mono");
                output = OutputPaths.Beside(input, ext, "(" + mode.ToLowerInvariant() + ")");
                args.AddRange(["-i", input, "-vn"]);
                args.AddRange(mode switch
                {
                    "Mono" => ["-ac", "1"],
                    "Stereo" => ["-ac", "2"],
                    "Sol/sağ değiştir" => ["-af", "pan=stereo|c0=c1|c1=c0"],
                    "Sadece sol" => ["-af", "pan=mono|c0=c0"],
                    "Sadece sağ" => ["-af", "pan=mono|c0=c1"],
                    _ => throw new InvalidOperationException("Bilinmeyen kanal modu."),
                });
                args.Add(output);
                break;
            }

            case "av:bleep":
            {
                var ranges = ParseRanges(o.GetValueOrDefault("ranges"));
                string on = string.Join("+", ranges.Select(r => $"between(t,{Time.Arg(r.Start)},{Time.Arg(r.End)})"));
                output = OutputPaths.Beside(input, ext, "(bip)");
                var info = await Ffmpeg.ProbeAsync(input, ct);
                args.AddRange(["-i", input, "-filter_complex",
                    $"[0:a]aformat=sample_rates=48000:channel_layouts=stereo,volume=0:enable='{on}'[m];" +
                    $"sine=frequency=1000:sample_rate=48000,aformat=channel_layouts=stereo,volume=0.35,volume=0:enable='not({on})'[s];" +
                    "[m][s]amix=inputs=2:duration=first:normalize=0[a]",
                    "-map", "[a]"]);
                if (isVideo && info.HasVideo) args.AddRange(["-map", "0:v:0", "-c:v", "copy", .. Aac]);
                args.Add(output);
                break;
            }

            case "av:visualize":
                output = OutputPaths.Beside(input, "mp4", "(dalga)");
                args.AddRange(["-i", input, "-filter_complex",
                    "[0:a]showwaves=s=1280x720:mode=cline:rate=30:colors=#F5A623|#5B8CFF,format=yuv420p[v]",
                    "-map", "[v]", "-map", "0:a", "-c:v", "libx264", "-preset", "medium", "-crf", "22", .. Aac, "-shortest", .. FastStart, output]);
                break;

            default:
                throw new ArgumentException("Bilinmeyen medya işlemi: " + id);
        }

        try
        {
            await Ffmpeg.RunAsync(args, frac, log, ct, duration);
        }
        catch
        {
            try { if (File.Exists(output)) File.Delete(output); } catch { }
            throw;
        }
        return output;
    }

    /// <summary>Concatenates videos (sorted by name) into one MP4, scaling/padding everything to the first video's size.</summary>
    public static async Task<string> MergeAsync(IReadOnlyList<string> inputs, Action<double> frac, Action<string> log, CancellationToken ct)
    {
        var files = inputs.OrderBy(p => Path.GetFileName(p), StringComparer.CurrentCultureIgnoreCase).ToList();
        var infos = new List<MediaInfo>();
        foreach (var f in files) infos.Add(await Ffmpeg.ProbeAsync(f, ct));
        int w = infos[0].Width / 2 * 2, h = infos[0].Height / 2 * 2;
        if (w <= 0 || h <= 0) throw new InvalidOperationException("İlk videonun çözünürlüğü okunamadı.");
        bool audio = infos.All(i => i.HasAudio);
        log($"Sıra: {string.Join(" → ", files.Select(Path.GetFileName))}");
        if (!audio) log("Not: bazı videolarda ses yok, çıktı sessiz olacak.");

        var args = new List<string>();
        foreach (var f in files) args.AddRange(["-i", f]);
        var graph = new System.Text.StringBuilder();
        for (int i = 0; i < files.Count; i++)
        {
            graph.Append($"[{i}:v:0]scale={w}:{h}:force_original_aspect_ratio=decrease,pad={w}:{h}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=30,format=yuv420p[v{i}];");
            if (audio) graph.Append($"[{i}:a:0]aresample=48000,aformat=channel_layouts=stereo[a{i}];");
        }
        for (int i = 0; i < files.Count; i++) graph.Append(audio ? $"[v{i}][a{i}]" : $"[v{i}]");
        graph.Append($"concat=n={files.Count}:v=1:a={(audio ? 1 : 0)}[v]{(audio ? "[a]" : "")}");

        string output = OutputPaths.Beside(files[0], "mp4", "(birleştirildi)");
        args.AddRange(["-filter_complex", graph.ToString(), "-map", "[v]"]);
        if (audio) args.AddRange(["-map", "[a]", .. Aac]);
        args.AddRange([.. H264, .. FastStart, output]);
        await Ffmpeg.RunAsync(args, frac, log, ct, infos.Sum(i => i.Duration));
        return output;
    }

    private static IEnumerable<string> ConvertArgs(string id, string input, string output) => id switch
    {
        "av:mp4" => ["-i", input, "-map", "0:v:0", "-map", "0:a?", .. H264.Select(a => a == "20" ? "23" : a), "-c:a", "aac", "-b:a", "160k", .. FastStart, output],
        "av:gif" => ["-i", input, "-vf", "fps=12,scale='min(480,iw)':-1:flags=lanczos,split[a][b];[a]palettegen=stats_mode=diff[p];[b][p]paletteuse=dither=bayer", "-loop", "0", output],
        "av:mp3" => ["-i", input, "-vn", "-c:a", "libmp3lame", "-q:a", "2", output],
        "av:m4a" => ["-i", input, "-vn", "-c:a", "aac", "-b:a", "192k", output],
        "av:wav" => ["-i", input, "-vn", "-c:a", "pcm_s16le", output],
        "av:flac" => ["-i", input, "-vn", "-c:a", "flac", output],
        "av:ogg" => ["-i", input, "-vn", "-c:a", "libvorbis", "-q:a", "5", output],
        "av:opus" => ["-i", input, "-vn", "-c:a", "libopus", "-b:a", "128k", output],
        "av:aiff" => ["-i", input, "-vn", "-c:a", "pcm_s16be", output],
        "av:wma" => ["-i", input, "-vn", "-c:a", "wmav2", "-b:a", "192k", output],
        "av:mov" => ["-i", input, "-map", "0:v:0", "-map", "0:a?", .. H264, "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart", output],
        "av:mkv" => ["-i", input, "-map", "0:v:0", "-map", "0:a?", .. H264, "-c:a", "aac", "-b:a", "192k", output],
        "av:webm" => ["-i", input, "-map", "0:v:0", "-map", "0:a?", "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-row-mt", "1", "-c:a", "libopus", "-b:a", "128k", output],
        "av:avi" => ["-i", input, "-map", "0:v:0", "-map", "0:a?", "-c:v", "mpeg4", "-q:v", "4", "-c:a", "libmp3lame", "-q:a", "3", output],
        "av:wmv" => ["-i", input, "-map", "0:v:0", "-map", "0:a?", "-c:v", "wmv2", "-q:v", "4", "-c:a", "wmav2", "-b:a", "192k", output],
        _ => throw new ArgumentException(id),
    };

    private static (string Ext, string? Suffix) OutputFor(string id) => id switch
    {
        "av:mp4" => ("mp4", null), "av:gif" => ("gif", null), "av:mp3" => ("mp3", null), "av:m4a" => ("m4a", null),
        "av:wav" => ("wav", null), "av:flac" => ("flac", null), "av:ogg" => ("ogg", null),
        "av:opus" => ("opus", null), "av:aiff" => ("aiff", null), "av:wma" => ("wma", null),
        "av:mov" => ("mov", null), "av:mkv" => ("mkv", null), "av:webm" => ("webm", null), "av:avi" => ("avi", null), "av:wmv" => ("wmv", null),
        _ => throw new ArgumentException(id),
    };

    /// <summary>atempo accepts 0.5..100 per stage; chain stages for slower speeds.</summary>
    private static string Atempo(double f)
    {
        var parts = new List<string>();
        while (f < 0.5) { parts.Add("atempo=0.5"); f /= 0.5; }
        parts.Add("atempo=" + f.ToString("0.####", CultureInfo.InvariantCulture));
        return string.Join(",", parts);
    }

    private static (int X, int Y, int W, int H) Region(IReadOnlyDictionary<string, string> o)
    {
        int Get(string k) => int.TryParse(o.GetValueOrDefault(k), out int v) ? v : throw new InvalidOperationException("Alan seçilmedi.");
        int w = Get("w") / 2 * 2, h = Get("h") / 2 * 2;
        if (w < 16 || h < 16) throw new InvalidOperationException("Seçilen alan çok küçük.");
        return (Math.Max(0, Get("x")), Math.Max(0, Get("y")), w, h);
    }

    /// <summary>"1-2.5, 1:10-1:12" → time ranges.</summary>
    public static List<(double Start, double End)> ParseRanges(string? text)
    {
        var list = new List<(double, double)>();
        foreach (var part in (text ?? "").Split([';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                                         .SelectMany(p => p.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
        {
            var se = part.Split('-', 2);
            if (se.Length != 2) throw new FormatException($"Geçersiz aralık: \"{part}\" (örnek: 1.5-2.3)");
            double s = Time.Parse(se[0]), e = Time.Parse(se[1]);
            if (e <= s) throw new FormatException($"Aralık sonu başından büyük olmalı: \"{part}\"");
            list.Add((s, e));
        }
        if (list.Count == 0) throw new FormatException("En az bir aralık girin (örnek: 1.5-2.3).");
        return list;
    }
}
