using System.IO;
using PdfSharp.Fonts;

namespace GKRStudio.Core;

/// <summary>Serves TrueType files from C:\Windows\Fonts to PDFsharp (embedded, full Unicode incl. Turkish).</summary>
public sealed class WindowsFontResolver : IFontResolver
{
    private static readonly Dictionary<string, string[]> Files = OperatingSystem.IsMacOS() ? MacFiles : WinFiles;

    private static Dictionary<string, string[]> MacFiles => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Segoe UI"] = ["Arial.ttf", "Arial Bold.ttf", "Arial Italic.ttf", "Arial Bold Italic.ttf"],
        ["Consolas"] = ["Courier New.ttf", "Courier New Bold.ttf", "Courier New Italic.ttf", "Courier New Bold Italic.ttf"],
        ["Arial"] = ["Arial.ttf", "Arial Bold.ttf", "Arial Italic.ttf", "Arial Bold Italic.ttf"],
    };

    private static Dictionary<string, string[]> WinFiles => new(StringComparer.OrdinalIgnoreCase)
    {
        // family → regular, bold, italic, bold-italic (first existing file wins, Arial as fallback)
        ["Segoe UI"] = ["segoeui.ttf", "segoeuib.ttf", "segoeuii.ttf", "segoeuiz.ttf"],
        ["Consolas"] = ["consola.ttf", "consolab.ttf", "consolai.ttf", "consolaz.ttf"],
        ["Arial"] = ["arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf"],
    };

    private static readonly string[] FontDirs = OperatingSystem.IsMacOS()
        ? ["/System/Library/Fonts/Supplemental", "/Library/Fonts", "/System/Library/Fonts",
           Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library/Fonts")]
        : [Environment.GetFolderPath(Environment.SpecialFolder.Fonts)];

    private static string? Locate(string file) => FontDirs.Select(d => Path.Combine(d, file)).FirstOrDefault(File.Exists);

    public FontResolverInfo? ResolveTypeface(string familyName, bool bold, bool italic)
    {
        int index = (bold ? 1 : 0) + (italic ? 2 : 0);
        foreach (var family in new[] { familyName, "Arial" })
        {
            if (!Files.TryGetValue(family, out var files)) continue;
            foreach (var candidate in new[] { files[index], files[bold ? 1 : 0], files[0] })
                if (Locate(candidate) != null) return new FontResolverInfo(candidate);
        }
        return null;
    }

    public byte[]? GetFont(string faceName)
    {
        string? path = Locate(faceName);
        return path != null ? File.ReadAllBytes(path) : null;
    }
}
