using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GKRStudio.Core;
using Microsoft.Win32;

namespace GKRStudio.UI;

public partial class MainWindow : Window
{
    private IReadOnlyList<string> files = [];

    public MainWindow()
    {
        InitializeComponent();
        Preferences.Load(); // sets the UI language
        MakeSectionsCollapsible(); // before translating: the Turkish titles are the keys
        Localizer.Translate(this);
        DownloadDirText.Text = L.F("İndirme klasörü: {0}", OutputPaths.Downloads);
        Activated += (_, _) => RefreshAll();
        LoadPreferences();
        RefreshAll();
        RebuildLink();
        Loaded += (_, _) => Dispatcher.BeginInvoke(FirstRunSetup, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// Every card whose first child is a heading becomes a collapsible section: clicking the heading row shows or hides
    /// the rest. The open/closed state is remembered per section (<see cref="UiState"/>).
    /// </summary>
    private void MakeSectionsCollapsible()
    {
        if (Content is not ScrollViewer { Content: StackPanel root }) return;
        foreach (var card in root.Children.OfType<Border>())
        {
            if (card.Child is not StackPanel stack || stack.Children.Count < 2 || stack.Children[0] is not TextBlock heading) continue;
            string key = heading.Text;

            var body = new StackPanel();
            while (stack.Children.Count > 1)
            {
                var child = stack.Children[1];
                stack.Children.RemoveAt(1);
                body.Children.Add(child);
            }
            stack.Children.Add(body);

            stack.Children.Remove(heading);
            heading.Margin = new Thickness(0);
            heading.VerticalAlignment = VerticalAlignment.Center;
            var chevron = new TextBlock
            {
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0),
            };
            chevron.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
            var header = new DockPanel { Background = Brushes.Transparent, Cursor = System.Windows.Input.Cursors.Hand, Focusable = true };
            DockPanel.SetDock(chevron, Dock.Right);
            header.Children.Add(chevron);
            header.Children.Add(heading);
            stack.Children.Insert(0, header);

            void Apply(bool open)
            {
                body.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                chevron.Text = open ? "" : ""; // chevron up / down
                header.Margin = new Thickness(0, 0, 0, open ? 8 : 0);
                System.Windows.Automation.AutomationProperties.SetName(header, heading.Text);
            }
            void Toggle()
            {
                bool open = body.Visibility != Visibility.Visible;
                Apply(open);
                UiState.SetOpen(key, open);
            }
            Apply(UiState.IsOpen(key));
            header.MouseLeftButtonUp += (_, _) => Toggle();
            header.KeyDown += (_, e) => { if (e.Key is System.Windows.Input.Key.Enter or System.Windows.Input.Key.Space) { Toggle(); e.Handled = true; } };
        }
    }

    /// <summary>First start on a new PC: offer the right-click menu + start with Windows + background mode in one click.</summary>
    private void FirstRunSetup()
    {
        var p = Preferences.Load();
        if (p.FirstRunDone) return;
        var answer = MessageBox.Show(this, L.T("GKR Studio'ya hoş geldiniz!\n\nŞunlar kurulsun mu?\n• Sağ tık menüsü\n• Windows açılınca arka planda başlatma\n• Şimdi arka planda başlatma (Shift/Ctrl/Alt + sağ tık tekerlekleri)\n\nHepsi Ayarlar'dan değiştirilebilir."),
            "GKR Studio", MessageBoxButton.YesNo, MessageBoxImage.Question);
        try
        {
            if (answer == MessageBoxResult.Yes)
            {
                ShellIntegration.Register();
                (p with { FirstRunDone = true, StartWithWindows = true }).Save();
                Resident.StartInBackground();
            }
            else (p with { FirstRunDone = true }).Save();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "GKR Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        loadingPrefs = true; AutostartBox.IsChecked = Preferences.Load().StartWithWindows; loadingPrefs = false;
        RefreshAll();
    }

    private void RefreshAll()
    {
        RefreshShell();
        RefreshTools();
        RefreshPacks();
        RefreshResident();
    }

    // ---- preferences ----

    private bool loadingPrefs;

    private void LoadPreferences()
    {
        loadingPrefs = true;
        var p = Preferences.Load();
        Fill(ConvertCombo, Preferences.ModifierChoices.Select(m => (m, L.T(m))), p.ConvertModifier);
        Fill(ToolsCombo, Preferences.ModifierChoices.Select(m => (m, L.T(m))), p.ToolsModifier);
        Fill(ActionsCombo, Preferences.ModifierChoices.Select(m => (m, L.T(m))), p.ActionsModifier);
        AutostartBox.IsChecked = p.StartWithWindows;
        ModifierBox.IsChecked = p.ModifierClicksEnabled;
        HotkeyBox.IsChecked = p.LinkHotkeyEnabled;
        BiteBox.IsChecked = p.BiteAnimation;
        TapBox.IsChecked = p.TripleTapShortcuts;

        foreach (var box in new[] { AutostartBox, ModifierBox, HotkeyBox, BiteBox, TapBox })
        {
            box.Checked += (_, _) => SavePreferences();
            box.Unchecked += (_, _) => SavePreferences();
        }
        foreach (var combo in new[] { ConvertCombo, ToolsCombo, ActionsCombo })
            combo.SelectionChanged += (_, _) => SavePreferences();

        Fill(CookieCombo, Preferences.CookieBrowsers.Where(b => b != "safari").Select(b => (b, b == "" ? L.T("Kapalı") : Preferences.BrowserName(b))), p.CookieBrowser);
        CookieCombo.SelectionChanged += (_, _) => SavePreferences();
        Fill(ThemeCombo, Theme.Themes.Select(t => (t, L.T(t))), Theme.Themes.Contains(p.Theme) ? p.Theme : "Koyu");
        ThemeCombo.SelectionChanged += (_, _) => SavePreferences();
        Fill(LanguageCombo, L.Languages.Select(l => (l.Code, l.Name)), p.Language);
        LanguageCombo.SelectionChanged += (_, _) =>
        {
            SavePreferences();
            // reopen the settings window so every text is rebuilt in the new language
            Dispatcher.BeginInvoke(() => { Close(); App.ShowSettings(); });
        };
        IconsBox.IsChecked = p.WheelIcons;
        IconsBox.Checked += (_, _) => SavePreferences();
        IconsBox.Unchecked += (_, _) => SavePreferences();
        accent = p.AccentColor;
        BuildAccentSwatches();

        VersionText.Text = L.F("Sürüm {0}", Updater.Current);
        FeedBox.Text = string.IsNullOrWhiteSpace(p.UpdateFeed) ? Updater.DefaultFeed : p.UpdateFeed;
        FeedBox.LostFocus += (_, _) => SavePreferences();
        AutoUpdateBox.IsChecked = p.AutoUpdateCheck;
        AutoUpdateBox.Checked += (_, _) => SavePreferences();
        AutoUpdateBox.Unchecked += (_, _) => SavePreferences();

        BuildVisibility(p);
        loadingPrefs = false;
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        SavePreferences();
        await UpdateFlow.CheckAsync(silent: false, this);
    }

    private string accent = "#3A5FD9";

    /// <summary>Combo items show translated text but keep the internal (Turkish / code) value in Tag.</summary>
    private static void Fill(ComboBox combo, IEnumerable<(string Value, string Text)> items, string selected)
    {
        combo.Items.Clear();
        foreach (var (value, text) in items)
        {
            var item = new ComboBoxItem { Content = text, Tag = value };
            combo.Items.Add(item);
            if (value == selected) combo.SelectedItem = item;
        }
        if (combo.SelectedItem == null && combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private static string? Value(ComboBox combo) => (combo.SelectedItem as ComboBoxItem)?.Tag as string;

    private void BuildAccentSwatches()
    {
        AccentPanel.Children.Clear();
        foreach (var (name, hex) in Theme.Accents)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            bool selected = string.Equals(hex, accent, StringComparison.OrdinalIgnoreCase);
            var swatch = new Button
            {
                ToolTip = L.T(name),
                Width = 34, Height = 34, Padding = new Thickness(0), Margin = new Thickness(0, 0, 8, 8),
                Background = new SolidColorBrush(color),
                BorderBrush = selected ? (Brush)FindResource("Fg") : new SolidColorBrush(color),
                BorderThickness = new Thickness(selected ? 3 : 1),
                Content = selected ? new TextBlock { Text = "✓", Foreground = Brushes.White, FontWeight = FontWeights.Bold } : null,
            };
            swatch.Click += (_, _) => { accent = hex; BuildAccentSwatches(); SavePreferences(); };
            AccentPanel.Children.Add(swatch);
        }
    }

    private void BuildVisibility(Preferences p)
    {
        VisibilityPanel.Children.Clear();
        var sections = new (string Title, IEnumerable<MenuAction> Actions)[]
        {
            ("Görsel", Catalog.Convert(InputKind.Image, 2).Concat(Catalog.Tools(InputKind.Image, 2))),
            ("Video", Catalog.Convert(InputKind.Video, 2).Concat(Catalog.Tools(InputKind.Video, 2))),
            ("Ses", Catalog.Convert(InputKind.Audio, 2).Concat(Catalog.Tools(InputKind.Audio, 2))),
            ("PDF", Catalog.Tools(InputKind.Pdf, 2)),
            ("İşlemler", Catalog.Actions(InputKind.Archive, 2)),
        };
        var seen = new HashSet<string>();
        foreach (var (title, actions) in sections)
        {
            VisibilityPanel.Children.Add(new TextBlock { Text = L.T(title), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 4) });
            var wrap = new WrapPanel();
            foreach (var a in actions.Where(a => seen.Add(title + a.Id)))
            {
                var box = new CheckBox
                {
                    Content = a.Title, Tag = a.Id, IsChecked = !p.HiddenActions.Contains(a.Id),
                    Margin = new Thickness(0, 0, 16, 6), Foreground = (Brush)FindResource("Fg"),
                };
                box.Checked += (_, _) => SavePreferences();
                box.Unchecked += (_, _) => SavePreferences();
                wrap.Children.Add(box);
            }
            VisibilityPanel.Children.Add(wrap);
        }
    }

    private void SavePreferences()
    {
        if (loadingPrefs) return;
        var chosen = new[] { Value(ConvertCombo), Value(ToolsCombo), Value(ActionsCombo) }
            .Where(m => m is not null and not "Kapalı").ToList();
        if (chosen.Distinct().Count() != chosen.Count)
        {
            PrefsNote.Text = L.T("⚠ İki tekerleğe aynı tuş atanamaz.");
            PrefsNote.Foreground = (Brush)FindResource("Warn");
            return;
        }

        // An id unchecked in any section is hidden everywhere (ids like av:mp3 appear for video and audio).
        var hidden = VisibilityPanel.Children.OfType<WrapPanel>().SelectMany(w => w.Children.OfType<CheckBox>())
            .Where(b => b.IsChecked != true).Select(b => (string)b.Tag).Distinct().ToList();

        var p = Preferences.Load() with
        {
            ConvertModifier = Value(ConvertCombo) ?? "Shift",
            ToolsModifier = Value(ToolsCombo) ?? "Ctrl",
            ActionsModifier = Value(ActionsCombo) ?? "Alt",
            StartWithWindows = AutostartBox.IsChecked == true,
            ModifierClicksEnabled = ModifierBox.IsChecked == true,
            LinkHotkeyEnabled = HotkeyBox.IsChecked == true,
            BiteAnimation = BiteBox.IsChecked == true,
            TripleTapShortcuts = TapBox.IsChecked == true,
            HiddenActions = hidden,
            Theme = Value(ThemeCombo) ?? "Koyu",
            Language = Value(LanguageCombo) ?? "tr",
            UpdateFeed = FeedBox.Text.Trim() == Updater.DefaultFeed ? "" : FeedBox.Text.Trim(),
            AutoUpdateCheck = AutoUpdateBox.IsChecked == true,
            AccentColor = accent,
            WheelIcons = IconsBox.IsChecked == true,
            CookieBrowser = Value(CookieCombo) ?? "",
        };
        try
        {
            p.Save();
            Theme.Apply(p); // live: all windows use DynamicResource colours; wheels read the settings when they open
            PrefsNote.Text = L.T("✔ Kaydedildi — anında uygulanır.");
            PrefsNote.Foreground = (Brush)FindResource("Ok");
        }
        catch (Exception ex)
        {
            PrefsNote.Text = L.T("Kaydedilemedi: ") + ex.Message;
            PrefsNote.Foreground = (Brush)FindResource("Bad");
        }
    }

    private void RefreshResident()
    {
        bool running = Resident.IsRunning();
        ResidentStatus.Text = L.T(running ? "● Arka planda çalışıyor (sistem tepsisinde GKR simgesi)." : "○ Arka planda çalışmıyor.");
        ResidentStatus.Foreground = (Brush)FindResource(running ? "Ok" : "Muted");
        ResidentButton.IsEnabled = !running;
    }

    private async void Resident_Click(object sender, RoutedEventArgs e)
    {
        Resident.StartInBackground();
        await Task.Delay(1200);
        RefreshResident();
    }

    private void RefreshShell()
    {
        var state = ShellIntegration.State();
        (ShellStatus.Text, ShellStatus.Foreground) = state switch
        {
            RegistrationState.Registered => (L.T("● Etkin — bu uygulamaya bağlı."), (Brush)FindResource("Ok")),
            RegistrationState.Stale => (L.T("● Kayıt eski bir konumu gösteriyor. Yeniden ekleyin."), (Brush)FindResource("Warn")),
            _ => (L.T("○ Ekli değil."), (Brush)FindResource("Muted")),
        };
        UnregisterButton.IsEnabled = state != RegistrationState.NotRegistered;
        RegisterButton.Content = L.T(state == RegistrationState.Registered ? "Yeniden ekle" : "Sağ tık menüsüne ekle");
    }

    private readonly HashSet<string> downloading = [];

    /// <summary>One row per language pack: name, size, Download / Remove (a pack is only fetched when the user asks).</summary>
    private void RefreshPacks()
    {
        PackList.Children.Clear();
        foreach (var pack in LanguagePacks.All)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            bool installed = pack.Installed, busy = downloading.Contains(pack.Id);
            row.Children.Add(new TextBlock { Text = installed ? "●" : "○", Foreground = (Brush)FindResource(installed ? "Ok" : "Muted"), Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
            var button = new Button
            {
                Content = L.T(installed ? "Kaldır" : "İndir"), IsEnabled = !busy, MinWidth = 110, Margin = new Thickness(8, 0, 0, 0),
            };
            DockPanel.SetDock(button, Dock.Right);
            row.Children.Add(button);
            var status = new TextBlock
            {
                Text = busy ? L.F("İndiriliyor… %{0}", 0) : installed ? L.T("Yüklü") : $"~{pack.SizeMb} MB",
                Foreground = (Brush)FindResource("Muted"), Width = 150, VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(status, Dock.Right);
            row.Children.Add(status);
            row.Children.Add(new TextBlock { Text = pack.Name, VerticalAlignment = VerticalAlignment.Center });
            button.Click += async (_, _) =>
            {
                if (pack.Installed) { try { LanguagePacks.Remove(pack); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "GKR Studio"); } RefreshPacks(); return; }
                downloading.Add(pack.Id);
                button.IsEnabled = false;
                try
                {
                    await Task.Run(() => LanguagePacks.InstallAsync(pack,
                        f => Dispatcher.BeginInvoke(() => status.Text = L.F("İndiriliyor… %{0}", (int)(f * 100))), _ => { }, CancellationToken.None));
                }
                catch (Exception ex) { MessageBox.Show(this, L.T("Hata: ") + ex.Message, "GKR Studio", MessageBoxButton.OK, MessageBoxImage.Error); }
                finally { downloading.Remove(pack.Id); RefreshPacks(); }
            };
            PackList.Children.Add(row);
        }
    }

    private void RefreshTools()
    {
        ToolList.Children.Clear();
        bool anyMissing = false;
        foreach (var (name, path) in new[] { ("ffmpeg", Tools.Ffmpeg), ("yt-dlp", Tools.YtDlp), ("deno", Tools.Deno) })
        {
            anyMissing |= path == null;
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            var badge = new TextBlock
            {
                Text = path != null ? "●" : "○",
                Foreground = (Brush)FindResource(path != null ? "Ok" : "Bad"),
                Margin = new Thickness(0, 0, 8, 0),
            };
            row.Children.Add(badge);
            row.Children.Add(new TextBlock { Text = name, Width = 70, FontWeight = FontWeights.SemiBold });
            row.Children.Add(new TextBlock
            {
                Text = path ?? L.T("bulunamadı"),
                Foreground = (Brush)FindResource("Muted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = path,
            });
            ToolList.Children.Add(row);
        }
        InstallButton.IsEnabled = anyMissing;
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        try { ShellIntegration.Register(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        RefreshShell();
    }

    private void Unregister_Click(object sender, RoutedEventArgs e)
    {
        try { ShellIntegration.Unregister(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
        RefreshShell();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var packages = new List<string>();
        if (Tools.Ffmpeg == null) packages.Add("Gyan.FFmpeg");
        if (Tools.YtDlp == null) packages.Add("yt-dlp.yt-dlp");
        if (Tools.Deno == null) packages.Add("DenoLand.Deno");
        if (packages.Count == 0) return;

        if (MessageBox.Show(this,
                L.F("Şu paketler winget ile resmi kaynaklarından kurulacak:\n\n  {0}\n\nDevam edilsin mi?", string.Join("\n  ", packages)),
                Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        InstallButton.IsEnabled = false;
        InstallButton.Content = "Kuruluyor…";
        var log = new List<string>();
        try
        {
            foreach (var id in packages)
            {
                int code = await ProcessRunner.RunAsync("winget",
                    ["install", "--id", id, "-e", "--source", "winget", "--silent", "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity"],
                    line => { lock (log) log.Add(line); }, CancellationToken.None);
                if (code != 0 && Tools.Find(id switch { "Gyan.FFmpeg" => "ffmpeg.exe", "yt-dlp.yt-dlp" => "yt-dlp.exe", _ => "deno.exe" }) == null)
                    log.Add($"{id}: winget çıkış kodu {code}");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "winget çalıştırılamadı: " + ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            InstallButton.Content = L.T("Araçları onar (winget)");
            RefreshTools();
        RefreshPacks();
            RebuildLink();
            RebuildFiles();
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshAll();

    private void OpenToolsDir_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(Tools.LocalToolsDir);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { Tools.LocalToolsDir } });
    }

    // ---- link ----

    private void Paste_Click(object sender, RoutedEventArgs e) => LinkBox.Text = ClipboardReader.ReadText() ?? "";

    private void LinkBox_TextChanged(object sender, TextChangedEventArgs e) => RebuildLink();

    private void RebuildLink()
    {
        var link = LinkParser.Parse(LinkBox.Text);
        LinkInfoText.Text = link switch
        {
            null => L.T("Bir YouTube/medya ya da doğrudan resim linki yapıştırın."),
            { Kind: LinkKind.Image } => L.T("Resim linki algılandı."),
            { Kind: LinkKind.YouTube } => L.T("YouTube linki algılandı."),
            _ => L.T("Medya sayfası linki (yt-dlp destekli siteler)."),
        };
        LinkWheelButton.IsEnabled = link != null;
    }

    private void LinkWheel_Click(object sender, RoutedEventArgs e)
    {
        var link = LinkParser.Parse(LinkBox.Text);
        if (link != null) MenuHost.ShowForLink(link, OutputPaths.Downloads);
    }

    // ---- files ----

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] dropped) SetFiles(dropped);
    }

    private void PickFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "Medya dosyaları|" + string.Join(";", Catalog.AllExtensions.Select(x => "*" + x)) + "|Tüm dosyalar|*.*",
        };
        if (dialog.ShowDialog(this) == true) SetFiles(dialog.FileNames);
    }

    private void SetFiles(IEnumerable<string> paths)
    {
        files = paths.Where(File.Exists).ToList();
        RebuildFiles();
        if (FileWheelButton.IsEnabled) MenuHost.ShowForFiles(files); // wheel opens right at the cursor
    }

    private void RebuildFiles()
    {
        var kind = Catalog.Classify(files);
        int usable = files.Count(p => Catalog.KindOf(p) == kind);
        FileInfoText.Text = files.Count == 0 ? L.T("Dosyaları buraya sürükleyin ya da seçin.") : Catalog.Describe(kind, usable);
        FileWheelButton.IsEnabled = Catalog.ForFiles(kind).Count > 0;
    }

    private void FileWheel_Click(object sender, RoutedEventArgs e)
    {
        if (files.Count > 0) MenuHost.ShowForFiles(files);
    }
}
