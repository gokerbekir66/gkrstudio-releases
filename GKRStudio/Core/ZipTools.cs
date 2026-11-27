using System.IO;
using System.IO.Compression;

namespace GKRStudio.Core;

/// <summary>ZIP create / safe extract. Extraction always goes into a NEW folder and never overwrites anything.</summary>
public static class ZipTools
{
    public const int MaxEntries = 20_000;
    public const long MaxTotalBytes = 8L * 1024 * 1024 * 1024; // 8 GB unpacked
    public const double MaxRatio = 250;                        // zip-bomb guard (unpacked / packed)

    public static string Create(IReadOnlyList<string> inputs, Action<double> frac, Action<string> log, CancellationToken ct)
    {
        string first = inputs[0];
        string dir = Path.GetDirectoryName(first)!;
        string name = inputs.Count == 1 ? Path.GetFileNameWithoutExtension(first) : new DirectoryInfo(dir).Name;
        string output = OutputPaths.Unique(dir, string.IsNullOrWhiteSpace(name) ? "arşiv" : name, "zip");

        var entries = new List<(string Source, string Entry)>();
        foreach (var path in inputs)
        {
            if (Directory.Exists(path))
            {
                string root = Path.GetDirectoryName(Path.GetFullPath(path).TrimEnd('\\'))!;
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    entries.Add((f, Path.GetRelativePath(root, f).Replace('\\', '/')));
            }
            else if (File.Exists(path))
            {
                entries.Add((path, Path.GetFileName(path)));
            }
        }

        try
        {
            using var fs = new FileStream(output, FileMode.CreateNew, FileAccess.Write);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < entries.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var (source, entry) = entries[i];
                string unique = entry;
                for (int n = 2; !used.Add(unique); n++)
                    unique = Path.ChangeExtension(entry, null) + $" ({n})" + Path.GetExtension(entry);
                zip.CreateEntryFromFile(source, unique, CompressionLevel.Optimal);
                frac((i + 1.0) / entries.Count);
            }
        }
        catch
        {
            try { File.Delete(output); } catch { }
            throw;
        }
        log($"{entries.Count} dosya arşivlendi.");
        return output;
    }

    public static string Extract(string zipPath, Action<double> frac, Action<string> log, CancellationToken ct, string? destination = null)
    {
        using var zip = ZipFile.OpenRead(zipPath);

        // 1) validate everything BEFORE writing a single byte
        if (zip.Entries.Count > MaxEntries)
            throw new InvalidOperationException($"Arşivde çok fazla giriş var ({zip.Entries.Count} > {MaxEntries}).");
        long total = 0, packed = 0;
        foreach (var e in zip.Entries)
        {
            total += e.Length;
            packed += e.CompressedLength;
            CheckName(e.FullName);
        }
        if (total > MaxTotalBytes)
            throw new InvalidOperationException($"Açılmış boyut çok büyük ({total / 1048576} MB > {MaxTotalBytes / 1048576} MB).");
        if (total > 100L * 1024 * 1024 && packed > 0 && (double)total / packed > MaxRatio)
            throw new InvalidOperationException($"Şüpheli sıkıştırma oranı ({total / packed}:1) — zip bombası olabilir.");

        // 2) extract into a brand-new folder
        string parent = Path.GetDirectoryName(zipPath)!;
        string dest = destination ?? OutputPaths.Unique(parent, Path.GetFileNameWithoutExtension(zipPath), "");
        Directory.CreateDirectory(dest);
        string destRoot = Path.GetFullPath(dest) + Path.DirectorySeparatorChar;

        long written = 0;
        int skipped = 0, i = 0;
        try
        {
            foreach (var e in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                i++;
                if (IsLink(e)) { skipped++; log("Link atlandı: " + e.FullName); continue; }

                string target = Path.GetFullPath(Path.Combine(dest, e.FullName.Replace('/', Path.DirectorySeparatorChar)));
                if (!target.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Arşiv klasör dışına yazmaya çalışıyor: " + e.FullName);

                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target)) // duplicate names inside the zip: keep both
                    target = OutputPaths.Unique(Path.GetDirectoryName(target)!, Path.GetFileNameWithoutExtension(target), Path.GetExtension(target));

                using (var src = e.Open())
                using (var dst = new FileStream(target, FileMode.CreateNew, FileAccess.Write))
                {
                    var buffer = new byte[81920];
                    int read;
                    while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        written += read;
                        if (written > MaxTotalBytes || written > total + 1024) // actual size must match the declared size
                            throw new InvalidOperationException("Arşiv beyan edilenden fazla veri içeriyor.");
                        dst.Write(buffer, 0, read);
                    }
                }
                try { File.SetLastWriteTime(target, e.LastWriteTime.DateTime); } catch { }
                frac((double)i / zip.Entries.Count);
            }
        }
        catch
        {
            try { Directory.Delete(dest, recursive: true); } catch { }
            throw;
        }
        log($"{i - skipped} giriş çıkarıldı{(skipped > 0 ? $", {skipped} link atlandı" : "")}.");
        return dest;
    }

    public static void CheckName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Contains('\0') || name.Contains(':') || name.StartsWith('/') || name.StartsWith('\\'))
            throw new InvalidOperationException("Geçersiz/mutlak yol içeren giriş: " + name);
        foreach (var part in name.Split('/', '\\'))
            if (part == "..") throw new InvalidOperationException("Üst klasöre çıkan yol (..) içeren giriş: " + name);
    }

    /// <summary>Unix symlinks (S_IFLNK in the high word) and Windows reparse points are never extracted.</summary>
    private static bool IsLink(ZipArchiveEntry e)
    {
        int attr = e.ExternalAttributes;
        int unixMode = (attr >> 16) & 0xFFFF;
        return (unixMode & 0xF000) == 0xA000 || (attr & 0x400) != 0;
    }
}
