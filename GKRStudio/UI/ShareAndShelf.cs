using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GKRStudio.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace GKRStudio.UI;

/// <summary>Opens the Windows Share sheet for files (Windows' counterpart of AirDrop).</summary>
public static class ShareHelper
{
    public static void Share(IReadOnlyList<string> paths)
    {
        // The share sheet needs a real top-level window; a tiny transparent one at the cursor works.
        var host = new Window
        {
            Width = 1, Height = 1, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, Topmost = true, Left = SystemParameters.WorkArea.Width / 2, Top = SystemParameters.WorkArea.Height / 2,
        };
        host.Show();
        var hwnd = new WindowInteropHelper(host).Handle;
        try
        {
            var manager = DataTransferManagerInterop.GetForWindow(hwnd);
            manager.DataRequested += async (_, args) =>
            {
                var deferral = args.Request.GetDeferral();
                try
                {
                    var items = new List<IStorageItem>();
                    foreach (var p in paths)
                        items.Add(Directory.Exists(p) ? await StorageFolder.GetFolderFromPathAsync(p) : await StorageFile.GetFileFromPathAsync(p));
                    args.Request.Data.Properties.Title = paths.Count == 1 ? Path.GetFileName(paths[0]) : L.F("{0} dosya", paths.Count);
                    args.Request.Data.SetStorageItems(items);
                }
                finally { deferral.Complete(); }
            };
            DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception ex)
        {
            host.Close();
            MessageBox.Show(L.T("Paylaş açılamadı: ") + ex.Message, "GKR Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            Launcher.ShutdownIfIdle();
            return;
        }
        // Keep the owner alive while the share sheet is open, then clean up.
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(3) };
        timer.Tick += (_, _) => { timer.Stop(); host.Close(); Launcher.ShutdownIfIdle(); };
        timer.Start();
    }
}

/// <summary>
/// "Yüzen pencere": a small always-on-top shelf that holds files so they can be dragged out later
/// (to another folder, an e-mail, a browser upload…). Removing an item never touches the file itself.
/// </summary>
public sealed class FloatShelf : Window
{
    private static FloatShelf? current;
    private readonly ListBox list = new();
    private readonly List<string> items = [];

    public static void Add(IEnumerable<string> paths)
    {
        if (current == null)
        {
            current = new FloatShelf();
            current.Closed += (_, _) => { current = null; Launcher.ShutdownIfIdle(); };
            current.Show();
        }
        foreach (var p in paths) current.AddItem(p);
        current.Activate();
    }

    private FloatShelf()
    {
        Title = L.T("GKR Studio — Raf");
        Icon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));
        Width = 280; Height = 360;
        Topmost = true;
        ShowInTaskbar = true;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Style = (Style)Application.Current.FindResource("AppWindow");
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 16;
        Top = area.Bottom - Height - 16;

        list.Background = Brushes.Transparent;
        list.BorderThickness = new Thickness(0);
        list.Foreground = (Brush)Application.Current.FindResource("Fg");
        list.SelectionMode = SelectionMode.Extended;
        list.PreviewMouseMove += OnDragOut;
        list.MouseDoubleClick += (_, _) => { if (list.SelectedItem is ListBoxItem { Tag: string p }) Process.Start(new ProcessStartInfo(p) { UseShellExecute = true }); };
        list.KeyDown += (_, e) => { if (e.Key == Key.Delete) RemoveSelected(); };

        var hint = new TextBlock { Text = L.T("Dosyaları buradan sürükleyip bırakın · Del: raftan kaldır"), FontSize = 11, Margin = new Thickness(10, 6, 10, 6),
                                   TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("Muted") };
        var clear = new Button { Content = L.T("Rafı temizle"), Margin = new Thickness(10, 0, 10, 10) };
        clear.Click += (_, _) => { items.Clear(); list.Items.Clear(); };
        var root = new DockPanel();
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(clear, Dock.Bottom);
        root.Children.Add(hint);
        root.Children.Add(clear);
        root.Children.Add(list);
        Content = root;

        AllowDrop = true; // files can also be dropped onto the shelf
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) foreach (var p in dropped) AddItem(p); };
    }

    private void AddItem(string path)
    {
        if (items.Contains(path, StringComparer.OrdinalIgnoreCase) || (!File.Exists(path) && !Directory.Exists(path))) return;
        items.Add(path);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = Directory.Exists(path) ? "📁" : "📄", Margin = new Thickness(0, 0, 8, 0) });
        row.Children.Add(new TextBlock { Text = Path.GetFileName(path.TrimEnd('\\')), ToolTip = path, TextTrimming = TextTrimming.CharacterEllipsis });
        list.Items.Add(new ListBoxItem { Content = row, Tag = path, Padding = new Thickness(8, 5, 8, 5) });
    }

    private void RemoveSelected()
    {
        foreach (var item in list.SelectedItems.Cast<ListBoxItem>().ToList())
        {
            items.Remove((string)item.Tag);
            list.Items.Remove(item);
        }
    }

    private void OnDragOut(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || list.SelectedItems.Count == 0) return;
        var paths = list.SelectedItems.Cast<ListBoxItem>().Select(i => (string)i.Tag).Where(p => File.Exists(p) || Directory.Exists(p)).ToArray();
        if (paths.Length == 0) return;
        DragDrop.DoDragDrop(list, new DataObject(DataFormats.FileDrop, paths), DragDropEffects.Copy | DragDropEffects.Move);
    }
}
