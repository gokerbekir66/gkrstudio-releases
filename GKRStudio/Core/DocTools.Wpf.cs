using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GKRStudio.Core;

/// <summary>Windows (WPF) parts of DocTools: RTF reading and page rendering.</summary>
public static partial class DocTools
{
    /// <summary>RTF via WPF's own RTF reader (STA thread).</summary>
    public static List<Block> FromRtf(string path)
    {
        var doc = new FlowDocument();
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using (var fs = File.OpenRead(path)) range.Load(fs, DataFormats.Rtf);
        var blocks = new List<Block>();
        foreach (var block in doc.Blocks)
            Collect(block, blocks);
        return blocks;

        static void Collect(System.Windows.Documents.Block b, List<Block> into)
        {
            switch (b)
            {
                case Paragraph p:
                {
                    string text = new TextRange(p.ContentStart, p.ContentEnd).Text.Trim();
                    if (text.Length == 0) return;
                    bool bold = p.FontWeight >= FontWeights.Bold || p.Inlines.All(i => i.FontWeight >= FontWeights.Bold);
                    into.Add(new Block(bold && p.FontSize >= 16 && text.Length < 120 ? 1 : 0, text));
                    break;
                }
                case List l:
                    foreach (var item in l.ListItems)
                        foreach (var inner in item.Blocks)
                            if (inner is Paragraph ip && new TextRange(ip.ContentStart, ip.ContentEnd).Text.Trim() is { Length: > 0 } t)
                                into.Add(new Block(0, t, true));
                    break;
                case Section s:
                    foreach (var inner in s.Blocks) Collect(inner, into);
                    break;
                case Table t:
                    foreach (var group in t.RowGroups)
                        foreach (var row in group.Rows)
                            into.Add(new Block(0, string.Join("\t", row.Cells.Select(c => new TextRange(c.ContentStart, c.ContentEnd).Text.Trim()))));
                    break;
            }
        }
    }

    /// <summary>Renders A4 pages (150 DPI) with WPF text layout; one file, or a folder for several pages.</summary>
    public static string RenderImages(List<Block> blocks, string format, string besideInput)
    {
        const int W = 1240, H = 1754, M = 110;
        var pages = new List<DrawingVisual>();
        DrawingVisual? visual = null;
        DrawingContext? dc = null;
        double y = 0;
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var bold = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        void NewPage()
        {
            dc?.Close();
            visual = new DrawingVisual();
            dc = visual.RenderOpen();
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, W, H));
            pages.Add(visual);
            y = M;
        }
        NewPage();
        foreach (var b in blocks)
        {
            double size = b.Heading switch { 1 => 40, 2 => 32, 3 => 27, > 3 => 24, _ => 22 };
            var ft = new FormattedText((b.ListItem ? "• " : "") + b.Text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                                       b.Heading > 0 ? bold : typeface, size, Brushes.Black, 1.5) { MaxTextWidth = W - 2 * M };
            if (y + ft.Height > H - M && y > M) NewPage();
            dc!.DrawText(ft, new Point(M, y));
            y += ft.Height + size * 0.6;
        }
        dc!.Close();

        string name = Path.GetFileNameWithoutExtension(besideInput);
        string dir = Path.GetDirectoryName(besideInput)!;
        string target = pages.Count == 1 ? dir : OutputPaths.Unique(dir, name, "", $"({format.ToUpperInvariant()})");
        if (pages.Count > 1) Directory.CreateDirectory(target);
        string? single = null;
        for (int i = 0; i < pages.Count; i++)
        {
            var rtb = new RenderTargetBitmap(W, H, 150, 150, PixelFormats.Pbgra32);
            rtb.Render(pages[i]);
            string path = pages.Count == 1 ? OutputPaths.Beside(besideInput, format) : OutputPaths.Unique(target, $"{L.T("sayfa")} {i + 1:000}", format);
            ImageTools.Save(rtb, format, path);
            single = path;
        }
        return pages.Count == 1 ? single! : target;
    }

}
