using System.IO;

namespace GKRStudio.Core;

/// <summary>Locates external tools on macOS (user tools → bundled → Homebrew → PATH). Fresh lookup every call.</summary>
public static class Tools
{
    public static string LocalToolsDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "GKRStudio", "tools");

    public static string? Ffmpeg => Find("ffmpeg");
    public static string? Ffprobe => Find("ffprobe");
    public static string? YtDlp => Find("yt-dlp");
    public static string? Deno => Find("deno");
    public static string? Tesseract => Find("tesseract");
    public static string? Brew => Find("brew");

    public static string? Find(string name)
    {
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        if (OperatingSystem.IsWindows()) name += ".exe"; // lets the Mac UI be tried on Windows during development

        var dirs = new List<string>
        {
            LocalToolsDir,                                                       // user-installed/updated copies win (the bundle is signed, read-only)
            Path.Combine(AppContext.BaseDirectory, "tools"),                     // bundled: GKR Studio.app/Contents/MacOS/tools
            Path.Combine(AppContext.BaseDirectory, "..", "Resources", "tools"),
            "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin",       // Finder/login items get a minimal PATH
        };
        char sep = OperatingSystem.IsWindows() ? ';' : ':';
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(sep, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (OperatingSystem.IsWindows())
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));

        foreach (var dir in dirs)
        {
            try
            {
                string candidate = Path.GetFullPath(Path.Combine(dir, name));
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    /// <summary>
    /// Downloads the official standalone yt-dlp (and deno for YouTube) into <see cref="LocalToolsDir"/> — no Homebrew
    /// needed. Plain HTTP downloads are not quarantined by macOS.
    /// </summary>
    public static async Task InstallDownloadToolsAsync(Action<string> status, CancellationToken ct = default)
    {
        Directory.CreateDirectory(LocalToolsDir);
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("GKR Studio");
        if (YtDlp == null)
        {
            status("yt-dlp indiriliyor…");
            string target = Path.Combine(LocalToolsDir, "yt-dlp");
            await Save(http, "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp_macos", target, ct);
            MakeExecutable(target);
        }
        if (Deno == null)
        {
            status("deno indiriliyor…");
            string arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "aarch64" : "x86_64";
            string zip = Path.Combine(Path.GetTempPath(), $"deno-{Guid.NewGuid():N}.zip");
            await Save(http, $"https://github.com/denoland/deno/releases/latest/download/deno-{arch}-apple-darwin.zip", zip, ct);
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/bin/ditto") { ArgumentList = { "-x", "-k", zip, LocalToolsDir }, UseShellExecute = false }))
                p?.WaitForExit(120_000);
            File.Delete(zip);
            MakeExecutable(Path.Combine(LocalToolsDir, "deno"));
        }
    }

    private static async Task Save(System.Net.Http.HttpClient http, string url, string target, CancellationToken ct)
    {
        string temp = target + ".part";
        using (var response = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(temp);
            await src.CopyToAsync(dst, ct);
        }
        File.Move(temp, target, overwrite: true);
    }

    private static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }
}
