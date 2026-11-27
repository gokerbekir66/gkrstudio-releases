using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace GKRStudio.Core;

/// <summary>Spreadsheets: CSV/TSV/XLSX read & write natively; XLS/ODS (and best-quality PDF) via LibreOffice/Excel.</summary>
public static class SheetTools
{
    public static async Task<string> ConvertAsync(string input, string target, Action<string> log, CancellationToken ct)
    {
        string ext = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
        if (target == "ods" || ext is "xls" or "ods" || (target == "pdf" && OfficeBridge.Available))
        {
            if (!OfficeBridge.Available)
                throw new InvalidOperationException($"{(target == "ods" ? "ODS çıktısı" : ext.ToUpperInvariant() + " okuma")} için LibreOffice veya Microsoft Excel gerekli.");
            log("Office ile dönüştürülüyor: " + OfficeBridge.Describe);
            return await OfficeBridge.ConvertAsync(input, target, ct);
        }

        var rows = Read(input);
        log($"{rows.Count} satır okundu.");
        string output = OutputPaths.Beside(input, target);
        switch (target)
        {
            case "csv": File.WriteAllText(output, ToDelimited(rows, ','), new UTF8Encoding(true)); break; // BOM: Excel opens UTF-8 correctly
            case "tsv": File.WriteAllText(output, ToDelimited(rows, '\t'), new UTF8Encoding(true)); break;
            case "xlsx": WriteXlsx(rows, output); break;
            case "pdf": WritePdf(rows, output); break;
            default: throw new NotSupportedException(target);
        }
        return output;
    }

    public static List<List<string>> Read(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".csv" => ParseDelimited(File.ReadAllText(path), DetectDelimiter(path)),
        ".tsv" => ParseDelimited(File.ReadAllText(path), '\t'),
        ".xlsx" => ReadXlsx(path),
        var e => throw new NotSupportedException("Okunamayan tablo: " + e),
    };

    private static char DetectDelimiter(string path)
    {
        string first = File.ReadLines(path).FirstOrDefault() ?? "";
        return first.Count(c => c == ';') > first.Count(c => c == ',') ? ';' : ',';
    }

    public static List<List<string>> ParseDelimited(string text, char sep)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == sep) { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(cell.ToString()); cell.Clear();
                rows.Add(row); row = [];
            }
            else if (c != '﻿') cell.Append(c);
        }
        if (cell.Length > 0 || row.Count > 0) { row.Add(cell.ToString()); rows.Add(row); }
        return rows;
    }

    public static string ToDelimited(List<List<string>> rows, char sep)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            sb.AppendJoin(sep, row.Select(c => sep == '\t' ? c.Replace('\t', ' ').Replace('\n', ' ')
                : c.IndexOfAny([sep, '"', '\n', '\r']) >= 0 ? "\"" + c.Replace("\"", "\"\"") + "\"" : c));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>First worksheet; shared strings, inline strings, numbers and booleans.</summary>
    public static List<List<string>> ReadXlsx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        XDocument Load(string name)
        {
            var e = zip.GetEntry(name) ?? throw new InvalidDataException("XLSX parçası eksik: " + name);
            using var s = e.Open();
            return XDocument.Load(s);
        }

        var shared = zip.GetEntry("xl/sharedStrings.xml") != null
            ? Load("xl/sharedStrings.xml").Root!.Elements(S + "si").Select(si => string.Concat(si.Descendants(S + "t").Select(t => t.Value))).ToList()
            : [];

        string sheetPath = "xl/worksheets/sheet1.xml";
        var workbook = Load("xl/workbook.xml");
        string? rid = workbook.Descendants(S + "sheet").FirstOrDefault()?.Attribute(R + "id")?.Value;
        if (rid != null && zip.GetEntry("xl/_rels/workbook.xml.rels") != null)
        {
            var target = Load("xl/_rels/workbook.xml.rels").Root!.Elements().FirstOrDefault(e => (string?)e.Attribute("Id") == rid)?.Attribute("Target")?.Value;
            if (target != null) sheetPath = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
        }

        var rows = new List<List<string>>();
        foreach (var r in Load(sheetPath).Descendants(S + "row"))
        {
            var row = new List<string>();
            foreach (var c in r.Elements(S + "c"))
            {
                int col = ColumnIndex((string?)c.Attribute("r"), row.Count);
                while (row.Count < col) row.Add("");
                string type = (string?)c.Attribute("t") ?? "";
                string v = c.Element(S + "v")?.Value ?? "";
                row.Add(type switch
                {
                    "s" when int.TryParse(v, out int i) && i < shared.Count => shared[i],
                    "inlineStr" => string.Concat(c.Descendants(S + "t").Select(t => t.Value)),
                    "b" => v == "1" ? "TRUE" : "FALSE",
                    _ => v,
                });
            }
            rows.Add(row);
        }
        return rows;
    }

    private static int ColumnIndex(string? cellRef, int fallback)
    {
        if (string.IsNullOrEmpty(cellRef)) return fallback;
        int col = 0;
        foreach (char ch in cellRef)
        {
            if (!char.IsLetter(ch)) break;
            col = col * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return col - 1;
    }

    private static string ColumnName(int index)
    {
        string name = "";
        for (index++; index > 0; index = (index - 1) / 26) name = (char)('A' + (index - 1) % 26) + name;
        return name;
    }

    public static void WriteXlsx(List<List<string>> rows, string output)
    {
        var data = new StringBuilder();
        for (int r = 0; r < rows.Count; r++)
        {
            data.Append($"<row r=\"{r + 1}\">");
            for (int c = 0; c < rows[r].Count; c++)
            {
                string v = rows[r][c];
                string cref = ColumnName(c) + (r + 1);
                if (v.Length == 0) continue;
                if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _) && !v.StartsWith('0') || v == "0")
                    data.Append($"<c r=\"{cref}\"><v>{v}</v></c>");
                else
                    data.Append($"<c r=\"{cref}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{new XText(v)}</t></is></c>");
            }
            data.Append("</row>");
        }
        DocTools.WriteZip(output, new()
        {
            ["[Content_Types].xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>",
            ["_rels/.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>",
            ["xl/workbook.xml"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Sayfa1\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>",
            ["xl/_rels/workbook.xml.rels"] = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>",
            ["xl/worksheets/sheet1.xml"] = $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{data}</sheetData></worksheet>",
        });
    }

    /// <summary>Fallback PDF without Office: monospace, column-aligned text.</summary>
    private static void WritePdf(List<List<string>> rows, string output)
    {
        int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
        var widths = Enumerable.Range(0, cols).Select(c => Math.Min(40, rows.Max(r => c < r.Count ? r[c].Length : 0))).ToArray();
        var lines = rows.Select(r => new Block(0, string.Join("  ", Enumerable.Range(0, cols).Select(c =>
        {
            string v = c < r.Count ? r[c] : "";
            if (v.Length > widths[c]) v = v[..Math.Max(1, widths[c] - 1)] + "…";
            return v.PadRight(widths[c]);
        })).TrimEnd())).ToList();
        DocTools.WritePdf(lines, output, monospace: true);
    }
}
