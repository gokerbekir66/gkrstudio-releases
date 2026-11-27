using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GKRStudio.Core;

public sealed record UpdateInfo(Version Version, string Notes, string Url, string Sha256);

/// <summary>
/// Remote updates for Windows and macOS.
/// Feed = update.json + update.json.sig (ECDSA P-256 / SHA-256, raw r||s, base64). The signature is checked with the
/// public key embedded below, so only whoever holds the private key (kept outside the repo) can publish updates.
/// Each package is additionally verified with its SHA-256 from the signed manifest.
/// update.json: { "version": "1.2.0", "notes": "...", "platforms": { "windows-x64": { "url": "...", "sha256": "..." }, "macos-arm64": {...}, "macos-x64": {...} } }
/// Relative URLs are resolved against the feed address.
/// </summary>
public static class Updater
{
    /// <summary>Default feed address (empty = not configured; can be set in Settings).</summary>
    public const string DefaultFeed = "https://github.com/gokerbekir66/gkrstudio-releases/releases/latest/download/update.json";

    /// <summary>SubjectPublicKeyInfo (base64) of the release signing key.</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEdT/QQqAsLsNfXmCH+h1Yzs4eoOGcEvplexTIqSCFSNpAhfr2Us4LwdQtbL9Wx+zBjwwpFbi3DU6XKv1eOTPuog==";

