using System.IO;

namespace GKRStudio.Core;

public sealed record ProgressInfo(double? Fraction, string Status);

/// <summary>A self-contained job: everything it needs is captured at creation, nothing is read from shared state.</summary>
public sealed record Job(MenuAction Action, IReadOnlyList<string> Inputs, LinkInfo? Link, string OutputDir,
                         IReadOnlyDictionary<string, string>? Options = null)
{
    public IReadOnlyDictionary<string, string> Opts => Options ?? new Dictionary<string, string>();
}

public static class ActionRunner
{
    /// <summary>Runs the job; returns the path to reveal in Explorer (file or folder).</summary>
    public static async Task<string?> RunAsync(Job job, IProgress<ProgressInfo> progress, Action<string> log, CancellationToken ct)
    {
        string id = job.Action.Id;
        void Frac(double f) => progress.Report(new ProgressInfo(f, job.Action.Title + "…"));

        // ---- link downloads ----
        if (id.StartsWith("dl:", StringComparison.Ordinal))
        {
            var link = job.Link ?? throw new InvalidOperationException("Link yok.");
            Directory.CreateDirectory(job.OutputDir);
            log($"Link: {link.Url}");
            log($"Hedef klasör: {job.OutputDir}");
            void Report(double? f, string s) => progress.Report(new ProgressInfo(f, s));
            string result = id == "dl:image"
                ? await Downloader.DownloadImageAsync(link.Url, job.OutputDir, Report, ct)
                : await Downloader.RunYtDlpAsync(id, link.Url, job.OutputDir, Report, log, ct, job.Opts);
            log("Kaydedildi: " + result);
            return result;
        }

        // ---- tools that take the whole selection at once ----
        switch (id)
        {
            case "av:merge": return await MediaTools.MergeAsync(job.Inputs, Frac, log, ct);
            case "img:pdf": return await Sta.Run(() => PdfTools.ImagesToPdf(job.Inputs));
            case "pdf:merge": return await Task.Run(() => PdfTools.Merge(job.Inputs, log), ct);
            case "zip:create": return await Task.Run(() => ZipTools.Create(job.Inputs, Frac, log, ct), ct);
            case "slide:pdf" when !OfficeBridge.Available:
                throw new InvalidOperationException("Sunumlar için LibreOffice veya Microsoft PowerPoint gerekli (Ayarlar → Eksik araçları kur).");
            case "act:copy" or "act:move":
                return await Task.Run(() => FileOps.CopyOrMove(job.Inputs, job.Opts["target"], id == "act:move", Frac, log, ct), ct);
        }

        // ---- per-file tools ----
        string? last = null;
        int n = job.Inputs.Count;
        var failures = new List<string>();
        for (int i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            string input = job.Inputs[i];
            string name = Path.GetFileName(input);
            int index = i;
            void FileFrac(double f) => progress.Report(new ProgressInfo((index + f) / n, $"({index + 1}/{n}) {name}"));
            FileFrac(0);
            log($"▶ {name}");
            try
            {
                string target = id[(id.IndexOf(':') + 1)..];
                last = id switch
                {
                    "img:qr" or "pdf:qr" => await QrTools.ScanAsync(input, log, ct),
                    "img:ocr" => await OcrTools.ImageAsync(input, log, ct),
                    "tr:translate" => await TranslateTools.RunAsync(input, job.Opts["from"], job.Opts["to"], FileFrac, log, ct),
                    "pdf:ocr" or "pdf:ocrclean" => await OcrTools.PdfAsync(input, job.Opts.GetValueOrDefault("range") == "Belirli sayfalar" ? job.Opts.GetValueOrDefault("pages") : null,
                        job.Opts.GetValueOrDefault("text") == "PDF + metin (.txt)", clean: id == "pdf:ocrclean", FileFrac, log, ct),
                    _ when id.StartsWith("img:", StringComparison.Ordinal) => await ImageTools.RunAsync(id, input, job.Opts, log, ct),
                    _ when id.StartsWith("av:", StringComparison.Ordinal) => await MediaTools.RunAsync(id, input, job.Opts, FileFrac, log, ct),

                    "pdf:split" => await Task.Run(() => PdfTools.Split(input, log), ct),
                    "pdf:pages" or "pdf:organize" => await Task.Run(() => PdfTools.Organize(input, job.Opts["pages"],
                        job.Opts.GetValueOrDefault("rotate") switch { "90° sağa" => 90, "90° sola" => 270, "180°" => 180, _ => 0 }, log), ct),
                    "pdf:compress" => await Task.Run(() => PdfTools.Compress(input, log), ct),
                    "pdf:strip" => await Task.Run(() => PdfTools.StripMetadata(input), ct),
                    "pdf:jpg" or "pdf:png" => await PdfRender.PagesToImagesAsync(input, target, input, ct),
                    "pdf:xlsx" => await Task.Run(() => { var o = OutputPaths.Beside(input, "xlsx"); SheetTools.WriteXlsx(PdfText.Table(input), o); return o; }, ct),
                    "pdf:docx" => await PdfToDocx.ConvertAsync(input, FileFrac, log, ct),
                    "pdf:epub" or "pdf:txt" or "pdf:md" => await Sta.Run(() =>
                        DocTools.Write(PdfText.Blocks(input), Path.GetFileNameWithoutExtension(input), target, input)),

                    _ when id.StartsWith("doc:", StringComparison.Ordinal) => await DocTools.ConvertAsync(input, target, log, ct),
                    _ when id.StartsWith("sheet:", StringComparison.Ordinal) => await SheetTools.ConvertAsync(input, target, log, ct),
                    "slide:pdf" => await OfficeBridge.ConvertAsync(input, "pdf", ct),
                    "slide:jpg" or "slide:png" => await SlidesToImagesAsync(input, target, ct),
                    _ when id.StartsWith("sub:", StringComparison.Ordinal) => await Task.Run(() => SubtitleTools.Convert(input, target), ct),

                    "zip:extract" or "arc:extract" => await Task.Run(() => ArchiveTools.Extract(input, FileFrac, log, ct), ct),
                    "arc:zip" or "arc:tar" or "arc:gz" => await Task.Run(() => ArchiveTools.Convert(input, target, FileFrac, log, ct), ct),
                    _ => throw new ArgumentException("Bilinmeyen işlem: " + id),
                };
                log("✔ " + last);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failures.Add($"{name}: {ex.Message}");
                log("✖ " + ex.Message);
            }
        }

        progress.Report(new ProgressInfo(1, "Bitti"));
        if (failures.Count > 0)
            throw new AggregateException($"{failures.Count}/{n} dosya başarısız:\n" + string.Join("\n", failures));
        return last;
    }

