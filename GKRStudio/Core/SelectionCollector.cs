using System.IO;
using System.IO.Pipes;
using System.Text;

namespace GKRStudio.Core;

/// <summary>
/// Explorer starts one process per selected file. The first process becomes the short-lived leader,
/// collects the paths of its siblings over a named pipe for a few hundred ms, and shows ONE menu.
/// Nothing survives the invocation, so there is no state to go stale.
/// </summary>
public static class SelectionCollector
{
    private static readonly string Session = Environment.UserName + "." + System.Diagnostics.Process.GetCurrentProcess().SessionId;
    private static readonly string MutexName = @"Local\GKRStudio.Selection." + Session;
    private static readonly string PipeName = "GKRStudio.Selection." + Session;

    private static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(3);

    /// <returns>All selected paths if this process is the leader; <c>null</c> if the paths were handed to another process.</returns>
    public static async Task<IReadOnlyList<string>?> CollectAsync(IReadOnlyList<string> mine)
    {
        using var mutex = new Mutex(initiallyOwned: true, MutexName, out bool leader);
        if (!leader)
        {
            if (TrySend(mine)) return null;
            return mine; // leader unreachable: just handle our own file
        }

        var all = new List<string>(mine);
        try
        {
            var started = DateTime.UtcNow;
            var deadline = started + Quiet;
            while (DateTime.UtcNow < deadline && DateTime.UtcNow - started < MaxWait)
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                using var cts = new CancellationTokenSource(deadline - DateTime.UtcNow);
                try { await server.WaitForConnectionAsync(cts.Token); }
                catch (OperationCanceledException) { break; }

                using var reader = new StreamReader(server, Encoding.UTF8);
                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                    if (line.Length > 0) all.Add(line);
                deadline = DateTime.UtcNow + Quiet;
            }
        }
        catch (IOException) { }
        finally
        {
            mutex.ReleaseMutex();
        }
        return all.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool TrySend(IReadOnlyList<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            foreach (var p in paths) writer.WriteLine(p);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