    public static Version Current
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    public static string Platform =>
        OperatingSystem.IsWindows() ? "windows-x64"
        : RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "macos-arm64" : "macos-x64";

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd($"GKRStudio/{Current} ({Platform})");
        return c;
    }

    public static string FeedFrom(Preferences p) => string.IsNullOrWhiteSpace(p.UpdateFeed) ? DefaultFeed : p.UpdateFeed.Trim();

    /// <summary>Returns the newer version for this platform, or null when up to date.</summary>
    public static async Task<UpdateInfo?> CheckAsync(string feed, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(feed)) throw new InvalidOperationException(L.T("Güncelleme adresi ayarlanmamış."));
        byte[] json = await Http.GetByteArrayAsync(feed, ct);
        string sig = (await Http.GetStringAsync(feed + ".sig", ct)).Trim();
        if (!Verify(json, sig)) throw new SecurityException(L.T("Güncelleme imzası geçersiz; güncelleme reddedildi."));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = Version.Parse(root.GetProperty("version").GetString()!);
        if (version <= Current) return null;
        if (!root.GetProperty("platforms").TryGetProperty(Platform, out var p)) return null;
        string url = new Uri(new Uri(feed), p.GetProperty("url").GetString()!).AbsoluteUri;
        return new UpdateInfo(version, root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "", url, p.GetProperty("sha256").GetString()!);
    }

    public static bool Verify(byte[] data, string signatureBase64)
    {
        if (PublicKey.StartsWith("__", StringComparison.Ordinal)) return false;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(PublicKey), out _);
            return ecdsa.VerifyData(data, Convert.FromBase64String(signatureBase64), HashAlgorithmName.SHA256);
        }
        catch (FormatException) { return false; }
        catch (CryptographicException) { return false; }
    }

    /// <summary>Downloads the package to a temp file and checks its SHA-256.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, Action<double>? progress, CancellationToken ct = default)
    {
        string file = Path.Combine(Path.GetTempPath(), $"gkrstudio-update-{info.Version}-{Guid.NewGuid():N}.zip");
        using (var response = await Http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = new FileStream(file, FileMode.CreateNew, FileAccess.Write);
            var buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Invoke((double)done / total.Value);
            }
        }
        string hash;
        await using (var fs = File.OpenRead(file)) hash = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct)).ToLowerInvariant();
        if (!hash.Equals(info.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
            throw new SecurityException(L.T("İndirilen güncelleme doğrulanamadı (SHA-256 uyuşmuyor)."));
        }
        return file;
    }

    /// <summary>
    /// Windows helper: waits for the updating process, closes every other copy running from the install folder
    /// (the background process keeps GKRStudio.exe locked), copies with retries, then starts the new version.
    /// Log: %TEMP%\gkrstudio-update.log
    /// </summary>
    public static string WindowsScript(int pid, string payload, string installDir, bool tray, bool reopenSettings, string staging, string zip)
    {
        string exe = Path.Combine(installDir, "GKRStudio.exe");
        return $$"""
            $ErrorActionPreference = 'Continue'
            Start-Transcript -Path (Join-Path $env:TEMP 'gkrstudio-update.log') -Force | Out-Null
            $exe = '{{Esc(exe)}}'
            function Stop-Copies {
                Get-Process -Name 'GKRStudio' -ErrorAction SilentlyContinue |
                    Where-Object { $_.Path -and ($_.Path -ieq $exe) } |
                    ForEach-Object { Write-Output "stop $($_.Id)"; Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }
            }
            Wait-Process -Id {{pid}} -Timeout 60 -ErrorAction SilentlyContinue
            Stop-Copies
            Start-Sleep -Milliseconds 800
            $ok = $false
            for ($i = 0; $i -lt 30 -and -not $ok; $i++) {
                try {
                    Copy-Item -Path '{{Esc(payload)}}\*' -Destination '{{Esc(installDir)}}' -Recurse -Force -ErrorAction Stop
                    $ok = $true
                } catch {
                    Write-Output "copy failed: $($_.Exception.Message)"
                    Stop-Copies
                    Start-Sleep -Seconds 1
                }
            }
            Write-Output "copied: $ok"
            Get-ChildItem -LiteralPath '{{Esc(installDir)}}' -Recurse -File -ErrorAction SilentlyContinue | Unblock-File -ErrorAction SilentlyContinue
            {{(tray ? "Start-Process -FilePath $exe -ArgumentList '--tray'\n            Start-Sleep -Seconds 2" : "")}}
            {{(reopenSettings || !tray ? "Start-Process -FilePath $exe" : "")}}
            Start-Sleep -Seconds 3
            if ($ok) {
                Remove-Item -LiteralPath '{{Esc(staging)}}' -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath '{{Esc(zip)}}' -Force -ErrorAction SilentlyContinue
            }
            Stop-Transcript | Out-Null
            """;
    }

    /// <summary>
    /// Extracts the package and starts a helper script that waits for this process to exit, swaps the files
    /// and starts the new version. The caller must then exit the app.
    /// </summary>
    public static void InstallAndRestart(string zip, bool tray, bool reopenSettings = false)
    {
        string staging = Path.Combine(Path.GetTempPath(), "gkrstudio-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        if (OperatingSystem.IsMacOS())
        {
            // ditto keeps Unix permissions (+x on the app binary); a plain byte copy would leave the app unlaunchable
            var ditto = Process.Start(new ProcessStartInfo("/usr/bin/ditto") { ArgumentList = { "-x", "-k", zip, staging }, UseShellExecute = false });
            ditto?.WaitForExit(120_000);
            if (ditto == null || ditto.ExitCode != 0) throw new InvalidOperationException("Güncelleme paketi açılamadı (ditto).");
        }
        else ZipTools.Extract(zip, _ => { }, _ => { }, CancellationToken.None, staging);
        int pid = Environment.ProcessId;

        if (OperatingSystem.IsWindows())
        {
            string payload = Directory.EnumerateFiles(staging, "GKRStudio.exe", SearchOption.AllDirectories).Select(Path.GetDirectoryName).FirstOrDefault()
                             ?? throw new InvalidOperationException("Paket içinde GKRStudio.exe yok.");
            string installDir = Path.GetDirectoryName(Environment.ProcessPath!)!;
            string script = Path.Combine(staging, "apply-update.ps1");
            File.WriteAllText(script, WindowsScript(pid, payload, installDir, tray, reopenSettings, staging, zip), new UTF8Encoding(true));
            Process.Start(new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script },
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        else
        {
            string newApp = Directory.EnumerateDirectories(staging, "GKR Studio.app", SearchOption.AllDirectories).FirstOrDefault()
                            ?? throw new InvalidOperationException("Paket içinde GKR Studio.app yok.");
            string bundle = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
            if (!bundle.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Uygulama bir .app paketinden çalışmıyor; elle güncelleyin.");
            string script = Path.Combine(staging, "apply-update.sh");
            string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "GKRStudio-update.log");
            Directory.CreateDirectory(Path.GetDirectoryName(log)!);
            string label = "com.gkrstudio.update." + DateTime.Now.Ticks;
            string mode = tray ? "--tray" : "--open";
            // Signing, Dock refresh, login agent and restart are done by the NEW bundle's finish-install.sh, so fixes
            // there apply to the update that ships them. Inline fallback for packages without it.
            File.WriteAllText(script, $"""
                #!/bin/bash
                exec >> "{log}" 2>&1
                # a launchctl-submitted job is restarted when it exits; the staging folder is gone after one run
                if [ ! -d "{staging}" ]; then launchctl remove "{label}" 2>/dev/null; exit 0; fi
                echo "=== GKR Studio update $(date '+%F %T') pid={pid} ==="
                i=0
                while kill -0 {pid} 2>/dev/null; do sleep 0.5; i=$((i + 1)); if [ $i -gt 240 ]; then kill -9 {pid} 2>/dev/null; fi; done
                # sign while still in the staging folder: macOS may forbid changing a bundle inside /Applications
                if [ -f "{newApp}/Contents/Resources/finish-install.sh" ]; then
                  /bin/bash "{newApp}/Contents/Resources/finish-install.sh" "{newApp}" --sign-only
                fi
                rm -rf "{bundle}.old"
                if mv "{bundle}" "{bundle}.old" && mv "{newApp}" "{bundle}"; then
                  rm -rf "{bundle}.old"; echo "bundle replaced"
                else
                  echo "could not replace the bundle"; [ -d "{bundle}" ] || mv "{bundle}.old" "{bundle}"
                fi
                if [ -f "{bundle}/Contents/Resources/finish-install.sh" ]; then
                  /bin/bash "{bundle}/Contents/Resources/finish-install.sh" "{bundle}" {mode}
                else
                  chmod +x "{bundle}/Contents/MacOS/GKRStudio"; xattr -cr "{bundle}"
                  find "{bundle}/Contents/MacOS" -type f ! -name GKRStudio -exec codesign --force --sign - '{"{}"}' ';' 2>/dev/null
                  codesign --force --sign - "{bundle}/Contents/MacOS/GKRStudio"; codesign --force --sign - "{bundle}"
                  open "{bundle}" {(tray ? "--args --tray" : "")}
                fi
                rm -rf "{staging}" "{zip}"
                echo "=== update finished $(date '+%F %T') ==="
                launchctl remove "{label}" 2>/dev/null
                """.Replace("\r\n", "\n"));
            Process.Start("/bin/chmod", ["+x", script])?.WaitForExit();
            File.AppendAllText(log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} GKR Studio {Current}: handing update to {label}\n");
            // Run the script as its own launchd job: when GKR Studio quits (it may itself be a launchd login item), its
            // child processes can be killed — a plain child script would die before swapping the files.
            var submit = Process.Start(new ProcessStartInfo("/bin/launchctl")
            {
                ArgumentList = { "submit", "-l", label, "--", "/bin/bash", script },
                UseShellExecute = false,
            });
            submit?.WaitForExit(10_000);
            if (submit == null || submit.ExitCode != 0) // fallback: detached shell
                Process.Start(new ProcessStartInfo("/bin/bash") { ArgumentList = { "-c", $"nohup /bin/bash '{script}' >/dev/null 2>&1 &" }, UseShellExecute = false });
        }
    }

    private static string Esc(string s) => s.Replace("'", "''");
}
