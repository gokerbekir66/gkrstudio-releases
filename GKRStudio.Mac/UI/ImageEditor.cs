using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GKRStudio.Core;
using SkiaSharp;

namespace GKRStudio.Mac;

/// <summary>Image editor: pen, rectangle, arrow, crop, rotate, flip, undo. Each edit is flattened into the bitmap; Save writes a new copy.</summary>
public sealed class ImageEditorWindow : Window
{
    private enum Tool { Pen, Rect, Arrow, Crop }

    private readonly string input;
    private readonly Stack<SKBitmap> undo = new();
    private SKBitmap bmp;
    private Tool tool;
    private readonly Image image = new() { Stretch = Stretch.Fill };
    private readonly Canvas overlay = new() { Background = Brushes.Transparent };
    private readonly TextBlock status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), Opacity = 0.75 };
    private readonly Button applyCrop;
    private Polyline? stroke;
    private Rectangle? shape;
    private Line? arrow;
    private Point start;
    private bool dragging;
    private PixelRect? cropRect;

    private static readonly SKColor MarkerColor = new(0xFF, 0xE0, 0x3B, 0x99), RedColor = new(0xFF, 0x3B, 0x30);

    public ImageEditorWindow(string path, bool cropMode)
    {
        input = path;
        bmp = ImageTools.Load(path);
        tool = cropMode ? Tool.Crop : Tool.Pen;
        Title = "GKR Studio — " + System.IO.Path.GetFileName(path);
        Icon = Assets.Icon();
        Width = 1100; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Button B(string text, Action click) { var b = new Button { Content = L.T(text) }; b.Click += (_, _) => click(); return b; }
        var bar = new WrapPanel { Margin = new Thickness(12, 10, 12, 6) };
        foreach (var (t, label) in new[] { (Tool.Pen, "🖍 Kalem"), (Tool.Rect, "▭ Dikdörtgen"), (Tool.Arrow, "➜ Ok"), (Tool.Crop, "✂ Kırpma alanı") })
            bar.Children.Add(B(label, () => { tool = t; ClearOverlay(); }));
        applyCrop = B("Kırp", ApplyCrop);
        applyCrop.IsEnabled = false;
        bar.Children.Add(applyCrop);
        bar.Children.Add(B("⟲ Sola", () => Commit(ImageTools.Rotate(bmp, 270))));
        bar.Children.Add(B("⟳ Sağa", () => Commit(ImageTools.Rotate(bmp, 90))));
        bar.Children.Add(B("⇋ Yansıt", () => Commit(ImageTools.Flip(bmp))));
        bar.Children.Add(B("↶ Geri al", Undo));
        var save = B("💾 Kaydet", Save);
        save.Classes.Add("accent");
        bar.Children.Add(save);
        bar.Children.Add(status);
        foreach (var c in bar.Children) c.Margin = new Thickness(0, 0, 6, 6);

        var stack = new Grid();
        stack.Children.Add(image);
        stack.Children.Add(overlay);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Viewbox { Child = stack, Margin = new Thickness(12), Stretch = Stretch.Uniform });
        Content = root;

        overlay.PointerPressed += OnDown;
        overlay.PointerMoved += OnMove;
        overlay.PointerReleased += OnUp;
        KeyDown += (_, e) =>
        {
            bool cmd = e.KeyModifiers.HasFlag(KeyModifiers.Meta) || e.KeyModifiers.HasFlag(KeyModifiers.Control);
            if (cmd && e.Key == Key.Z) Undo();
            if (cmd && e.Key == Key.S) Save();
            if (e.Key == Key.Enter && cropRect != null) ApplyCrop();
        };
        ShowBitmap();
    }

    private float Thick(double factor) => (float)Math.Max(3, Math.Min(bmp.Width, bmp.Height) * factor);

    private void ShowBitmap()
    {
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        image.Source = new Bitmap(new MemoryStream(data.ToArray()));
        image.Width = overlay.Width = bmp.Width;
        image.Height = overlay.Height = bmp.Height;
        status.Text = $"{bmp.Width} × {bmp.Height}";
    }

    private Point Pos(PointerEventArgs e)
    {
        var p = e.GetPosition(overlay);
        return new Point(Math.Clamp(p.X, 0, bmp.Width), Math.Clamp(p.Y, 0, bmp.Height));
    }

    private void OnDown(object? sender, PointerPressedEventArgs e)
    {
        ClearOverlay();
        start = Pos(e);
        dragging = true;
        switch (tool)
        {
            case Tool.Pen:
                stroke = new Polyline { Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xE0, 0x3B)), StrokeThickness = Thick(1 / 40.0), StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round, Points = [start] };
                overlay.Children.Add(stroke);
                break;
            case Tool.Arrow:
                arrow = new Line { StartPoint = start, EndPoint = start, Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), StrokeThickness = Thick(1 / 150.0) };
                overlay.Children.Add(arrow);
                break;
            default:
                shape = tool == Tool.Rect
                    ? new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)), StrokeThickness = Thick(1 / 200.0) }
                    : new Rectangle { Stroke = Brushes.White, StrokeThickness = Thick(1 / 400.0), StrokeDashArray = [4, 3], Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x5B, 0x8C, 0xFF)) };
                overlay.Children.Add(shape);
                Place(shape, start, start);
                break;
        }
    }

    private void OnMove(object? sender, PointerEventArgs e)
    {
        if (!dragging) return;
        var p = Pos(e);
        if (stroke != null) stroke.Points = [.. stroke.Points, p];
        else if (arrow != null) arrow.EndPoint = p;
        else if (shape != null) Place(shape, start, p);
    }

    private void OnUp(object? sender, PointerReleasedEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        var end = Pos(e);
        switch (tool)
        {
            case Tool.Pen when stroke is { Points.Count: > 0 }:
            {
                var pts = stroke.Points.ToList();
                Commit(Draw(c =>
                {
                    using var paint = new SKPaint { Color = MarkerColor, StrokeWidth = Thick(1 / 40.0), Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round, IsAntialias = true };
                    using var path = new SKPath();
                    path.MoveTo((float)pts[0].X, (float)pts[0].Y);
                    foreach (var q in pts.Skip(1)) path.LineTo((float)q.X, (float)q.Y);
                    if (pts.Count == 1) path.LineTo((float)pts[0].X + 0.1f, (float)pts[0].Y);
                    c.DrawPath(path, paint);
                }));
                break;
            }
            case Tool.Rect when Math.Abs(end.X - start.X) > 2 && Math.Abs(end.Y - start.Y) > 2:
                Commit(Draw(c =>
                {
                    using var paint = new SKPaint { Color = RedColor, StrokeWidth = Thick(1 / 200.0), Style = SKPaintStyle.Stroke, IsAntialias = true };
                    c.DrawRect(SKRect.Create((float)Math.Min(start.X, end.X), (float)Math.Min(start.Y, end.Y), (float)Math.Abs(end.X - start.X), (float)Math.Abs(end.Y - start.Y)), paint);
                }));
                break;
            case Tool.Arrow when Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2)) > 4:
                Commit(Draw(c =>
                {
                    float t = Thick(1 / 150.0);
                    var s = new SKPoint((float)start.X, (float)start.Y);
                    var tip = new SKPoint((float)end.X, (float)end.Y);
                    var dir = tip - s;
                    float len = dir.Length;
                    var u = new SKPoint(dir.X / len, dir.Y / len);
                    var n = new SKPoint(-u.Y, u.X);
                    float head = t * 4.5f;
                    var b = tip - new SKPoint(u.X * head, u.Y * head);
                    using var line = new SKPaint { Color = RedColor, StrokeWidth = t, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, IsAntialias = true };
                    c.DrawLine(s, b, line);
                    using var fill = new SKPaint { Color = RedColor, Style = SKPaintStyle.Fill, IsAntialias = true };
                    using var tri = new SKPath();
                    tri.MoveTo(tip);
                    tri.LineTo(b + new SKPoint(n.X * head * 0.55f, n.Y * head * 0.55f));
                    tri.LineTo(b - new SKPoint(n.X * head * 0.55f, n.Y * head * 0.55f));
                    tri.Close();
                    c.DrawPath(tri, fill);
                }));
                break;
            case Tool.Crop:
            {
                double x = Math.Min(start.X, end.X), y = Math.Min(start.Y, end.Y), w = Math.Abs(end.X - start.X), h = Math.Abs(end.Y - start.Y);
                cropRect = w >= 4 && h >= 4 ? new PixelRect((int)x, (int)y, (int)w, (int)h) : null;
                applyCrop.IsEnabled = cropRect != null;
                if (cropRect is { } r) status.Text = L.F("Seçim: {0} × {1}  —  Kırp'a basın (Enter)", r.Width, r.Height);
                return;
            }
        }
        ClearOverlay();
    }

    private static void Place(Rectangle r, Point a, Point b)
    {
        Canvas.SetLeft(r, Math.Min(a.X, b.X));
        Canvas.SetTop(r, Math.Min(a.Y, b.Y));
        r.Width = Math.Abs(a.X - b.X);
        r.Height = Math.Abs(a.Y - b.Y);
    }

    private void ClearOverlay()
    {
        overlay.Children.Clear();
        stroke = null; shape = null; arrow = null; cropRect = null;
        applyCrop.IsEnabled = false;
    }

    private SKBitmap Draw(Action<SKCanvas> draw)
    {
        var copy = bmp.Copy();
        using var canvas = new SKCanvas(copy);
        draw(canvas);
        return copy;
    }

    private void ApplyCrop()
    {
        if (cropRect is not { } r) return;
        var rect = SKRectI.Intersect(new SKRectI(r.X, r.Y, r.Right, r.Bottom), SKRectI.Create(bmp.Width, bmp.Height));
        var dst = new SKBitmap(rect.Width, rect.Height, bmp.ColorType, bmp.AlphaType);
        using (var canvas = new SKCanvas(dst)) canvas.DrawBitmap(bmp, rect, SKRect.Create(rect.Width, rect.Height));
        Commit(dst);
        ClearOverlay();
    }

    private void Commit(SKBitmap next)
    {
        undo.Push(bmp);
        bmp = next;
        ShowBitmap();
    }

    private void Undo()
    {
        if (undo.Count == 0) return;
        bmp.Dispose();
        bmp = undo.Pop();
        ClearOverlay();
        ShowBitmap();
    }

    private async void Save()
    {
        try
        {
            string ext = ImageTools.SameFormat(input);
            string output = OutputPaths.Beside(input, ext, "(düzenlendi)");
            ImageTools.Save(bmp, ext, output);
            status.Text = L.T("Kaydedildi: ") + System.IO.Path.GetFileName(output);
            if (await Ask.YesNo(L.F("Kaydedildi:\n{0}\n\nKlasörde gösterilsin mi?", output))) AppHost.Open(output, reveal: true);
        }
        catch (Exception ex) { await Ask.Info(ex.Message); }
    }
}
