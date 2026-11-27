using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using GKRStudio.Core;

namespace GKRStudio.UI;

/// <summary>Cheat sheet opened by tapping Ctrl, Alt or Shift three times quickly. Reflects the user's own key settings.</summary>
public sealed class ShortcutsWindow : Window
{
    private static ShortcutsWindow? current;

    public static void Toggle()
    {
        if (current != null) { current.SafeClose(); return; }
        Theme.Apply(Preferences.Load());
        current = new ShortcutsWindow();
        current.Closed += (_, _) => { current = null; Launcher.ShutdownIfIdle(); };
        current.Show();
        current.Activate();
    }

    private ShortcutsWindow()
    {
        var p = Preferences.Load();
        Title = "GKR Studio — " + L.T("Kısayollar");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        Brush R(string key) => (Brush)Application.Current.FindResource(key);

        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string Wheel(string modifier) => modifier == "Kapalı" ? L.T("kapalı") : L.F("{0} + sağ tık", modifier);
        var sections = new (string Header, (string Keys, string What)[] Rows)[]
        {
            ("Gezgin ve masaüstü", [
                (Wheel(p.ConvertModifier), "Dönüştür tekerleği"),
                (Wheel(p.ToolsModifier), "Araçlar tekerleği"),
                (Wheel(p.ActionsModifier), "İşlemler tekerleği"),
                ("Sağ tık → GKR Studio", "Hepsi bir arada tekerlek"),
                (p.LinkHotkeyEnabled ? "Ctrl + Alt + L" : "Ctrl + Alt + L (kapalı)", "Panodaki linki indir"),
                ("3× Ctrl / Alt / Shift", "Bu kısayol penceresi"),
            ]),
            ("Tekerlekte", [
                ("Sol tık", "Seç"),
                ("1 – 9", "Hızlı seçim"),
                ("Tuşu bırak", "Kapat (tuşla açıldıysa)"),
                ("Esc / Backspace", "Kapat / geri"),
                ("Orta daire", "Geri / kapat"),
                ("Sağ tık", "Ayarlar"),
            ]),
            ("Görsel düzenleyicide", [
                ("Ctrl + Z", "Geri al"),
                ("Ctrl + S", "Kaydet (yeni kopya)"),
                ("Enter", "Seçili alanı kırp"),
            ]),
        };

        int row = 0;
        foreach (var (header, rows) in sections)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var h = new TextBlock { Text = L.T(header), FontWeight = FontWeights.SemiBold, Foreground = R("Accent"), Margin = new Thickness(0, row == 0 ? 0 : 14, 0, 6) };
            Grid.SetRow(h, row++);
            Grid.SetColumnSpan(h, 3);
            grid.Children.Add(h);
            foreach (var (keys, what) in rows)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var key = new Border
                {
                    Background = R("CardHover"), BorderBrush = R("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Left,
                    Child = new TextBlock { Text = L.T(keys), Foreground = R("Fg"), FontFamily = new FontFamily("Segoe UI Semibold"), FontSize = 12.5 },
                };
                var desc = new TextBlock { Text = L.T(what), Foreground = R("Muted"), VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
                Grid.SetRow(key, row); Grid.SetColumn(key, 0);
                Grid.SetRow(desc, row++); Grid.SetColumn(desc, 2);
                grid.Children.Add(key);
                grid.Children.Add(desc);
            }
        }

        var title = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var close = new TextBlock { Text = "Esc", Foreground = R("Muted"), FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(close, Dock.Right);
        title.Children.Add(close);
        title.Children.Add(new TextBlock { Text = L.T("GKR Studio kısayolları"), FontSize = 17, FontWeight = FontWeights.Bold, Foreground = R("Fg") });

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(grid);
        Content = new Border
        {
            Background = R("Card"), BorderBrush = R("Line"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(24, 20, 24, 20), Margin = new Thickness(24), Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 4, Opacity = 0.45 },
        };

        KeyDown += (_, e) => { if (e.Key is Key.Escape or Key.Enter or Key.Space) SafeClose(); };
        MouseLeftButtonUp += (_, _) => SafeClose();
        Deactivated += (_, _) => { if (IsLoaded) SafeClose(); }; // also fires *during* Close → must not close twice
        Closing += (_, _) => closing = true;
        Loaded += (_, _) => { SetForegroundWindow(new WindowInteropHelper(this).Handle); Activate(); Focus(); };
    }

    private bool closing;

    /// <summary>Close exactly once, and never from inside another Close (WPF throws in that case).</summary>
    private void SafeClose()
    {
        if (closing) return;
        closing = true;
        Dispatcher.BeginInvoke(Close);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
}
