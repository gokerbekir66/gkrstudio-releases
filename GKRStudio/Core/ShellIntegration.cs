using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace GKRStudio.Core;

/// <summary>
/// Adds/removes the Explorer context menu under HKCU\Software\Classes (no admin rights needed).
/// Every entry just launches this exe with the clicked path; the exe decides the menu on the spot.
/// </summary>
public static class ShellIntegration
{
    private const string VerbName = "GKRStudio";
    private const string FilesKey = @"Software\Classes\*\shell\" + VerbName;
    private const string FolderBackgroundKey = @"Software\Classes\Directory\Background\shell\" + VerbName;
    private const string DesktopBackgroundKey = @"Software\Classes\DesktopBackground\shell\" + VerbName;

    private static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("Uygulama yolu bulunamadı.");

    public static void Register()
    {
        string exe = Exe;
        string icon = $"\"{exe}\",0";

        // Shown for every file; the wheel itself decides (from the extension) which tools fit.
        using (var key = Registry.CurrentUser.CreateSubKey(FilesKey))
        {
            key.SetValue("MUIVerb", "GKR Studio");
            key.SetValue("Icon", icon);
            key.DeleteValue("AppliesTo", throwOnMissingValue: false); // older versions limited it to media files
            key.SetValue("MultiSelectModel", "Player");
            using var cmd = key.CreateSubKey("command");
            cmd.SetValue("", $"\"{exe}\" --menu \"%1\"");
        }

        foreach (var path in new[] { FolderBackgroundKey, DesktopBackgroundKey })
        {
            using var key = Registry.CurrentUser.CreateSubKey(path);
            key.SetValue("MUIVerb", L.T("GKR Studio: Panodaki linki indir"));
            key.SetValue("Icon", icon);
            using var cmd = key.CreateSubKey("command");
            cmd.SetValue("", $"\"{exe}\" --background \"%V\"");
        }
        NotifyShell();
    }

    public static void Unregister()
    {
        foreach (var path in new[] { FilesKey, FolderBackgroundKey, DesktopBackgroundKey })
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
        NotifyShell();
    }

    /// <summary>"Registered" means all keys exist and point at this exe (not an older copy elsewhere).</summary>
    public static RegistrationState State()
    {
        string? command = Registry.CurrentUser.OpenSubKey(FilesKey + @"\command")?.GetValue("") as string;
        bool background = Registry.CurrentUser.OpenSubKey(FolderBackgroundKey + @"\command") != null;
        if (command == null && !background) return RegistrationState.NotRegistered;
        if (command != null && background && command.Contains(Exe, StringComparison.OrdinalIgnoreCase)) return RegistrationState.Registered;
        return RegistrationState.Stale;
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    private static void NotifyShell() => SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero);
}

public enum RegistrationState { NotRegistered, Registered, Stale }
