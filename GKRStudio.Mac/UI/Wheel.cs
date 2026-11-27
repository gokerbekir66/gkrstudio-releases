using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using GKRStudio.Core;
using Path = Avalonia.Controls.Shapes.Path;

namespace GKRStudio.Mac;

/// <summary>The radial wheel (Avalonia port of the Windows version): ring segments, labels with icons, hub, badge, fire.</summary>
public sealed class WheelVisual : Canvas
{
    public const double Size = 560;
    private const double C = Size / 2, Halo = 262, Outer = 248, Inner = 98, Hub = 86;

    private readonly IBrush segmentBrush, segmentHot, gapBrush, hubBrush, hubHot, textBrush, mutedBrush;
    private readonly List<Path> segments = [];
    private readonly List<TextBlock[]> labels = [];
    private readonly Ellipse hub;
    private readonly int count;
    private readonly double sweep;

    public IReadOnlyList<MenuAction> Actions { get; }
    public bool[] Disabled { get; }

    public WheelVisual(string title, string? subtitle, int badge, IReadOnlyList<MenuAction> actions, bool[] disabled, Preferences prefs)
    {
        var t = AppTheme.Wheel(prefs);
        accent = t.Accent;
        segmentBrush = new SolidColorBrush(t.Segment);
        segmentHot = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(AppTheme.Lighten(t.Accent, 0.08), 0), new GradientStop(AppTheme.Darken(t.Accent, 0.22), 1) },
        };
        gapBrush = new SolidColorBrush(t.Gap);
        hubBrush = new SolidColorBrush(t.Hub);
        hubHot = new SolidColorBrush(t.HubHot);
        textBrush = new SolidColorBrush(t.Text);
        mutedBrush = new SolidColorBrush(t.Muted);
        var rim = new SolidColorBrush(t.Rim);

        Actions = actions;
        Disabled = disabled;
        count = actions.Count;
        sweep = count == 0 ? 360 : 360.0 / count;
        Width = Height = Size;
        Background = Brushes.Transparent;

        Children.Add(Circle(Halo, new SolidColorBrush(t.Halo), rim, 1.2, new DropShadowEffect { BlurRadius = 36, OffsetX = 0, OffsetY = 0, Opacity = 0.55, Color = Colors.Black }));
        for (int i = 0; i < Math.Max(count, 1); i++)
        {
            var path = new Path
            {
                Data = count <= 1 ? Ring() : Sector(i),
                Fill = segmentBrush, Stroke = gapBrush, StrokeThickness = count <= 1 ? 0 : 7,
                Opacity = count == 0 || disabled[i] ? 0.45 : 1,
            };
            segments.Add(path);
            Children.Add(path);
        }

        for (int i = 0; i < count; i++)
        {
            double mid = Start + sweep * (i + 0.5);
            double labelWidth = Math.Min(130, 2 * Math.PI * ((Inner + Outer) / 2) / Math.Max(count, 1) - 14);
            var stack = new StackPanel { Width = labelWidth, IsHitTestVisible = false };
            var text = new TextBlock
            {
                Text = actions[i].Label, Foreground = textBrush, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
                FontSize = (count > 9 ? 13 : count > 7 ? 15 : 17) + (actions[i].Label.Length <= 5 ? 2 : 0),
            };
            stack.Children.Add(text);
            var parts = new List<TextBlock> { text };
            if (prefs.WheelIcons)
            {
                var icon = new TextBlock { Text = AppTheme.Icon(actions[i]), FontSize = count > 9 ? 17 : count > 7 ? 20 : 23, Foreground = textBrush, Opacity = 0.85, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 0) };
                stack.Children.Add(icon);
                parts.Add(icon);
            }
            if (disabled[i])
                stack.Children.Add(new TextBlock { Text = L.T(actions[i].NeedsYtDlp && Tools.YtDlp == null ? "yt-dlp yok" : "ffmpeg yok"), Foreground = mutedBrush, FontSize = 11, TextAlignment = TextAlignment.Center });
            labels.Add([.. parts]);
            stack.Measure(new Avalonia.Size(labelWidth, double.PositiveInfinity));
            var p = Polar((Inner + Outer) / 2, mid);
            SetLeft(stack, p.X - labelWidth / 2);
            SetTop(stack, p.Y - stack.DesiredSize.Height / 2);
            Children.Add(stack);
        }

        hub = Circle(Hub, hubBrush, rim, 1, null);
        Children.Add(hub);

        var center = new StackPanel { Width = 150, IsHitTestVisible = false };
        center.Children.Add(new TextBlock { Text = title, Foreground = textBrush, FontSize = 17, FontWeight = FontWeight.SemiBold, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrEmpty(subtitle))
            center.Children.Add(new TextBlock { Text = subtitle, Foreground = mutedBrush, FontSize = 11, Margin = new Thickness(0, 3, 0, 0), TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxHeight = 46 });
        center.Measure(new Avalonia.Size(150, double.PositiveInfinity));
        SetLeft(center, C - 75);
        SetTop(center, C - center.DesiredSize.Height / 2);
        Children.Add(center);

        if (badge > 0)
        {
            var g = new Grid { Width = 30, Height = 30, IsHitTestVisible = false };
            g.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.Parse("#E53935")), Stroke = gapBrush, StrokeThickness = 2.5 });
            g.Children.Add(new TextBlock { Text = badge > 99 ? "99+" : badge.ToString(), Foreground = Brushes.White, FontWeight = FontWeight.Bold, FontSize = badge > 9 ? 11 : 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
            SetLeft(g, C + 22);
            SetTop(g, C + 26);
            Children.Add(g);
        }
    }

    private double Start => -90 - sweep / 2;

    public int HitIndex(Point p)
    {
        double dx = p.X - C, dy = p.Y - C, d = Math.Sqrt(dx * dx + dy * dy);
        if (d <= Hub) return -1;
        if (d > Halo || count == 0) return -2;
        double angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        double rel = ((angle - Start) % 360 + 720) % 360;
        return Math.Min(count - 1, (int)(rel / sweep));
    }

    public void Highlight(int index)
    {
        for (int i = 0; i < segments.Count; i++)
        {
            bool hot = i == index && count > 0 && !Disabled[i];
            segments[i].Fill = hot ? segmentHot : segmentBrush;
            if (i < labels.Count) foreach (var tb in labels[i]) tb.Foreground = hot ? Brushes.White : textBrush;
        }
        hub.Fill = index == -1 ? hubHot : hubBrush;
    }

    // ---------- fire ----------

    private static readonly Geometry FlameShape = Geometry.Parse(
        "M 0,0 C -10,0 -12,-10 -7,-18 C -4,-23 -5,-28 -2,-34 C 1,-27 6,-24 9,-17 C 12,-10 10,0 0,0 Z");
    private static readonly Geometry FlakeShape = Geometry.Parse("M -4,-3 L 3,-4 L 5,2 L -1,4 L -5,1 Z");

    private static IBrush VerticalFire(params (byte A, byte R, byte G, byte B, double At)[] stops)
    {
        var b = new LinearGradientBrush { StartPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative) };
        foreach (var s in stops) b.GradientStops.Add(new GradientStop(Color.FromArgb(s.A, s.R, s.G, s.B), s.At));
        return b;
    }

    private static readonly IBrush FlakeBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x24, 0x21));
    private static readonly IBrush SmokeBrush = new SolidColorBrush(Color.FromRgb(0x5E, 0x5A, 0x58));
    private static readonly IBrush CharBrush = new SolidColorBrush(Color.FromRgb(0x1A, 0x11, 0x0D));

    /// <summary>The wheel's accent colour; the fire takes its hue from it.</summary>
    private Color accent;

    private sealed record FirePalette(IBrush Outer, IBrush Core, IBrush Spark, IBrush FlakeEdge, IBrush EdgeOuter, IBrush EdgeInner, IBrush Heat, Color Glow);

    /// <summary>
    /// Fire colours in the accent's hue: a white-hot core, a bright body and a dark, fading tip (the hue drifts a little
    /// towards the tip like real flames). With the orange accent this gives classic fire; blue gives a gas flame, and so on.
    /// </summary>
    private static FirePalette MakeFirePalette(Color accent)
    {
        var (h, s, _) = ToHsl(accent);
        s = Math.Max(s, 0.8);
        var outer = new LinearGradientBrush { StartPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative) };
        outer.GradientStops.Add(new GradientStop(Hsl(h + 20, 1, 0.67), 0));
        outer.GradientStops.Add(new GradientStop(Hsl(h + 4, s, 0.55, 250), 0.3));
        outer.GradientStops.Add(new GradientStop(Hsl(h - 12, s, 0.48, 220), 0.7));
        outer.GradientStops.Add(new GradientStop(Hsl(h - 16, s, 0.33, 0), 1));
        var core = new LinearGradientBrush { StartPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative) };
        core.GradientStops.Add(new GradientStop(Hsl(h + 26, 1, 0.94), 0));
        core.GradientStops.Add(new GradientStop(Hsl(h + 20, 1, 0.67, 235), 0.5));
        core.GradientStops.Add(new GradientStop(Hsl(h + 4, 1, 0.56, 0), 1));
        var heat = new RadialGradientBrush();
        heat.GradientStops.Add(new GradientStop(Hsl(h + 16, s, 0.66), 0));
        heat.GradientStops.Add(new GradientStop(Hsl(h - 6, s, 0.5), 1));
        return new FirePalette(outer, core,
            new SolidColorBrush(Hsl(h + 20, 1, 0.72)),
            new SolidColorBrush(Hsl(h, s, 0.55)),
            new SolidColorBrush(Hsl(h - 4, s, 0.53)),
            new SolidColorBrush(Hsl(h + 24, 1, 0.77)),
            heat,
            Hsl(h - 4, 1, 0.5));
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
        if (d < 1e-6) return (0, 0, l);
        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60, s, l);
    }

    private static Color Hsl(double h, double s, double l, byte alpha = 255)
    {
        h = ((h % 360) + 360) % 360 / 360;
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
        double Channel(double t)
        {
            t = (t % 1 + 1) % 1;
            return t < 1.0 / 6 ? p + (q - p) * 6 * t : t < 0.5 ? q : t < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - t) * 6 : p;
        }
        static byte B(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
        return Color.FromArgb(alpha, B(Channel(h + 1.0 / 3)), B(Channel(h)), B(Channel(h - 1.0 / 3)));
    }

    private static readonly RelativePoint TopLeft = new(0, 0, RelativeUnit.Absolute);

    private sealed record Particle(Control Shape, ScaleTransform Scale, RotateTransform Turn, TranslateTransform Move, int Kind,
                                   double Angle, double Spawn, double Life, double Rise, double Drift, double Size, double Phase, double Spin);

    /// <summary>
    /// Burns segment <paramref name="index"/> like paper: a glowing edge eats it from the rim inward, big merged flames
    /// ride on that edge, the burnt part chars and falls away, ash flakes and sparks drift up, then the wheel fades out.
    /// Calls <paramref name="done"/> when finished.
    /// </summary>
    public void PlayFire(int index, Action done)
    {
        var pal = MakeFirePalette(accent);
        const double BurnFrom = 60, BurnTo = 700, FadeAt = 760, FadeMs = 180, CharWidth = 13;
        double a0 = Start + sweep * index, a1 = a0 + sweep;
        var rnd = new Random();
        double seed = rnd.NextDouble() * 100;
        int steps = Math.Max(16, (int)(sweep * 1.2));

        // Radius of the burning edge at angle a (degrees, inside the segment) and time t (ms): moves rim → hub with a ragged line.
        double Front(double a, double t)
        {
            double k = Math.Clamp((t - BurnFrom) / (BurnTo - BurnFrom), 0, 1);
            k = k < 0.5 ? 2 * k * k : 1 - Math.Pow(-2 * k + 2, 2) / 2;
            double r = Outer + 10 - (Outer - Inner + 34) * k;
            double n = 0.55 * Math.Sin(a * 0.31 + seed + t * 0.006) + 0.3 * Math.Sin(a * 0.83 + seed * 2.1 - t * 0.0045) + 0.15 * Math.Sin(a * 2.2 + t * 0.011);
            return r + 9 * n;
        }

        var heat = new Path { Data = segments[index].Data, Fill = pal.Heat, Opacity = 0, IsHitTestVisible = false };
        var hole = new Path { Fill = gapBrush, IsHitTestVisible = false };
        var charred = new Path { Fill = CharBrush, IsHitTestVisible = false };
        var edgeGlow = new Path { Stroke = pal.EdgeOuter, StrokeThickness = 4.5, StrokeJoin = PenLineJoin.Round, IsHitTestVisible = false,
                                  Effect = new DropShadowEffect { Color = pal.Glow, BlurRadius = 14, OffsetX = 0, OffsetY = 0, Opacity = 1 } };
        var edgeCore = new Path { Stroke = pal.EdgeInner, StrokeThickness = 1.6, StrokeJoin = PenLineJoin.Round, IsHitTestVisible = false };
        var flameBlur = new Canvas { IsHitTestVisible = false, Effect = new BlurEffect { Radius = 2.2 } };
        var flames = new Canvas { IsHitTestVisible = false, Effect = new DropShadowEffect { Color = pal.Glow, BlurRadius = 26, OffsetX = 0, OffsetY = 0, Opacity = 0.95 } };
        flames.Children.Add(flameBlur);
        var air = new Canvas { IsHitTestVisible = false };
        int hubAt = Children.IndexOf(hub);
        foreach (var layer in new Control[] { flames, edgeCore, edgeGlow, charred, hole, heat })
            Children.Insert(hubAt, layer);   // ends up heat < hole < char < edge < flames, all below the hub
        Children.Add(air);

        var parts = new List<Particle>();
        void Make(int kind, Canvas host, double spawn, double life, double rise, double drift, double size)
        {
            Control shape;
            if (kind == 0)
            {
                var c = new Canvas();
                c.Children.Add(new Path { Data = FlameShape, Fill = pal.Outer });
                c.Children.Add(new Path { Data = FlameShape, Fill = pal.Core, RenderTransform = new ScaleTransform(0.5, 0.55), RenderTransformOrigin = TopLeft });
                shape = c;
            }
            else if (kind == 3) shape = new Path { Data = FlakeShape, Fill = FlakeBrush, Stroke = pal.FlakeEdge, StrokeThickness = 0.9 };
            else
            {
                double d = kind == 1 ? 2.5 + rnd.NextDouble() * 2.5 : 16;
                var e = new Ellipse { Width = d, Height = d, Fill = kind == 1 ? pal.Spark : SmokeBrush };
                Canvas.SetLeft(e, -d / 2);
                Canvas.SetTop(e, -d / 2);
                var holder = new Canvas();
                holder.Children.Add(e);
                shape = holder;
            }
            var scale = new ScaleTransform(0, 0);
            var turn = new RotateTransform();
            var move = new TranslateTransform();
            shape.RenderTransform = new TransformGroup { Children = { scale, turn, move } };
            shape.RenderTransformOrigin = TopLeft;
            shape.Opacity = 0;
            host.Children.Add(shape);
            double pad = Math.Min(3, sweep / 8);
            parts.Add(new Particle(shape, scale, turn, move, kind, a0 + pad + rnd.NextDouble() * (sweep - 2 * pad),
                                   spawn, life, rise, drift, size, rnd.NextDouble() * Math.PI * 2, (rnd.NextDouble() - 0.5) * 720));
        }

        int flameCount = Math.Clamp((int)(sweep * 1.3), 26, 70);
        for (int i = 0; i < flameCount; i++) Make(0, flameBlur, BurnFrom + rnd.NextDouble() * (BurnTo - BurnFrom - 60), 260 + rnd.NextDouble() * 200, 14 + rnd.NextDouble() * 26, 2 + rnd.NextDouble() * 3, 1.3 + rnd.NextDouble() * 1.1);
        for (int i = 0; i < 16; i++) Make(1, air, 120 + rnd.NextDouble() * 560, 380 + rnd.NextDouble() * 280, 80 + rnd.NextDouble() * 90, (rnd.NextDouble() - 0.5) * 60, 1);
        for (int i = 0; i < 14; i++) Make(3, air, 150 + rnd.NextDouble() * 560, 500 + rnd.NextDouble() * 300, 50 + rnd.NextDouble() * 70, (rnd.NextDouble() - 0.5) * 50, 0.6 + rnd.NextDouble() * 0.5);
        for (int i = 0; i < 7; i++) Make(2, air, 300 + rnd.NextDouble() * 380, 520 + rnd.NextDouble() * 200, 50 + rnd.NextDouble() * 40, (rnd.NextDouble() - 0.5) * 30, 1);

        double labelRadius = (Inner + Outer) / 2;
        TextBlock[] texts = index < labels.Count ? labels[index] : [];
        const double Total = FadeAt + FadeMs;
        Tween.Run(Total, f =>
        {
            double t = f * Total;
            heat.Opacity = 0.45 * Math.Clamp(t / 140, 0, 1);

            // burnt-away part (hole), char band and the glowing edge, rebuilt every frame
            var edge = new Point[steps + 1];
            var charIn = new Point[steps + 1];
            for (int i = 0; i <= steps; i++)
            {
                double a = a0 + sweep * i / steps, r = Front(a, t);
                edge[i] = Polar(Math.Clamp(r, Inner - 3, Outer + 6), a);
                charIn[i] = Polar(Math.Clamp(r + CharWidth, Inner - 3, Outer + 6), a);
            }
            hole.Data = Band(charIn, Outer + 6, a0, a1);
            charred.Data = Strip(edge, charIn);
            var line = Line(edge);
            edgeGlow.Data = line;
            edgeCore.Data = line;
            double edgeAlive = Math.Clamp((BurnTo + 40 - t) / 80, 0, 1) * Math.Clamp((t - 30) / 60, 0, 1);
            edgeGlow.Opacity = edgeCore.Opacity = edgeAlive;

            double midFront = Front(a0 + sweep / 2, t);
            double gone = labelRadius + 40 - midFront;
            foreach (var tb in texts) tb.Opacity = gone <= 0 ? 1 : Math.Clamp(1 - gone / 50, 0, 1);

            foreach (var p in parts)
            {
                double u = (t - p.Spawn) / p.Life;
                if (u <= 0 || u >= 1) { p.Shape.Opacity = 0; continue; }
                double bell = Math.Sin(Math.PI * u);
                double fr = Math.Clamp(Front(p.Angle, p.Kind == 0 ? t : p.Spawn), Inner, Outer);
                var basePt = Polar(fr, p.Angle);
                switch (p.Kind)
                {
                    case 0: // big flame riding on the burning edge
                        double s = p.Size * (0.35 + 0.8 * Math.Pow(bell, 0.6)) * (1 + 0.14 * Math.Sin(t / 26 + p.Phase));
                        s = Math.Min(s, Math.Max(0.3, (basePt.Y - 4) / 36));
                        p.Scale.ScaleX = s * (0.85 + 0.15 * Math.Sin(t / 17 + p.Phase));
                        p.Scale.ScaleY = s;
                        p.Move.X = basePt.X + p.Drift * Math.Sin(p.Phase + u * 8);
                        p.Move.Y = basePt.Y - Math.Min(p.Rise * u, Math.Max(0, basePt.Y - 36 * s - 2));
                        p.Shape.Opacity = Math.Min(1, bell * 1.7) * edgeAlive;
                        break;
                    case 1: // spark
                        p.Scale.ScaleX = p.Scale.ScaleY = 1 - 0.6 * u;
                        p.Move.X = basePt.X + p.Drift * u + 4 * Math.Sin(p.Phase + u * 12);
                        p.Move.Y = basePt.Y - p.Rise * u;
                        p.Shape.Opacity = 1 - u;
                        break;
                    case 3: // ash flake: tumbles up, its glowing rim dies out
                        p.Scale.ScaleX = p.Scale.ScaleY = p.Size;
                        p.Turn.Angle = p.Spin * u;
                        p.Move.X = basePt.X + p.Drift * u + 6 * Math.Sin(p.Phase + u * 6);
                        p.Move.Y = basePt.Y - p.Rise * u;
                        p.Shape.Opacity = Math.Min(1, 3 * (1 - u));
                        break;
                    default: // smoke
                        p.Scale.ScaleX = p.Scale.ScaleY = 1 + 1.8 * u;
                        p.Move.X = basePt.X + p.Drift * u;
                        p.Move.Y = basePt.Y - 10 - Math.Min(p.Rise * u, Math.Max(0, basePt.Y - 50));
                        p.Shape.Opacity = 0.24 * bell;
                        break;
                }
            }
            Opacity = t < FadeAt ? 1 : Math.Max(0, 1 - (t - FadeAt) / FadeMs);
        }, done);
    }

    /// <summary>Area between the ragged line <paramref name="inner"/> and the outer arc at radius <paramref name="outer"/>.</summary>
    private static Geometry Band(Point[] inner, double outer, double a0, double a1)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(inner[0], true);
            for (int i = 1; i < inner.Length; i++) ctx.LineTo(inner[i]);
            ctx.LineTo(Polar(outer, a1));
            ctx.ArcTo(Polar(outer, a0), new Avalonia.Size(outer, outer), 0, a1 - a0 > 180, SweepDirection.CounterClockwise);
            ctx.EndFigure(true);
        }
        return g;
    }

    private static Geometry Strip(Point[] a, Point[] b)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(a[0], true);
            for (int i = 1; i < a.Length; i++) ctx.LineTo(a[i]);
            for (int i = b.Length - 1; i >= 0; i--) ctx.LineTo(b[i]);
            ctx.EndFigure(true);
        }
        return g;
    }

    private static Geometry Line(Point[] pts)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(pts[0], false);
            for (int i = 1; i < pts.Length; i++) ctx.LineTo(pts[i]);
            ctx.EndFigure(false);
        }
        return g;
    }

    private static Point Polar(double r, double deg)
    {
        double a = deg * Math.PI / 180;
        return new Point(C + r * Math.Cos(a), C + r * Math.Sin(a));
    }

    private Geometry Sector(int i)
    {
        double a0 = Start + sweep * i, a1 = a0 + sweep;
        bool large = sweep > 180;
        var fig = new PathFigure { StartPoint = Polar(Outer, a0), IsClosed = true, Segments = new PathSegments() };
        fig.Segments.Add(new ArcSegment { Point = Polar(Outer, a1), Size = new Avalonia.Size(Outer, Outer), IsLargeArc = large, SweepDirection = SweepDirection.Clockwise });
        fig.Segments.Add(new LineSegment { Point = Polar(Inner, a1) });
        fig.Segments.Add(new ArcSegment { Point = Polar(Inner, a0), Size = new Avalonia.Size(Inner, Inner), IsLargeArc = large, SweepDirection = SweepDirection.CounterClockwise });
        return new PathGeometry { Figures = new PathFigures { fig } };
    }

    private static Geometry Ring() => new CombinedGeometry(GeometryCombineMode.Exclude,
        new EllipseGeometry(new Rect(C - Outer, C - Outer, Outer * 2, Outer * 2)), new EllipseGeometry(new Rect(C - Inner, C - Inner, Inner * 2, Inner * 2)));

    private static Ellipse Circle(double r, IBrush fill, IBrush? stroke, double thickness, IEffect? effect)
    {
        var e = new Ellipse { Width = r * 2, Height = r * 2, Fill = fill, Stroke = stroke, StrokeThickness = thickness, Effect = effect };
        SetLeft(e, C - r);
        SetTop(e, C - r);
        return e;
    }
}

