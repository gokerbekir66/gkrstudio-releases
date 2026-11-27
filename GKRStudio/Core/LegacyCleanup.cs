using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace GKRStudio.Core;

/// <summary>
/// The app was called "LionMedia" up to version 1.1.6. On start: take over its settings once and remove what the old
/// version left registered (right-click menu, autostart, macOS login agent and Quick Actions), so nothing points to the
/// old app any more. Old names are spelled out in pieces so a project-wide rename never rewrites them.
/// </summary>
public static class LegacyCleanup
{
    private static readonly string Old = "Lion" + "Media";
    private static readonly string OldLower = "lion" + "media";

    public static void Run()
    {
        try { MigrateSettings(); } catch { }
        try
        {
            bool hadMenu = OperatingSystem.IsWindows() ? CleanWindows() : OperatingSystem.IsMacOS() && CleanMac();
            if (hadMenu)
            {
                Preferences.Load(); // sets the UI language, so the menu texts come out in it
                ShellIntegration.Register(); // the old right-click menu / Quick Actions become GKR Studio's
            }
        }
        catch { }
    }

    private static void MigrateSettings()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string oldFile = Path.Combine(appData, Old, "settings.json");
        string newDir = Path.Combine(appData, "GKRStudio");
        string newFile = Path.Combine(newDir, "settings.json");
        if (File.Exists(newFile) || !File.Exists(oldFile)) return;
        Directory.CreateDirectory(newDir);
        File.Copy(oldFile, newFile);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool CleanWindows()
    {
        bool had = false;
        foreach (string key in new[]
                 {
                     @"Software\Classes\*\shell\" + Old,
                     @"Software\Classes\Directory\Background\shell\" + Old,
                     @"Software\Classes\DesktopBackground\shell\" + Old,
                 })
        {
            using (var k = Registry.CurrentUser.OpenSubKey(key)) had |= k != null;
            Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        }
        using var run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        run?.DeleteValue(Old, throwOnMissingValue: false);
        return had;
    }

    private static bool CleanMac()
    {
        bool had = false;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string agent = $"com.{OldLower}.agent";
        string plist = Path.Combine(home, "Library", "LaunchAgents", agent + ".plist");
        if (File.Exists(plist))
        {
            Process.Start(new ProcessStartInfo("/bin/launchctl") { ArgumentList = { "remove", agent }, UseShellExecute = false })?.WaitForExit(5000);
            File.Delete(plist);
        }
        string services = Path.Combine(home, "Library", "Services");
        if (Directory.Exists(services))
            foreach (var dir in Directory.EnumerateDirectories(services, Old + "*.workflow"))
            {
                Directory.Delete(dir, recursive: true);
                had = true;
            }
        return had;
    }
}
