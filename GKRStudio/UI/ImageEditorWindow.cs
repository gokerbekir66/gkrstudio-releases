using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GKRStudio.Core;

namespace GKRStudio.UI;

/// <summary>
/// Image editor: crop, freehand marker, rectangle, rotate, horizontal flip, undo. Every edit is flattened into
/// the bitmap immediately (simple, predictable undo). Saving writes a NEW file "(düzenlendi)" next to the original.
/// </summary>
public sealed class ImageEditorWindow : Window
{
    private enum Tool { Marker, Rect, Arrow, Text, Crop }

    private readonly string input;
    private readonly Stack<BitmapSource> undo = new();
    private BitmapSource bmp;
    private Tool tool = Tool.Marker;

    private readonly Image image = new() { Stretch = Stretch.Fill };
    private readonly Canvas overlay = new() { Background = Brushes.Transparent, Cursor = Cursors.Pen };
    private readonly TextBlock status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button applyCrop, undoButton;
    private readonly Dictionary<Tool, Button> toolButtons = [];

    private Polyline? stroke;
    private Line? arrow;
    private Rectangle? shape;
    private Point start;
    private Int32Rect? cropRect;

    private static readonly Color MarkerColor = Color.FromArgb(0x99, 0xFF, 0xE0, 0x3B);
    private static readonly Color RectColor = Color.FromRgb(0xFF, 0x3B, 0x30);

    public ImageEditorWindow(string path, bool cropMode = false)
    {
        input = path;
        bmp = ImageTools.Load(path);
        Style = (Style)Application.Current.FindResource("AppWindow");
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
        Title = "GKR Studio — " + System.IO.Path.GetFileName(path);
        Width = 1100; Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Button B(string text, Action click, bool primary = false)
        {
            var b = new Button { Content = L.T(text), Margin = new Thickness(0, 0, 6, 0) };
            if (primary) b.Style = (Style)Application.Current.FindResource("Primary");
            b.Click += (_, _) => click();
            return b;
        }

        var bar = new WrapPanel { Margin = new Thickness(12, 10, 12, 6) };
        foreach (var (t, label) in new[] { (Tool.Marker, "🖍 Kalem"), (Tool.Rect, "▭ Dikdörtgen"), (Tool.Arrow, "➜ Ok"), (Tool.Text, "T Metin"), (Tool.Crop, "✂ Kırpma alanı") })
        {
            var b = B(label, () => SetTool(t));
            toolButtons[t] = b;
            bar.Children.Add(b);
        }
        applyCrop = B("Kırp", ApplyCrop);
        applyCrop.IsEnabled = false;
        bar.Children.Add(applyCrop);
        bar.Children.Add(new Separator { Width = 12, Visibility = Visibility.Hidden });
        bar.Children.Add(B("⟲ Sola", () => Commit(new TransformedBitmap(bmp, new RotateTransform(270)))));
        bar.Children.Add(B("⟳ Sağa", () => Commit(new TransformedBitmap(bmp, new RotateTransform(90)))));
        bar.Children.Add(B("⇋ Yansıt", () => Commit(new TransformedBitmap(bmp, new ScaleTransform(-1, 1)))));
        undoButton = B("↶ Geri al", Undo);
        undoButton.IsEnabled = false;
        bar.Children.Add(undoButton);
        bar.Children.Add(new Separator { Width = 12, Visibility = Visibility.Hidden });
        bar.Children.Add(B("💾 Kaydet", Save, primary: true));
        bar.Children.Add(status);

        var stack = new Grid();
        stack.Children.Add(image);
        stack.Children.Add(overlay);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Viewbox { Child = stack, Margin = new Thickness(12), Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both });
        Content = root;

