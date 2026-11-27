using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace GKRStudio.Core;

public static partial class Downloader
{
    [GeneratedRegex(@"\[download\]\s+(\d+(?:\.\d+)?)%")]
    private static partial Regex PercentRegex();

    private const string FilePrefix = "MTA_FILE|";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All, MaxAutomaticRedirections = 10 })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) GKRStudio/1.0");
        return client;
    }

    /// <summary>yt-dlp based video/audio/thumbnail download. Returns the file (or folder) to reveal.</summary>
    public static async Task<string> RunYtDlpAsync(string id, string url, string outputDir, Action<double?, string> progress, Action<string> log, CancellationToken ct,
                                                   IReadOnlyDictionary<string, string>? options = null)
    {
        string quality = options?.GetValueOrDefault("quality") ?? "";
        string ytdlp = Tools.YtDlp ?? throw new InvalidOperationException("yt-dlp bulunamadı. Ana penceredeki \"Eksik araçları kur\" düğmesini kullanın.");
        string? ffmpeg = Tools.Ffmpeg;
        string? deno = Tools.Deno;

        var args = new List<string>
        {
            "--ignore-config", "--no-playlist", "--no-overwrites", "--no-mtime", "--newline", "--progress",
            "--encoding", "utf-8",
            "--print", "after_move:" + FilePrefix + "%(filepath)s",
            "-P", outputDir,
            "-o", "%(title).150B [%(id)s].%(ext)s",
        };
        if (OperatingSystem.IsWindows())
            // Trust the Windows certificate store instead of yt-dlp's bundled list. Certificates are still verified,
            // but HTTPS-scanning antivirus (Avast, Kaspersky, ESET…) and corporate proxies no longer break downloads.
            // Windows only: on macOS a standalone yt-dlp has no usable system CA store without certifi.
            args.AddRange(["--compat-options", "no-certifi", "--windows-filenames"]);
        if (ffmpeg != null) args.AddRange(["--ffmpeg-location", Path.GetDirectoryName(ffmpeg)!]);
        // Sites behind a cookie consent or a login (e.g. news sites, private posts): reuse the browser's cookies.
        string browser = Preferences.Load().CookieBrowser;
        if (!string.IsNullOrWhiteSpace(browser))
        {
            args.AddRange(["--cookies-from-browser", browser.Trim().ToLowerInvariant()]);
            log("Tarayıcı çerezleri: " + browser);
        }
        if (deno != null) args.AddRange(["--js-runtimes", "deno:" + deno]);

        switch (id)
        {
            case "dl:audio":
                if (ffmpeg == null) throw new InvalidOperationException("MP3 için ffmpeg gerekli.");
            {
                // "320 kbps" → 320K (constant bit rate); default 320K
                string kbps = new string(quality.TakeWhile(char.IsDigit).ToArray());
                if (kbps.Length == 0) kbps = "320";
                args.AddRange(["-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", kbps + "K"]);
                log($"MP3 kalitesi: {kbps} kbps");
                break;
            }
            case "dl:thumb":
                args.AddRange(["--skip-download", "--write-thumbnail", "--convert-thumbnails", "jpg", "-o", "thumbnail:%(title).150B [%(id)s].%(ext)s"]);
                break;
            default:
            {
                // Prefer H.264 + AAC: plays everywhere (Windows Media Player, phones, TVs, editors).
                // The "best" YouTube stream is often AV1/VP9, which many players can't decode (audio only).
                bool highest = quality.StartsWith("En yüksek", StringComparison.Ordinal);
                int height = int.TryParse(new string(quality.TakeWhile(char.IsDigit).ToArray()), out int h) ? h : 1080;
                string sort = highest ? "res,vcodec:h264,acodec:m4a" : "vcodec:h264,res,acodec:m4a";
                string format = highest
                    ? (ffmpeg != null ? "bv*+ba/b" : "b")
                    : (ffmpeg != null ? $"bv*[height<={height}]+ba/b[height<={height}]/bv*+ba/b" : $"b[height<={height}][ext=mp4]/b[height<={height}]/b");
                args.AddRange(["-f", format, "-S", sort]);
                if (ffmpeg != null) args.AddRange(["--merge-output-format", "mp4"]);
                log("Video kalitesi: " + (highest ? "en yüksek" : $"en fazla {height}p (H.264)"));
                break;
            }
        }
        args.AddRange(["--", url]);

        progress(null, "yt-dlp denetleniyor…");
        await SelfUpdateAsync(ytdlp, log, ct).ConfigureAwait(false);

        string? produced = null;
        var tail = new Queue<string>();
        progress(null, "Bağlantı çözümleniyor…");
        int code = await ProcessRunner.RunAsync(ytdlp, args, line =>
        {
            if (line.StartsWith(FilePrefix, StringComparison.Ordinal)) { produced = line[FilePrefix.Length..].Trim(); return; }
            var m = PercentRegex().Match(line);
            if (m.Success)
            {
                progress(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) / 100.0, "İndiriliyor…");
                return;
            }
            if (line.Contains("[Merger]") || line.Contains("[ExtractAudio]") || line.Contains("[VideoConvertor]"))
                progress(null, "Dönüştürülüyor…");
            log(line);
            tail.Enqueue(line);
            if (tail.Count > 8) tail.Dequeue();
        }, ct).ConfigureAwait(false);

        if (code != 0) throw new InvalidOperationException(Explain(code, tail.ToList()));
        return produced != null && File.Exists(produced) ? produced : outputDir;
    }

    /// <summary>
    /// YouTube changes often and an old yt-dlp stops working. The standalone yt-dlp (bundled on Windows, downloaded on
    /// macOS) updates itself with -U; at most once every 2 days. Package-managed copies (Homebrew, winget) are skipped.
    /// </summary>
    private static async Task SelfUpdateAsync(string ytdlp, Action<string> log, CancellationToken ct)
    {
        if (ytdlp.StartsWith("/opt/homebrew/", StringComparison.Ordinal) || ytdlp.StartsWith("/usr/local/", StringComparison.Ordinal) ||
            ytdlp.StartsWith("/usr/bin/", StringComparison.Ordinal) || ytdlp.Contains("WinGet", StringComparison.OrdinalIgnoreCase)) return;
        string stamp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GKRStudio", "yt-dlp-update.stamp");
        try
        {
            if (File.Exists(stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(stamp) < TimeSpan.FromDays(2)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(stamp)!);
            File.WriteAllText(stamp, DateTime.UtcNow.ToString("o"));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(120));
            int code = await ProcessRunner.RunAsync(ytdlp, OperatingSystem.IsWindows() ? ["--compat-options", "no-certifi", "-U"] : ["-U"], log, cts.Token).ConfigureAwait(false);
            if (code != 0) log($"yt-dlp güncellenemedi (kod {code}); mevcut sürümle devam ediliyor.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { log("yt-dlp güncellemesi zaman aşımına uğradı; devam ediliyor."); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { log("yt-dlp güncellemesi atlandı: " + ex.Message); }
    }

    /// <summary>Turns yt-dlp's wall of warnings into one understandable message (details stay in the log).</summary>
    private static string Explain(int code, List<string> tail)
    {
        string all = string.Join("\n", tail);
        string? error = tail.LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
        string hint =
            all.Contains("Could not copy", StringComparison.OrdinalIgnoreCase) && all.Contains("cookie", StringComparison.OrdinalIgnoreCase) ? "Tarayıcı açıkken çerezleri kilitli; tarayıcıyı tamamen kapatıp tekrar deneyin (ya da Ayarlar'dan başka bir tarayıcı seçin)." :
            all.Contains("Failed to decrypt", StringComparison.OrdinalIgnoreCase) || all.Contains("app-bound", StringComparison.OrdinalIgnoreCase) ? "Bu tarayıcı çerezlerini şifreliyor ve okunamıyor. Ayarlar'da Firefox'u seçin ya da çerez seçeneğini kapatın." :
            all.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(Preferences.Load().CookieBrowser) ? "Bu sayfada video bulunamadı. Site önce çerez onayı istiyorsa: tarayıcınızda sayfayı açıp onaylayın, sonra Ayarlar → Linkten indir → \"Tarayıcı çerezleri\" ile o tarayıcıyı seçin." :
            all.Contains("CERTIFICATE_VERIFY_FAILED") ? "Güvenli bağlantı doğrulanamadı. Antivirüsün HTTPS taraması ya da bir proxy bağlantıyı değiştiriyor olabilir; Windows saatinin doğru olduğundan emin olun." :
            all.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) || all.Contains("login required", StringComparison.OrdinalIgnoreCase) ? "Bu içerik giriş yapmayı gerektiriyor; yalnızca herkese açık medya indirilebilir." :
            all.Contains("Private video", StringComparison.OrdinalIgnoreCase) ? "Video özel; indirilemez." :
            all.Contains("Unsupported URL", StringComparison.OrdinalIgnoreCase) ? "Bu site ya da sayfa desteklenmiyor. Doğrudan video sayfasının linkini kopyalayın." :
            all.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase) ? "İstenen biçim bu videoda yok." :
            all.Contains("HTTP Error 429") ? "Site çok fazla istek nedeniyle geçici olarak engelledi; biraz sonra tekrar deneyin." :
            all.Contains("getaddrinfo failed") || all.Contains("Failed to resolve") ? "İnternet bağlantısı yok ya da site adı çözülemedi." :
            "İndirme başarısız oldu. yt-dlp'yi güncellemek çoğu zaman çözer (Ayarlar → Eksik araçları kur / winget upgrade yt-dlp.yt-dlp).";
        string detail = error != null ? "\n\n" + (error.Length > 300 ? error[..300] + "…" : error) : "";
        return $"{hint}{detail}\n(yt-dlp kod {code}; tüm çıktı Ayrıntılar'da)";
    }

    /// <summary>Plain HTTP download for direct image links.</summary>
    public static async Task<string> DownloadImageAsync(string url, string outputDir, Action<double?, string> progress, CancellationToken ct)
    {
        progress(null, "Resim indiriliyor…");
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType != null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Bağlantı bir resim döndürmedi ({mediaType}).");

        var uri = new Uri(url);
        string name = Uri.UnescapeDataString(Path.GetFileNameWithoutExtension(uri.AbsolutePath));
        if (string.IsNullOrWhiteSpace(name)) name = uri.Host;
        string ext = mediaType?.ToLowerInvariant() switch
        {
            "image/jpeg" => "jpg", "image/png" => "png", "image/webp" => "webp", "image/gif" => "gif",
            "image/avif" => "avif", "image/bmp" => "bmp", "image/svg+xml" => "svg", "image/heic" => "heic", "image/tiff" => "tif",
            _ => Path.GetExtension(uri.AbsolutePath).TrimStart('.') is { Length: > 0 } e ? e : "jpg",
        };

        string output = OutputPaths.Unique(outputDir, name, ext);
        long? total = response.Content.Headers.ContentLength;
        string temp = output + ".part";
        try
        {
            await using (var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var dst = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    progress(total > 0 ? (double)done / total.Value : null, "Resim indiriliyor…");
                }
            }
            File.Move(temp, output);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return output;
    }
}
