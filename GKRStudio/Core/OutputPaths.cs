using System.IO;

namespace GKRStudio.Core;

public static class OutputPaths
{
    /// <summary>Returns a non-existing path "dir\name[ suffix][ (n)].ext" so nothing is ever overwritten.</summary>
    public static string Unique(string directory, string baseName, string extension, string? suffix = null)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(baseName)) baseName = "dosya";
        if (baseName.Length > 150) baseName = baseName[..150];
        if (extension.Length > 0 && !extension.StartsWith('.')) extension = "." + extension;

        if (suffix != null) suffix = L.T(suffix);
        if (suffix != null) foreach (char c in Path.GetInvalidFileNameChars()) suffix = suffix.Replace(c, '-');
        string stem = suffix == null ? baseName : $"{baseName} {suffix}";
        string path = Path.Combine(directory, stem + extension);
        for (int i = 2; File.Exists(path) || Directory.Exists(path); i++)
            path = Path.Combine(directory, $"{stem} ({i}){extension}");
        return path;
    }

    /// <summary>Output next to the input; same-extension conversions get a suffix.</summary>
    public static string Beside(string input, string extension, string? suffix = null)
    {
        string dir = Path.GetDirectoryName(input) ?? Environment.CurrentDirectory;
        string name = Path.GetFileNameWithoutExtension(input);
        if (suffix == null && string.Equals(Path.GetExtension(input), "." + extension.TrimStart('.'), StringComparison.OrdinalIgnoreCase))
            suffix = "(dönüştürüldü)"; // translated in Unique
        return Unique(dir, name, extension, suffix);
    }

    public static string Downloads =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}