/// <summary>Transparent top-most window with the wheel centred on the cursor. Groups open sub-wheels; the hub goes back.</summary>
public sealed class WheelWindow : Window
{
    private readonly Grid root = new();
    private readonly TextBlock hint;
    private readonly Action<MenuAction> onChosen;
    private readonly bool bite;
    private readonly int badge;
    private readonly Preferences prefs;
    private readonly Stack<(string, string?, IReadOnlyList<MenuAction>)> history = new();
    private (string Title, string? Subtitle, IReadOnlyList<MenuAction> Actions) level;
    private WheelVisual wheel = null!;
    private bool done, biting, heldSeen;
    private ulong holdMask;
    private DateTime openedAt = DateTime.MaxValue;
    private DispatcherTimer? holdTimer;

    public bool Chosen { get; private set; }

    public WheelWindow(string title, string? subtitle, int badge, IReadOnlyList<MenuAction> actions, Action<MenuAction> onChosen)
    {
        prefs = Preferences.Load();
        this.onChosen = onChosen;
        this.badge = badge;
        bite = prefs.BiteAnimation;

        hint = new TextBlock
        {
            Text = L.T("Sol tıkla seç  ·  Esc kapat  ·  Sağ tık: Ayarlar"), Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false,
        };
        root.Children.Add(hint);
        ShowLevel((title, subtitle, actions), animate: false);

        Content = root;
        Title = "GKR Studio";
        SystemDecorations = SystemDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        Width = Height = WheelVisual.Size;
        Position = CursorTopLeft();

        PointerMoved += (_, e) => { if (!biting) wheel.Highlight(wheel.HitIndex(e.GetPosition(wheel))); };
        PointerExited += (_, _) => { if (!biting) wheel.Highlight(-2); };
        PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Right) { if (!biting) { Finish(); App.ShowSettings(); } return; }
            if (e.InitialPressMouseButton == MouseButton.Left) Choose(wheel.HitIndex(e.GetPosition(wheel)));
        };
        Deactivated += (_, _) =>
        {
            if (biting || holdMask != 0) return;
            // macOS: a window shown from the menu-bar app can lose focus right after opening while the app is being
            // activated (Finder was frontmost). Take focus back instead of closing during the first moment.
            if (DateTime.UtcNow - openedAt < TimeSpan.FromMilliseconds(800)) { Program.Log("wheel: early deactivate → reactivate"); Activate(); return; }
            Finish("deactivated");
        };
        KeyDown += OnKeyDown;
        Opened += (_, _) =>
        {
            openedAt = DateTime.UtcNow;
            if (OperatingSystem.IsMacOS() && Screens.ScreenFromWindow(this) == null)
            {
                // off every screen: the position was scaled twice (points vs. pixels) → place it in points
                var c = MacInput.Cursor();
                int half = (int)(WheelVisual.Size / 2);
                Position = new PixelPoint(Math.Max(0, (int)c.X - half), Math.Max(0, (int)c.Y - half));
                Program.Log($"wheel: was off-screen, moved to {Position}");
            }
            Program.Log($"wheel opened: pos={Position} size={Bounds.Width}x{Bounds.Height} scaling={RenderScaling} screens={string.Join(";", Screens.All.Select(s => $"{s.Bounds}@{s.Scaling}"))}");
            Activate(); Focus(); AnimateIn();
        };
        Closed += (_, _) => Program.Log("wheel closed");
    }

    /// <summary>Keep the wheel open exactly while the chord's modifier keys are held (Finder modifier + right-click).</summary>
    public void HoldWhilePressed(string chord)
    {
        foreach (var part in chord.Split('+'))
            holdMask |= part switch
            {
                "Shift" => MacInput.FlagShift, "Ctrl" => MacInput.FlagControl, "Alt" => MacInput.FlagOption, "Cmd" => MacInput.FlagCommand,
                "F1" => Resident.FlagF1, "F2" => Resident.FlagF2, "F3" => Resident.FlagF3, _ => 0,
            };
        if (holdMask == 0 || !OperatingSystem.IsMacOS()) return;
        hint.Text = L.F("Sol tıkla seç  ·  {0} bırakılınca kapanır", Settings.ModifierName(chord));
        var started = DateTime.UtcNow;
        holdTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Input, (_, _) =>
        {
            if (done || biting) { holdTimer?.Stop(); return; }
            bool held = (Resident.HeldFlags() & holdMask) != 0;
            if (held) { heldSeen = true; return; }
            if (heldSeen) { holdTimer?.Stop(); Finish("modifier released"); return; }
            if (DateTime.UtcNow - started > TimeSpan.FromMilliseconds(400))
            {
                // key was already up when the wheel appeared: stay open until a click / Esc, like a plain wheel
                Program.Log("wheel: modifier not held → normal mode");
                holdTimer?.Stop();
                holdMask = 0;
                hint.Text = L.T("Sol tıkla seç  ·  Esc kapat  ·  Sağ tık: Ayarlar");
            }
        });
        holdTimer.Start();
    }

    private static bool IsDisabled(MenuAction a) => a.IsGroup ? a.Children!.All(IsDisabled)
        : (a.NeedsYtDlp && Tools.YtDlp == null) || (a.NeedsFfmpeg && Tools.Ffmpeg == null);

    private void ShowLevel((string Title, string? Subtitle, IReadOnlyList<MenuAction> Actions) next, bool animate)
    {
        level = next;
        var visual = new WheelVisual(next.Title, next.Subtitle, badge, next.Actions, next.Actions.Select(IsDisabled).ToArray(), prefs) { Opacity = 0 };
        if (wheel != null) root.Children.Remove(wheel);
        root.Children.Insert(0, visual);
        wheel = visual;
        if (animate) AnimateIn();
    }

    private void AnimateIn()
    {
        var scale = new ScaleTransform(0.86, 0.86);
        wheel.RenderTransform = scale;
        var w = wheel;
        Tween.Run(140, t => { double e = Tween.EaseOut(t); w.Opacity = e; scale.ScaleX = scale.ScaleY = 0.86 + 0.14 * e; });
    }

    private void Choose(int index)
    {
        if (biting) return;
        if (index == -1 && history.Count > 0) { ShowLevel(history.Pop(), true); return; }
        if (index < 0 || index >= wheel.Actions.Count) { Finish(); return; }
        if (wheel.Disabled[index]) return;
        var action = wheel.Actions[index];
        if (action.IsGroup)
        {
            history.Push(level);
            ShowLevel((action.Title, L.T("Geri için ortaya tıklayın"), action.Children!), true);
            return;
        }
        Chosen = true;
        if (!bite) { Finish(); onChosen(action); return; }
        biting = true;
        wheel.Highlight(index);
        wheel.PlayFire(index, () => { Finish(); onChosen(action); });
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (biting) return;
        if (e.Key == Key.Escape) { if (history.Count > 0) ShowLevel(history.Pop(), true); else Finish(); return; }
        if (e.Key == Key.Back && history.Count > 0) { ShowLevel(history.Pop(), true); return; }
        int n = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
        if (n >= 0 && n < wheel.Actions.Count) Choose(n);
    }

    private void Finish(string reason = "done")
    {
        if (done) return;
        done = true;
        Program.Log("wheel finish: " + reason);
        Dispatcher.UIThread.Post(Close);
    }

    /// <summary>Top-left position (pixels) that centres the window on the mouse, kept on screen.</summary>
    private PixelPoint CursorTopLeft()
    {
        double scale = Screens.Primary?.Scaling ?? 1;
        PixelPoint cursor;
        if (OperatingSystem.IsMacOS())
        {
            var p = MacInput.Cursor();
            cursor = new PixelPoint((int)(p.X * scale), (int)(p.Y * scale));
        }
        else if (OperatingSystem.IsWindows() && GetCursorPos(out var wp)) cursor = new PixelPoint(wp.X, wp.Y);
        else cursor = new PixelPoint(800, 500);
        var screen = Screens.ScreenFromPoint(cursor) ?? Screens.Primary;
        if (screen != null) scale = screen.Scaling;
        int size = (int)(WheelVisual.Size * scale);
        var area = screen?.WorkingArea ?? new PixelRect(0, 0, 4000, 4000);
        return new PixelPoint(Math.Clamp(cursor.X - size / 2, area.X, Math.Max(area.X, area.Right - size)),
                              Math.Clamp(cursor.Y - size / 2, area.Y, Math.Max(area.Y, area.Bottom - size)));
    }

    [StructLayout(LayoutKind.Sequential)] private struct WinPoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out WinPoint p);
}
