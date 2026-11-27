using System.IO;

namespace GKRStudio.Core;

/// <summary>Locates external tools. Looked up fresh on each call (cheap) so installs are picked up immediately.</summary>
public static class Tools
{
    public static string LocalToolsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GKRStudio", "tools");

    public static string? Ffmpeg => Find("ffmpeg.exe");
    public static string? Ffprobe => Find("ffprobe.exe");
    public static string? YtDlp => Find("yt-dlp.exe");
    public static string? Deno => Find("deno.exe");

    public static string? Find(string exe)
    {
        var dirs = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "tools"),
            AppContext.BaseDirectory,
            LocalToolsDir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"),
        };
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        // Machine/User PATH as stored in registry (a just-finished winget install is not in our process PATH yet).
        foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH", target) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var dir in dirs)
        {
            try
            {
                string candidate = Path.Combine(Environment.ExpandEnvironmentVariables(dir), exe);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }

        // winget portable packages (e.g. Gyan.FFmpeg) live under WinGet\Packages\<id>\...\bin\
        try
        {
            string packages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                foreach (var pkg in Directory.EnumerateDirectories(packages))
                {
                    var hit = Directory.EnumerateFiles(pkg, exe, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true }).FirstOrDefault();
                    if (hit != null) return hit;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return null;
    }
}
