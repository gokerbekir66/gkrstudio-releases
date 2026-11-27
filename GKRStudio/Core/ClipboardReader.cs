using System.Runtime.InteropServices;
using System.Windows;

namespace GKRStudio.Core;

public static class ClipboardReader
{
    /// <summary>Reads clipboard text right now (call on the UI/STA thread). Retries briefly if another app holds the clipboard.</summary>
    public static string? ReadText()
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : null;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
            catch (ExternalException)
            {
                Thread.Sleep(40);
            }
        }
        return null;
    }

    public static LinkInfo? ReadLink() => LinkParser.Parse(ReadText());
}
