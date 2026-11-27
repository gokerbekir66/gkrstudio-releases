using System.IO;
using System.Text.Json;

namespace GKRStudio.Core;

/// <summary>
/// Which settings sections are expanded. Kept apart from settings.json (pure UI convenience, never needed by the wheel).
/// Keys are the Turkish section titles, so they don't change with the UI language.
/// </summary>
public static class UiState
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GKRStudio", "ui-state.json");

    /// <summary>Sections open on first use: the two that matter for setting the app up.</summary>
    private static readonly string[] DefaultOpen = ["Sağ tık menüsü", "Finder menüsü", "Arka plan ve kısayollar"];

    private static HashSet<string>? open;

    public static bool IsOpen(string section) => Load().Contains(section);

    public static void SetOpen(string section, bool isOpen)
    {
        var set = Load();
        if (isOpen ? !set.Add(section) : !set.Remove(section)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(set.OrderBy(s => s, StringComparer.Ordinal).ToArray()));
        }
        catch { /* not important enough to bother the user */ }
    }

    private static HashSet<string> Load()
    {
        if (open != null) return open;
        try
        {
            if (File.Exists(FilePath) && JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath)) is { } saved)
                return open = new HashSet<string>(saved, StringComparer.Ordinal);
        }
        catch { }
        return open = new HashSet<string>(DefaultOpen, StringComparer.Ordinal);
    }
}
