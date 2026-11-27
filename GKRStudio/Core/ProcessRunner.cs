using System.Diagnostics;
using System.Text;

namespace GKRStudio.Core;

public static class ProcessRunner
{
    /// <summary>Runs a tool with an argument list (no shell quoting issues), streaming every output line.</summary>
    public static async Task<int> RunAsync(string exe, IEnumerable<string> args, Action<string> onLine, CancellationToken ct,
        IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUTF8"] = "1";
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) onLine(e.Data); };

        if (!process.Start()) throw new InvalidOperationException($"{exe} başlatılamadı.");
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using (ct.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }))
        {
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
        return process.ExitCode;
    }
}
