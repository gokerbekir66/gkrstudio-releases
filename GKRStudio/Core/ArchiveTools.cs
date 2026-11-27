using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using SharpCompress.Readers;

namespace GKRStudio.Core;

/// <summary>RAR / TAR / TAR.GZ / GZ / 7Z extraction (SharpCompress) with the same safety checks as ZIP,
/// and archive → ZIP / TAR / TAR.GZ conversion. Nothing is ever overwritten.</summary>
public static class ArchiveTools
{
    public static string BaseName(string path)
    {
        string name = Path.GetFileName(path);
        foreach (var ext in new[] { ".tar.gz", ".tgz", ".tar", ".zip", ".rar", ".gz", ".7z" })
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return name[..^ext.Length];
        return Path.GetFileNameWithoutExtension(name);
    }

    public static string Extract(string archive, Action<double> frac, Action<string> log, CancellationToken ct, string? destination = null)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return ZipTools.Extract(archive, frac, log, ct, destination);

        // pass 1: validate (names, links, count, declared size, ratio) before writing anything
        long total = 0;
        int count = 0;
        long packed = new FileInfo(archive).Length;
        using (var fs = File.OpenRead(archive))
        using (var reader = ReaderFactory.OpenReader(fs))
        {
            while (reader.MoveToNextEntry())
            {
                var e = reader.Entry;
                if (++count > ZipTools.MaxEntries) throw new InvalidOperationException($"Arşivde çok fazla giriş var (> {ZipTools.MaxEntries}).");
                if (e.Key != null) ZipTools.CheckName(e.Key);
                total += Math.Max(0, e.Size);
                if (total > ZipTools.MaxTotalBytes) throw new InvalidOperationException("Açılmış boyut çok büyük.");
            }
        }
        if (total > 100L * 1024 * 1024 && (double)total / Math.Max(1, packed) > ZipTools.MaxRatio)
            throw new InvalidOperationException("Şüpheli sıkıştırma oranı — arşiv bombası olabilir.");

        string dest = destination ?? OutputPaths.Unique(Path.GetDirectoryName(archive)!, BaseName(archive), "");
        Directory.CreateDirectory(dest);
        string root = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;
        long written = 0;
        int done = 0, skipped = 0;
        try
        {
            using var fs = File.OpenRead(archive);
            using var reader = ReaderFactory.OpenReader(fs);
            while (reader.MoveToNextEntry())
            {
                ct.ThrowIfCancellationRequested();
                var e = reader.Entry;
                done++;
                if (!string.IsNullOrEmpty(e.LinkTarget)) { skipped++; log("Link atlandı: " + e.Key); continue; }
                string key = e.Key ?? BaseName(archive); // plain .gz has no inner name
                string target = Path.GetFullPath(Path.Combine(dest, key.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Arşiv klasör dışına yazmaya çalışıyor: " + key);
                if (e.IsDirectory) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                    target = OutputPaths.Unique(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target), Path.GetExtension(target));

                using var src = reader.OpenEntryStream();
                using var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write);
                var buffer = new byte[81920];
                int read;
                while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                {
                    written += read;
                    if (written > ZipTools.MaxTotalBytes) throw new InvalidOperationException("Arşiv izin verilen boyutu aşıyor.");
                    dst.Write(buffer, 0, read);
                }
                frac((double)done / Math.Max(1, count));
            }
        }
        catch
        {
            try { Directory.Delete(dest, true); } catch { }
            throw;
        }
        log($"{done - skipped} giriş çıkarıldı{(skipped > 0 ? $", {skipped} link atlandı" : "")}.");
        return dest;
    }

    /// <summary>arc:zip / arc:tar / arc:gz — safe extract to temp, then repack.</summary>
    public static string Convert(string archive, string target, Action<double> frac, Action<string> log, CancellationToken ct)
    {
        string temp = Path.Combine(Path.GetTempPath(), "gkrstudio-arc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            Extract(archive, f => frac(f * 0.6), log, ct, temp);
            string dir = Path.GetDirectoryName(archive)!;
            string name = BaseName(archive);
            string output = OutputPaths.Unique(dir, name, target switch { "zip" => ".zip", "tar" => ".tar", _ => ".tar.gz" });
            using (var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write))
            {
                switch (target)
                {
                    case "zip":
                        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                            foreach (var f in Directory.EnumerateFiles(temp, "*", SearchOption.AllDirectories))
                                zip.CreateEntryFromFile(f, Path.GetRelativePath(temp, f).Replace('\\', '/'), CompressionLevel.Optimal);
                        break;
                    case "tar":
                        TarFile.CreateFromDirectory(temp, fs, includeBaseDirectory: false);
                        break;
                    default:
                        using (var gz = new GZipStream(fs, CompressionLevel.Optimal))
                            TarFile.CreateFromDirectory(temp, gz, includeBaseDirectory: false);
                        break;
                }
            }
            frac(1);
            return output;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }
}