        overlay.MouseLeftButtonDown += OnDown;
        overlay.MouseMove += OnMove;
        overlay.MouseLeftButtonUp += OnUp;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) Undo();
            if (e.Key == Key.S && Keyboard.Modifiers == ModifierKeys.Control) Save();
            if (e.Key == Key.Enter && cropRect != null) ApplyCrop();
        };

        Show(bmp);
        SetTool(cropMode ? Tool.Crop : Tool.Marker);
    }

    private double Thickness(double factor) => Math.Max(3, Math.Min(bmp.PixelWidth, bmp.PixelHeight) * factor);

    private void SetTool(Tool t)
    {
        tool = t;
        ClearOverlay();
        foreach (var (k, b) in toolButtons) b.BorderBrush = k == t ? (Brush)FindResource("Accent") : (Brush)FindResource("Line");
        overlay.Cursor = t == Tool.Crop ? Cursors.Cross : Cursors.Pen;
    }

    private void Show(BitmapSource b)
    {
        image.Source = b;
        image.Width = overlay.Width = b.PixelWidth;
        image.Height = overlay.Height = b.PixelHeight;
        status.Text = $"{b.PixelWidth} × {b.PixelHeight}";
        status.Foreground = (Brush)FindResource("Muted");
    }

    private Point Pos(MouseEventArgs e)
    {
        var p = e.GetPosition(image);
        return new Point(Math.Clamp(p.X, 0, bmp.PixelWidth), Math.Clamp(p.Y, 0, bmp.PixelHeight));
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        ClearOverlay();
        start = Pos(e);
        overlay.CaptureMouse();
        switch (tool)
        {
            case Tool.Text:
                overlay.ReleaseMouseCapture();
                AddText(start);
                return;
            case Tool.Arrow:
                arrow = new Line { X1 = start.X, Y1 = start.Y, X2 = start.X, Y2 = start.Y, Stroke = new SolidColorBrush(RectColor), StrokeThickness = Thickness(1 / 150.0), StrokeEndLineCap = PenLineCap.Triangle };
                overlay.Children.Add(arrow);
                break;
            case Tool.Marker:
                stroke = new Polyline
                {
                    Stroke = new SolidColorBrush(MarkerColor), StrokeThickness = Thickness(1 / 40.0),
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                stroke.Points.Add(start);
                overlay.Children.Add(stroke);
                break;
            default:
                shape = tool == Tool.Rect
                    ? new Rectangle { Stroke = new SolidColorBrush(RectColor), StrokeThickness = Thickness(1 / 200.0) }
                    : new Rectangle { Stroke = Brushes.White, StrokeThickness = Thickness(1 / 400.0), StrokeDashArray = [4, 3], Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x5B, 0x8C, 0xFF)) };
                overlay.Children.Add(shape);
                Place(shape, start, start);
                break;
        }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (!overlay.IsMouseCaptured) return;
        var p = Pos(e);
        if (stroke != null) stroke.Points.Add(p);
        else if (arrow != null) { arrow.X2 = p.X; arrow.Y2 = p.Y; }
        else if (shape != null) Place(shape, start, p);
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (!overlay.IsMouseCaptured) return;
        overlay.ReleaseMouseCapture();
        var end = Pos(e);
        switch (tool)
        {
            case Tool.Marker when stroke is { Points.Count: > 0 }:
            {
                var geometry = new PathGeometry();
                var fig = new PathFigure { StartPoint = stroke.Points[0] };
                fig.Segments.Add(new PolyLineSegment(stroke.Points.Skip(1).DefaultIfEmpty(stroke.Points[0]), true));
                geometry.Figures.Add(fig);
                var pen = new Pen(stroke.Stroke, stroke.StrokeThickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                Commit(Render(dc => dc.DrawGeometry(null, pen, geometry)));
                break;
            }
            case Tool.Rect when shape != null:
            {
                var r = new Rect(start, end);
                if (r.Width > 2 && r.Height > 2)
                {
                    var pen = new Pen(shape.Stroke, shape.StrokeThickness) { LineJoin = PenLineJoin.Miter };
                    Commit(Render(dc => dc.DrawRectangle(null, pen, r)));
                }
                break;
            }
            case Tool.Arrow when arrow != null && (end - start).Length > 4:
            {
                double t = arrow.StrokeThickness;
                var dir = end - start; dir.Normalize();
                var normal = new Vector(-dir.Y, dir.X);
                double head = t * 4.5;
                var tip = end;
                var b = end - dir * head;
                var headGeo = new PathGeometry([new PathFigure(tip, [new LineSegment(b + normal * head * 0.55, true), new LineSegment(b - normal * head * 0.55, true)], true)]);
                var pen = new Pen(arrow.Stroke, t) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Flat };
                Commit(Render(dc => { dc.DrawLine(pen, start, b + dir * t * 0.5); dc.DrawGeometry(arrow.Stroke, null, headGeo); }));
                break;
            }
            case Tool.Crop:
            {
                var r = new Rect(start, end);
                cropRect = r.Width >= 4 && r.Height >= 4 ? new Int32Rect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height) : null;
                applyCrop.IsEnabled = cropRect != null;
                if (cropRect != null) status.Text = L.F("Seçim: {0} × {1}  —  Kırp'a basın (Enter)", cropRect.Value.Width, cropRect.Value.Height);
                return; // keep the selection visible
            }
        }
        ClearOverlay();
    }

    /// <summary>White text with a dark outline, readable on any background.</summary>
    private void AddText(Point at)
    {
        var values = ParamDialog.Ask("Metin ekle", null, new TextField("text", "Metin", ""));
        string text = values?.GetValueOrDefault("text") ?? "";
        if (text.Length == 0) return;
        double size = Math.Max(14, Math.Min(bmp.PixelWidth, bmp.PixelHeight) / 18.0);
        var ft = new FormattedText(text, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size, Brushes.White, 1.0);
        var geo = ft.BuildGeometry(at);
        var outline = new Pen(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)), size / 9) { LineJoin = PenLineJoin.Round };
        Commit(Render(dc => { dc.DrawGeometry(null, outline, geo); dc.DrawGeometry(Brushes.White, null, geo); }));
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
        stroke = null;
        shape = null;
        arrow = null;
        cropRect = null;
        applyCrop.IsEnabled = false;
    }

    private BitmapSource Render(Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(bmp, new Rect(0, 0, bmp.PixelWidth, bmp.PixelHeight));
            draw(dc);
        }
        var rtb = new RenderTargetBitmap(bmp.PixelWidth, bmp.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return rtb;
    }

    private void ApplyCrop()
    {
        if (cropRect is not { } r) return;
        r.Width = Math.Min(r.Width, bmp.PixelWidth - r.X);
        r.Height = Math.Min(r.Height, bmp.PixelHeight - r.Y);
        Commit(new CroppedBitmap(bmp, r));
        ClearOverlay();
    }

    private void Commit(BitmapSource next)
    {
        next.Freeze();
        undo.Push(bmp);
        bmp = next;
        undoButton.IsEnabled = true;
        Show(bmp);
    }

    private void Undo()
    {
        if (undo.Count == 0) return;
        bmp = undo.Pop();
        undoButton.IsEnabled = undo.Count > 0;
        ClearOverlay();
        Show(bmp);
    }

    private void Save()
    {
        try
        {
            string ext = ImageTools.SameFormat(input);
            string output = OutputPaths.Beside(input, ext, "(düzenlendi)");
            ImageTools.Save(bmp, ext, output);
            status.Text = L.T("Kaydedildi: ") + System.IO.Path.GetFileName(output);
            status.Foreground = (Brush)FindResource("Ok");
            if (MessageBox.Show(this, L.F("Kaydedildi:\n{0}\n\nKlasörde gösterilsin mi?", output), "GKR Studio", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", output } });
            undo.Clear();
            undoButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "GKR Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
