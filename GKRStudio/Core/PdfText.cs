using System.IO;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace GKRStudio.Core;

/// <summary>PDF text extraction (PdfPig): words → lines → paragraphs, plus a column-aware table heuristic.</summary>
public static class PdfText
{
    private sealed record Line(double Y, double Height, List<Word> Words)
    {
        public string Text => string.Join(" ", Words.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text));
    }

    private static List<Line> Lines(Page page)
    {
        var lines = new List<Line>();
        foreach (var word in page.GetWords().OrderByDescending(w => w.BoundingBox.Bottom).ThenBy(w => w.BoundingBox.Left))
        {
            double y = word.BoundingBox.Bottom, h = Math.Max(1, word.BoundingBox.Height);
            var line = lines.LastOrDefault();
            if (line != null && Math.Abs(line.Y - y) < Math.Max(line.Height, h) * 0.5) line.Words.Add(word);
            else lines.Add(new Line(y, h, [word]));
        }
        return lines;
    }

    public static List<Block> Blocks(string path)
    {
        using var doc = PdfDocument.Open(path);
        var blocks = new List<Block>();
        foreach (var page in doc.GetPages())
        {
            var lines = Lines(page);
            if (lines.Count == 0) continue;
            double body = lines.Select(l => l.Height).OrderBy(h => h).ElementAt(lines.Count / 2); // median text height
            var para = new List<string>();
            Line? prev = null;
            void Flush() { if (para.Count > 0) { blocks.Add(new Block(0, string.Join(" ", para))); para.Clear(); } }
            foreach (var line in lines)
            {
                bool gap = prev != null && prev.Y - line.Y > Math.Max(prev.Height, line.Height) * 1.9;
                if (line.Height > body * 1.35 && line.Text.Length < 120)
                {
                    Flush();
                    blocks.Add(new Block(line.Height > body * 1.8 ? 1 : 2, line.Text));
                }
                else
                {
                    if (gap) Flush();
                    para.Add(line.Text);
                }
                prev = line;
            }
            Flush();
        }
        if (blocks.Count == 0) throw new InvalidOperationException("PDF'te seçilebilir metin yok (taranmış belge olabilir).");
        return blocks;
    }

    /// <summary>Rows of cells: a new cell starts where the horizontal gap between words is large.</summary>
    public static List<List<string>> Table(string path)
    {
        using var doc = PdfDocument.Open(path);
        var rows = new List<List<string>>();
        foreach (var page in doc.GetPages())
        {
            foreach (var line in Lines(page))
            {
                var words = line.Words.OrderBy(w => w.BoundingBox.Left).ToList();
                var cells = new List<string>();
                var cell = new List<string> { words[0].Text };
                for (int i = 1; i < words.Count; i++)
                {
                    double gap = words[i].BoundingBox.Left - words[i - 1].BoundingBox.Right;
                    if (gap > line.Height * 1.2) { cells.Add(string.Join(" ", cell)); cell.Clear(); }
                    cell.Add(words[i].Text);
                }
                cells.Add(string.Join(" ", cell));
                rows.Add(cells);
            }
            rows.Add([]); // blank row between pages
        }
        return rows;
    }
}