    private static async Task<string> SlidesToImagesAsync(string input, string format, CancellationToken ct)
    {
        if (!OfficeBridge.Available) throw new InvalidOperationException("Sunumlar için LibreOffice veya Microsoft PowerPoint gerekli.");
        string pdf = await OfficeBridge.ConvertToTempPdfAsync(input, ct);
        try { return await PdfRender.PagesToImagesAsync(pdf, format, input, ct); }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(pdf)!, true); } catch { }
        }
    }
}

/// <summary>Copy / move into a folder without ever overwriting (name clashes get " (2)").</summary>
public static class FileOps
{
    public static string CopyOrMove(IReadOnlyList<string> inputs, string targetDir, bool move, Action<double> frac, Action<string> log, CancellationToken ct)
    {
        if (!Directory.Exists(targetDir)) throw new DirectoryNotFoundException(targetDir);
        string? last = null;
        for (int i = 0; i < inputs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string src = inputs[i];
            bool isDir = Directory.Exists(src);
            string name = Path.GetFileName(src.TrimEnd('\\'));
            string dest = isDir
                ? OutputPaths.Unique(targetDir, name, "")
                : OutputPaths.Unique(targetDir, Path.GetFileNameWithoutExtension(src), Path.GetExtension(src));
            if (isDir)
            {
                if (Path.GetFullPath(targetDir).StartsWith(Path.GetFullPath(src).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Bir klasör kendi içine kopyalanamaz/taşınamaz.");
                if (move && SameVolume(src, targetDir)) Directory.Move(src, dest);
                else { CopyDir(src, dest, ct); if (move) Directory.Delete(src, true); }
            }
            else
            {
                if (move) File.Move(src, dest, overwrite: false);
                else File.Copy(src, dest, overwrite: false);
            }
            log((move ? "Taşındı: " : "Kopyalandı: ") + dest);
            last = dest;
            frac((i + 1.0) / inputs.Count);
        }
        return last ?? targetDir;
    }

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static void CopyDir(string src, string dest, CancellationToken ct)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(src, dir)));
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(src, file)), overwrite: false);
        }
    }
}
