using System.IO;
using System.Runtime.InteropServices;

namespace GKRStudio.Core;

/// <summary>
/// High-fidelity Office conversions. Prefers LibreOffice (free, headless); falls back to an installed
/// Microsoft Office via COM. Output never overwrites: results are written to a temp folder, then moved.
/// </summary>
public static class OfficeBridge
{
    public static string? Soffice
    {
        get
        {
            if (OperatingSystem.IsMacOS())
            {
                foreach (var app in new[] { "/Applications/LibreOffice.app", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications/LibreOffice.app") })
                    if (File.Exists(Path.Combine(app, "Contents/MacOS/soffice"))) return Path.Combine(app, "Contents/MacOS/soffice");
                return Tools.Find("soffice");
            }
            foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                string p = Path.Combine(root, "LibreOffice", "program", "soffice.com");
                if (File.Exists(p)) return p;
                p = Path.Combine(root, "LibreOffice", "program", "soffice.exe");
                if (File.Exists(p)) return p;
            }
            return Tools.Find("soffice.exe");
        }
    }

    private static bool HasProgId(string id) => OperatingSystem.IsWindows() && Type.GetTypeFromProgID(id) != null;
    public static bool HasWord => HasProgId("Word.Application");
    public static bool HasExcel => HasProgId("Excel.Application");
    public static bool HasPowerPoint => HasProgId("PowerPoint.Application");

    public static bool Available => Soffice != null || HasWord || HasExcel || HasPowerPoint;
    public static string Describe => Soffice != null ? "LibreOffice" : "Microsoft Office";

    /// <summary>Converts to <paramref name="targetExt"/> next to the input.</summary>
    public static async Task<string> ConvertAsync(string input, string targetExt, CancellationToken ct, string? suffix = null)
    {
        string temp = await ConvertToTempAsync(input, targetExt, ct);
        try
        {
            string output = OutputPaths.Beside(input, targetExt, suffix);
            File.Move(temp, output);
            return output;
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(temp)!, true); } catch { }
        }
    }

    /// <summary>Converts to a PDF in a private temp folder (caller deletes it).</summary>
    public static Task<string> ConvertToTempPdfAsync(string input, CancellationToken ct) => ConvertToTempAsync(input, "pdf", ct);

    private static async Task<string> ConvertToTempAsync(string input, string targetExt, CancellationToken ct)
    {
        string dir = Path.Combine(Path.GetTempPath(), "gkrstudio-office-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string expected = Path.Combine(dir, Path.GetFileNameWithoutExtension(input) + "." + targetExt);

        if (Soffice is { } soffice)
        {
            string filter = targetExt switch { "csv" => "csv:Text - txt - csv (StarCalc):44,34,76", "tsv" => "csv:Text - txt - csv (StarCalc):9,34,76", "txt" => "txt:Text (encoded):UTF8", _ => targetExt };
            var log = new List<string>();
            int code = await ProcessRunner.RunAsync(soffice,
                ["--headless", "--norestore", "--nolockcheck", $"-env:UserInstallation=file:///{dir.Replace('\\', '/')}/profile",
                 "--convert-to", filter, "--outdir", dir, input], l => log.Add(l), ct);
            string produced = targetExt == "tsv" ? Path.ChangeExtension(expected, "csv") : expected;
            if (code == 0 && File.Exists(produced))
            {
                if (produced != expected) File.Move(produced, expected);
                return expected;
            }
            throw new InvalidOperationException("LibreOffice dönüştüremedi: " + string.Join(" ", log.TakeLast(3)));
        }

        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("LibreOffice gerekli.");
        await Sta.Run(() => { ConvertWithMsOffice(input, expected, targetExt); return 0; });
        if (!File.Exists(expected)) throw new InvalidOperationException("Microsoft Office çıktı üretmedi.");
        return expected;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void ConvertWithMsOffice(string input, string output, string ext)
    {
        var kind = Catalog.KindOf(input);
        dynamic? app = null;
        try
        {
            switch (kind)
            {
                case InputKind.Document:
                {
                    app = Create("Word.Application");
                    app.DisplayAlerts = 0;
                    dynamic doc = app.Documents.Open(input, false, true);
                    int fmt = ext switch { "pdf" => 17, "docx" => 16, "txt" => 7, "html" => 8, "rtf" => 6, "odt" => 23, _ => throw Unsupported(ext) };
                    doc.SaveAs2(output, fmt);
                    doc.Close(false);
                    break;
                }
                case InputKind.Spreadsheet:
                {
                    app = Create("Excel.Application");
                    app.DisplayAlerts = false;
                    dynamic wb = app.Workbooks.Open(input, 0, true);
                    if (ext == "pdf") wb.ExportAsFixedFormat(0, output);
                    else wb.SaveAs(output, ext switch { "xlsx" => 51, "ods" => 60, "csv" => 62, "tsv" => 42, _ => throw Unsupported(ext) });
                    wb.Close(false);
                    break;
                }
                case InputKind.Presentation:
                {
                    app = Create("PowerPoint.Application");
                    dynamic pres = app.Presentations.Open(input, -1, 0, 0);
                    pres.SaveAs(output, ext switch { "pdf" => 32, "pptx" => 24, _ => throw Unsupported(ext) });
                    pres.Close();
                    break;
                }
                default:
                    throw new InvalidOperationException("Office bu dosya türünü açamıyor.");
            }
        }
        finally
        {
            try { app?.Quit(); } catch { }
            if (app != null) Marshal.FinalReleaseComObject(app);
        }
    }

    private static dynamic Create(string progId) =>
        Activator.CreateInstance(Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException(progId + " kurulu değil."))!;

    private static Exception Unsupported(string ext) => new NotSupportedException("Office ile bu hedefe dönüştürülemiyor: " + ext);
}
