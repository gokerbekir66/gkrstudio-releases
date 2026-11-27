using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using GKRStudio.Core;

namespace GKRStudio.UI;

public abstract record Field(string Key, string Label);
/// <param name="EnabledWhen">"choiceKey=value": the box is only editable while that option is picked.</param>
public sealed record TextField(string Key, string Label, string Default, string? Hint = null, string? EnabledWhen = null) : Field(Key, Label);
public sealed record ChoiceField(string Key, string Label, string[] Options, int Selected = 0) : Field(Key, Label);

/// <summary>Small dark dialog that asks for a few parameters. Returns null when cancelled.</summary>
public sealed class ParamDialog : Window
{
    private readonly Dictionary<string, Func<string>> readers = [];
    private readonly Dictionary<string, List<RadioButton>> choices = [];

    private ParamDialog(string title, string? description, IReadOnlyList<Field> fields)
    {
        Style = (Style)Application.Current.FindResource("AppWindow");
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
        Title = "GKR Studio — " + L.T(title);
        Width = 420;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;

        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = L.T(title), Style = (Style)Application.Current.FindResource("Heading") });
        if (description != null)
            panel.Children.Add(new TextBlock { Text = L.T(description), Style = (Style)Application.Current.FindResource("MutedText"), Margin = new Thickness(0, 0, 0, 12) });

        Control? first = null;
        foreach (var field in fields)
        {
            var label = new TextBlock { Text = L.T(field.Label), Margin = new Thickness(0, 6, 0, 4) };
            panel.Children.Add(label);
            switch (field)
            {
                case TextField t:
                {
                    var box = new TextBox { Text = t.Default };
                    readers[t.Key] = () => box.Text.Trim();
                    panel.Children.Add(box);
                    TextBlock? hint = null;
                    if (t.Hint != null)
                        panel.Children.Add(hint = new TextBlock { Text = L.T(t.Hint), FontSize = 11, Style = (Style)Application.Current.FindResource("MutedText"), Margin = new Thickness(0, 3, 0, 0) });
                    if (t.EnabledWhen?.Split('=', 2) is [var key, var value] && choices.TryGetValue(key, out var group))
                    {
                        // shown only while that option is picked
                        var target = group.First(r => (string)r.Tag == value);
                        void Sync()
                        {
                            var v = target.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
                            label.Visibility = box.Visibility = v;
                            if (hint != null) hint.Visibility = v;
                        }
                        group.ForEach(r => { r.Checked += (_, _) => Sync(); });
                        target.Checked += (_, _) => Dispatcher.BeginInvoke(() => { box.Focus(); box.SelectAll(); });
                        Sync();
                    }
                    else first ??= box;
                    break;
                }
                case ChoiceField c:
                {
                    var wrap = new WrapPanel();
                    var radios = c.Options.Select((o, i) => new RadioButton
                    {
                        Content = L.T(o), Tag = o, IsChecked = i == c.Selected, GroupName = c.Key, Margin = new Thickness(0, 0, 14, 6),
                        Foreground = (Brush)Application.Current.FindResource("Fg"),
                    }).ToList();
                    radios.ForEach(r => wrap.Children.Add(r));
                    choices[c.Key] = radios;
                    readers[c.Key] = () => (string)radios.First(r => r.IsChecked == true).Tag; // internal value, not the translated text
                    panel.Children.Add(wrap);
                    first ??= radios[c.Selected];
                    break;
                }
            }
        }

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var ok = new Button { Content = L.T("Başlat"), IsDefault = true, Style = (Style)Application.Current.FindResource("Primary") };
        var cancel = new Button { Content = L.T("İptal"), IsCancel = true, Margin = new Thickness(0, 0, 0, 8) };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        Loaded += (_, _) => { Activate(); first?.Focus(); if (first is TextBox tb) tb.SelectAll(); };
    }

    public static Dictionary<string, string>? Ask(string title, string? description, params Field[] fields)
    {
        var dialog = new ParamDialog(title, description, fields);
        return dialog.ShowDialog() == true ? dialog.readers.ToDictionary(kv => kv.Key, kv => kv.Value()) : null;
    }
}

/// <summary>Shows a still frame and lets the user drag a rectangle; returns it in image pixel coordinates.</summary>
public sealed class RegionPickerWindow : Window
{
    private readonly Rectangle selection = new()
    {
        Stroke = Brushes.White, StrokeThickness = 2, StrokeDashArray = [4, 3],
        Fill = new SolidColorBrush(Color.FromArgb(0x40, 0x5B, 0x8C, 0xFF)), Visibility = Visibility.Collapsed,
    };
    private readonly Image image;
    private readonly Button ok;
    private Point start;
    private Int32Rect? result;

    private RegionPickerWindow(BitmapSource frame, string title, string hint)
    {
        Style = (Style)Application.Current.FindResource("AppWindow");
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
        Title = "GKR Studio — " + L.T(title);
        Width = 960; Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Topmost = true;

        image = new Image { Source = frame, Width = frame.PixelWidth, Height = frame.PixelHeight, Stretch = Stretch.Fill };
        var overlay = new Canvas { Width = frame.PixelWidth, Height = frame.PixelHeight, Background = Brushes.Transparent, Cursor = Cursors.Cross };
        overlay.Children.Add(selection);
        var stack = new Grid();
        stack.Children.Add(image);
        stack.Children.Add(overlay);

        overlay.MouseLeftButtonDown += (_, e) =>
        {
            start = Clamp(e.GetPosition(image), frame);
            overlay.CaptureMouse();
            Update(start, start);
            selection.Visibility = Visibility.Visible;
        };
        overlay.MouseMove += (_, e) => { if (overlay.IsMouseCaptured) Update(start, Clamp(e.GetPosition(image), frame)); };
        overlay.MouseLeftButtonUp += (_, _) => overlay.ReleaseMouseCapture();

        ok = new Button { Content = L.T("Uygula"), IsEnabled = false, Style = (Style)Application.Current.FindResource("Primary") };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = L.T("İptal"), IsCancel = true };

        var bar = new DockPanel { Margin = new Thickness(14, 10, 14, 4) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Right);
        bar.Children.Add(buttons);
        bar.Children.Add(new TextBlock { Text = L.T(hint), VerticalAlignment = VerticalAlignment.Center, Style = (Style)Application.Current.FindResource("MutedText") });

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(new Viewbox { Child = stack, Margin = new Thickness(14), Stretch = Stretch.Uniform });
        Content = root;
    }

    private static Point Clamp(Point p, BitmapSource f) => new(Math.Clamp(p.X, 0, f.PixelWidth), Math.Clamp(p.Y, 0, f.PixelHeight));

    private void Update(Point a, Point b)
    {
        double x = Math.Min(a.X, b.X), y = Math.Min(a.Y, b.Y), w = Math.Abs(a.X - b.X), h = Math.Abs(a.Y - b.Y);
        Canvas.SetLeft(selection, x);
        Canvas.SetTop(selection, y);
        selection.Width = w;
        selection.Height = h;
        result = new Int32Rect((int)x, (int)y, (int)w, (int)h);
        ok.IsEnabled = w >= 16 && h >= 16;
    }

    public static Int32Rect? Pick(BitmapSource frame, string title, string hint)
    {
        var w = new RegionPickerWindow(frame, title, hint);
        return w.ShowDialog() == true ? w.result : null;
    }
}
