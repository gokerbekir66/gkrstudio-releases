using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using GKRStudio.Core;

namespace GKRStudio.Mac;

public static class Settings
{
    public static readonly string[] ModifierChoices = ["Shift", "Ctrl", "Alt", "Cmd", "Ctrl+Shift", "Shift+Alt", "F1", "F2", "F3", "Kapalı"];

    /// <summary>Mac names for the stored modifier values.</summary>
    public static string ModifierName(string chord) => chord == "Kapalı" ? L.T("Kapalı") : string.Join(" + ", chord.Split('+').Select(k => k switch
    {
        "Shift" => "⇧ Shift", "Ctrl" => "⌃ Control", "Alt" => "⌥ Option", "Cmd" => "⌘ Command", _ => k,
    }));
}

/// <summary>Settings window for macOS (same features as the Windows one, with Finder / menu bar specifics).</summary>
public sealed class SettingsWindow : Window
{
    private readonly StackPanel body = new() { Margin = new Thickness(24), Spacing = 12 };
    private bool loading = true;
    private Preferences prefs = Preferences.Load();

    private readonly TextBlock finderStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock residentStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock axStatus = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel toolList = new() { Spacing = 2 };
    private readonly TextBlock note = new() { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox autostart = new(), modifiers = new(), hotkey = new(), tap = new(), bite = new(), icons = new(), autoUpdate = new();
    private readonly ComboBox convert = new(), tools = new(), actions = new(), theme = new(), language = new(), wheelClick = new(), cookies = new();
    private readonly WrapPanel accentPanel = new();
    private readonly TextBox feed = new(), linkBox = new() { Watermark = "https://…" };
    private string accent;

    public SettingsWindow()
    {
        Title = "GKR Studio";
        Icon = Assets.Icon();
        Width = 720; Height = 820; MinWidth = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        accent = prefs.AccentColor;
        Build();
        Content = new ScrollViewer { Content = body };
        Activated += (_, _) => Refresh();
        Refresh();
        loading = false;
        Opened += async (_, _) => await FirstRunAsync();
    }

    private readonly StackPanel packList = new() { Spacing = 4 };
    private readonly HashSet<string> downloading = [];

    /// <summary>One row per language pack: name, size, Download / Remove (fetched only when the user asks).</summary>
    private void RefreshPacks()
    {
        packList.Children.Clear();
        foreach (var pack in LanguagePacks.All)
        {
            bool installed = pack.Installed, busy = downloading.Contains(pack.Id);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("20,*,150,120") };
            var dot = new TextBlock { Text = installed ? "●" : "○", Foreground = installed ? Brushes.MediumSeaGreen : Brushes.Gray, VerticalAlignment = VerticalAlignment.Center };
            var name = new TextBlock { Text = pack.Name, VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock { Text = busy ? L.F("İndiriliyor… %{0}", 0) : installed ? L.T("Yüklü") : $"~{pack.SizeMb} MB", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
            var button = new Button { Content = L.T(installed ? "Kaldır" : "İndir"), IsEnabled = !busy, HorizontalAlignment = HorizontalAlignment.Stretch };
            Grid.SetColumn(name, 1); Grid.SetColumn(status, 2); Grid.SetColumn(button, 3);
            row.Children.Add(dot); row.Children.Add(name); row.Children.Add(status); row.Children.Add(button);
            button.Click += async (_, _) =>
            {
                if (pack.Installed) { try { LanguagePacks.Remove(pack); } catch { } RefreshPacks(); return; }
                downloading.Add(pack.Id);
                button.IsEnabled = false;
                try
                {
                    await Task.Run(() => LanguagePacks.InstallAsync(pack,
                        f => Avalonia.Threading.Dispatcher.UIThread.Post(() => status.Text = L.F("İndiriliyor… %{0}", (int)(f * 100))), _ => { }, CancellationToken.None));
                }
                catch (Exception ex) { status.Text = L.T("Hata: ") + ex.Message; await Task.Delay(4000); }
                finally { downloading.Remove(pack.Id); RefreshPacks(); }
            };
            packList.Children.Add(row);
        }
    }

    /// <summary>A collapsible section: clicking the title row shows or hides the content; the state is remembered (<see cref="UiState"/>).</summary>
    private static Border Card(string title, params Control[] children)
    {
        var body = new StackPanel { Spacing = 8 };
        foreach (var c in children) body.Children.Add(c);
        var heading = new TextBlock { Text = L.T(title), FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var chevron = new TextBlock { FontSize = 13, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 2, 0) };
        var header = new DockPanel { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Hand), Focusable = true };
        DockPanel.SetDock(chevron, Dock.Right);
        header.Children.Add(chevron);
        header.Children.Add(heading);
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(header);
        stack.Children.Add(body);

        void Apply(bool open)
        {
            body.IsVisible = open;
            chevron.Text = open ? "\u25B4" : "\u25BE"; // up / down triangle
        }
        void Toggle()
        {
            bool open = !body.IsVisible;
            Apply(open);
            UiState.SetOpen(title, open);
        }
        Apply(UiState.IsOpen(title));
        header.PointerReleased += (_, _) => Toggle();
        header.KeyDown += (_, e) => { if (e.Key is Key.Enter or Key.Space) { Toggle(); e.Handled = true; } };
        return new Border { Child = stack, Padding = new Thickness(16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)) };
    }

    private static Button Btn(string text, Action click, bool primary = false)
    {
        var b = new Button { Content = L.T(text) };
        if (primary) b.Classes.Add("accent");
        b.Click += (_, _) => click();
        return b;
    }

    private static StackPanel Row(params Control[] c)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var x in c) p.Children.Add(x);
        return p;
    }

    private static TextBlock Muted(string text) => new() { Text = L.T(text), Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    private void Build()
    {
        var header = new DockPanel();
        var logo = new Image { Source = Assets.Logo(), Width = 56, Height = 56, Margin = new Thickness(0, 0, 14, 0) };
        DockPanel.SetDock(logo, Dock.Left);
        header.Children.Add(logo);
        var ht = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        ht.Children.Add(new TextBlock { Text = "GKR Studio", FontSize = 24, FontWeight = FontWeight.Bold });
        ht.Children.Add(Muted("Görsel ve medya dönüştürme, panodaki linkten video/ses/resim indirme — Gezgin sağ tık menüsünden."));
        header.Children.Add(ht);
        body.Children.Add(header);

        body.Children.Add(Card("Finder menüsü", finderStatus,
            Row(Btn("Finder'a ekle", () => { ShellIntegration.Register(); Refresh(); }, true), Btn("Finder'dan kaldır", () => { ShellIntegration.Unregister(); Refresh(); })),
            Muted("Finder'da dosyaya sağ tıklayın → Hızlı İşlemler → GKR Studio (Dönüştür / Araçlar / İşlemler). Görünmezse: Sistem Ayarları → Gizlilik ve Güvenlik → Uzantılar → Finder.")));

        foreach (var (combo, value) in new[] { (convert, prefs.ConvertModifier), (tools, prefs.ToolsModifier), (actions, prefs.ActionsModifier) })
            Fill(combo, Settings.ModifierChoices.Select(m => (m, Settings.ModifierName(m))), value);
        Fill(wheelClick, [("Right", L.T("İki parmak (sağ tık)")), ("Left", L.T("Tek parmak (sol tık)")), ("Both", L.T("İkisi de"))], prefs.MacWheelClick);
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,200"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), Margin = new Thickness(22, 0, 0, 0) };
        void G(string label, ComboBox c, int row) { var t = new TextBlock { Text = L.T(label), VerticalAlignment = VerticalAlignment.Center }; Grid.SetRow(t, row); Grid.SetRow(c, row); Grid.SetColumn(c, 1); c.Margin = new Thickness(0, 3); grid.Children.Add(t); grid.Children.Add(c); }
        G("Dönüştür tekerleği", convert, 0); G("Araçlar tekerleği", tools, 1); G("İşlemler tekerleği", actions, 2); G("Tekerleği açan tıklama", wheelClick, 3);
        Check(autostart, "Oturum açılınca arka planda başlat", prefs.StartWithWindows);
        Check(modifiers, "Finder'da tuş + sağ tık tekerleklerini kullan", prefs.ModifierClicksEnabled);
        Check(hotkey, "⌃⌥L: panodaki linkin indirme tekerleği", prefs.LinkHotkeyEnabled);
        Check(tap, "3 kez hızlıca Ctrl / Alt / Shift: kısayol penceresi", prefs.TripleTapShortcuts);
        Check(bite, "Seçimde ateş animasyonu 🔥", prefs.BiteAnimation);
        body.Children.Add(Card("Arka plan ve kısayollar", residentStatus,
            Row(Btn("Arka planda başlat", () => { Resident.StartInBackground(); Task.Delay(1500).ContinueWith(_ => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh)); }, true)),
            axStatus, Row(Btn("Erişilebilirlik ayarlarını aç", MacInput.OpenAccessibilitySettings)),
            autostart, modifiers, grid,
            Muted("Tek parmak seçilirse tuş + normal tıklama tekerleği açar; Finder'da ⌘/Shift + tıklama ile çoklu seçim o tuşlarla çalışmaz."),
            hotkey, tap, bite, note));

        Fill(language, L.Languages.Select(l => (l.Code, l.Name)), prefs.Language);
        Fill(theme, AppTheme.Themes.Select(t => (t, L.T(t))), prefs.Theme);
        language.SelectionChanged += (_, _) => { if (loading) return; Save(); Close(); App.ShowSettings(); };
        Check(icons, "Tekerlekte yazıların altında simge göster", prefs.WheelIcons);
        BuildAccents();
        var look = new Grid { ColumnDefinitions = new ColumnDefinitions("110,200"), RowDefinitions = new RowDefinitions("Auto,Auto") };
        var l1 = new TextBlock { Text = L.T("Dil"), VerticalAlignment = VerticalAlignment.Center };
        var l2 = new TextBlock { Text = L.T("Tema"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(language, 1); Grid.SetRow(l2, 1); Grid.SetRow(theme, 1); Grid.SetColumn(theme, 1);
        language.Margin = theme.Margin = new Thickness(0, 3);
        look.Children.Add(l1); look.Children.Add(language); look.Children.Add(l2); look.Children.Add(theme);
        body.Children.Add(Card("Görünüm", look, new TextBlock { Text = L.T("Vurgu rengi") }, accentPanel, icons));

        body.Children.Add(Card("Dil paketleri (çeviri)",
            Muted("Çeviri internetsiz yapılır. Yalnızca istediğiniz dil paketlerini indirin; diğer diller İngilizce üzerinden çevrilir (ör. Türkçe → İngilizce → Felemenkçe)."),
            packList));
        RefreshPacks();

        var linkInfo = Muted("Bir YouTube/medya ya da doğrudan resim linki yapıştırın.");
        var openWheel = Btn("◎  İndirme tekerleğini aç", () => { if (LinkParser.Parse(linkBox.Text) is { } l) MenuHost.ShowForLink(l, OutputPaths.Downloads); }, true);
        linkBox.TextChanged += (_, _) => openWheel.IsEnabled = LinkParser.Parse(linkBox.Text) != null;
        openWheel.IsEnabled = false;
        Fill(cookies, Preferences.CookieBrowsers.Where(b => b != "edge").Select(b => (b, b == "" ? L.T("Kapalı") : Preferences.BrowserName(b))), prefs.CookieBrowser);
        var cookieRow = new Grid { ColumnDefinitions = new ColumnDefinitions("170,200"), Margin = new Thickness(0, 6, 0, 0) };
        var cookieLabel = new TextBlock { Text = L.T("Tarayıcı çerezleri"), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(cookies, 1); cookieRow.Children.Add(cookieLabel); cookieRow.Children.Add(cookies);
        body.Children.Add(Card("Linkten indir", linkBox, Row(Btn("Panodan yapıştır", () => linkBox.Text = ClipboardReader.ReadText() ?? ""), openWheel), linkInfo,
            Muted(L.F("İndirme klasörü: {0}", OutputPaths.Downloads)), cookieRow,
            Muted("Çerez onayı ya da giriş isteyen siteler için: o tarayıcıda siteyi bir kez açıp onaylayın. Chrome/Edge açıkken çerezlerini kilitler; gerekirse kapatın.")));

        var drop = new Border { Padding = new Thickness(18), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(Color.FromArgb(0x50, 0x80, 0x80, 0x80)), Child = Muted("Dosyaları buraya sürükleyin ya da seçin.") };
        DragDrop.SetAllowDrop(drop, true);
        drop.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).Where(p => p != null).Select(p => p!).ToList();
            if (files is { Count: > 0 }) MenuHost.ShowForFiles(files);
        });
        body.Children.Add(Card("Dosya dönüştür", drop, Btn("Dosya seç…", async () => { var f = await Pickers.Files(L.T("GKR Studio — dosya seçin")); if (f.Length > 0) MenuHost.ShowForFiles(f); })));

        feed.Text = string.IsNullOrWhiteSpace(prefs.UpdateFeed) ? Updater.DefaultFeed : prefs.UpdateFeed;
        feed.LostFocus += (_, _) => Save();
        Check(autoUpdate, "Güncellemeleri otomatik denetle", prefs.AutoUpdateCheck);
        body.Children.Add(Card("Güncellemeler", Muted(L.F("Sürüm {0}", Updater.Current)), new TextBlock { Text = L.T("Güncelleme adresi (update.json)") }, feed, autoUpdate,
            Btn("Güncellemeleri denetle", async () => { Save(); await UpdateFlow.CheckAsync(false); }),
            Muted("Güncellemeler dijital imza ve SHA-256 ile doğrulanır; doğrulanamayan paket kurulmaz.")));

        body.Children.Add(Card("Yerleşik araçlar (gelişmiş)", toolList,
            Row(Btn("Homebrew ile kur", InstallWithBrew, true), Btn("Yenile", Refresh)),
            Muted("ffmpeg, yt-dlp ve deno uygulamayla birlikte gelir; ayrıca bir şey kurmanız gerekmez. İsteğe bağlı: tesseract (OCR) ve LibreOffice (Office belgeleri) Homebrew ile kurulabilir.")));

        foreach (var c in new[] { convert, tools, actions, theme, wheelClick, cookies }) c.SelectionChanged += (_, _) => Save();
    }

    private void Check(CheckBox box, string text, bool value)
    {
        box.Content = L.T(text);
        box.IsChecked = value;
        box.IsCheckedChanged += (_, _) => Save();
    }

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

    private static string? Value(ComboBox c) => (c.SelectedItem as ComboBoxItem)?.Tag as string;

    private void BuildAccents()
    {
        accentPanel.Children.Clear();
        foreach (var (name, hex) in AppTheme.Accents)
        {
            bool sel = string.Equals(hex, accent, StringComparison.OrdinalIgnoreCase);
            var b = new Button
            {
                Width = 34, Height = 34, Margin = new Thickness(0, 0, 8, 8), Background = new SolidColorBrush(Color.Parse(hex)),
                BorderThickness = new Thickness(sel ? 3 : 0), BorderBrush = Brushes.White, Content = sel ? "✓" : null, Foreground = Brushes.White,
            };
            ToolTip.SetTip(b, L.T(name));
            b.Click += (_, _) => { accent = hex; BuildAccents(); Save(); };
            accentPanel.Children.Add(b);
        }
    }

    private void Save()
    {
        if (loading) return;
        var chosen = new[] { Value(convert), Value(tools), Value(actions) }.Where(m => m is not null and not "Kapalı").ToList();
        if (chosen.Distinct().Count() != chosen.Count) { note.Text = L.T("⚠ İki tekerleğe aynı tuş atanamaz."); return; }
        prefs = Preferences.Load() with
        {
            ConvertModifier = Value(convert) ?? "Shift", ToolsModifier = Value(tools) ?? "Ctrl", ActionsModifier = Value(actions) ?? "Alt",
            StartWithWindows = autostart.IsChecked == true, ModifierClicksEnabled = modifiers.IsChecked == true, LinkHotkeyEnabled = hotkey.IsChecked == true,
            TripleTapShortcuts = tap.IsChecked == true, MacWheelClick = Value(wheelClick) ?? "Right", CookieBrowser = Value(cookies) ?? "", BiteAnimation = bite.IsChecked == true, WheelIcons = icons.IsChecked == true,
            Theme = Value(theme) ?? "Koyu", Language = Value(language) ?? "tr", AccentColor = accent,
            UpdateFeed = (feed.Text ?? "").Trim() == Updater.DefaultFeed ? "" : (feed.Text ?? "").Trim(), AutoUpdateCheck = autoUpdate.IsChecked == true,
        };
        try
        {
            prefs.Save();
            AppTheme.Apply(prefs);
            note.Text = L.T("✔ Kaydedildi — anında uygulanır.");
        }
        catch (Exception ex) { note.Text = L.T("Kaydedilemedi: ") + ex.Message; }
    }

    private void Refresh()
    {
        var state = ShellIntegration.State();
        finderStatus.Text = L.T(state switch
        {
            RegistrationState.Registered => "● Etkin — bu uygulamaya bağlı.",
            RegistrationState.Stale => "● Kayıt eski bir konumu gösteriyor. Yeniden ekleyin.",
            _ => "○ Ekli değil.",
        });
        residentStatus.Text = L.T(Resident.IsRunning() ? "● Arka planda çalışıyor (menü çubuğunda GKR simgesi)." : "○ Arka planda çalışmıyor.");
        axStatus.Text = OperatingSystem.IsMacOS() && !MacInput.AXIsProcessTrusted()
            ? L.T("⚠ Tuş + sağ tık ve kısayollar için Erişilebilirlik izni gerekli: Sistem Ayarları → Gizlilik ve Güvenlik → Erişilebilirlik → GKR Studio'yı açın.")
            : L.T("● Erişilebilirlik izni var.");
        toolList.Children.Clear();
        foreach (var (name, path) in new[] { ("ffmpeg", Tools.Ffmpeg), ("yt-dlp", Tools.YtDlp), ("deno", Tools.Deno), ("tesseract", Tools.Tesseract), ("LibreOffice", OfficeBridge.Soffice) })
            toolList.Children.Add(new TextBlock { Text = $"{(path != null ? "●" : "○")}  {name}   {path ?? L.T("bulunamadı")}", Opacity = path != null ? 1 : 0.6, TextTrimming = TextTrimming.CharacterEllipsis });
    }

    /// <summary>Runs Homebrew in Terminal so the user sees progress (and any password prompt).</summary>
    private void InstallWithBrew()
    {
        if (Tools.Brew is not { } brew)
        {
            // no Homebrew: the download tools come as official standalone builds; the rest needs Homebrew
            _ = Task.Run(async () =>
            {
                try
                {
                    await Tools.InstallDownloadToolsAsync(_ => { });
                    Avalonia.Threading.Dispatcher.UIThread.Post(() => { Toast.Show(L.T("yt-dlp kuruldu. İndirmeyi yeniden başlatın.")); Refresh(); });
                }
                catch (Exception ex) { Program.Log("yt-dlp install failed: " + ex); }
            });
            AppHost.Open("https://brew.sh");
            return;
        }
        string cmd = $"{brew} install ffmpeg yt-dlp deno tesseract tesseract-lang";
        string script = $"tell application \"Terminal\" to do script \"{cmd}\"\ntell application \"Terminal\" to activate";
        Process.Start(new ProcessStartInfo("/usr/bin/osascript") { ArgumentList = { "-e", script }, UseShellExecute = false });
    }

    /// <summary>First start on a new Mac: offer Finder menu + login item + background in one click.</summary>
    private async Task FirstRunAsync()
    {
        var p = Preferences.Load();
        if (p.FirstRunDone) return;
        bool yes = await Ask.YesNo(L.T("GKR Studio'ya hoş geldiniz!\n\nŞunlar kurulsun mu?\n• Finder Hızlı İşlemler menüsü\n• Oturum açılınca arka planda başlatma\n• Şimdi arka planda başlatma (menü çubuğu, Shift/Ctrl/Option + sağ tık)\n\nHepsi Ayarlar'dan değiştirilebilir."));
        try
        {
            if (yes)
            {
                ShellIntegration.Register();
                (p with { FirstRunDone = true, StartWithWindows = true }).Save();
                Resident.StartInBackground();
                if (OperatingSystem.IsMacOS() && !MacInput.AXIsProcessTrusted())
                {
                    await Ask.Info(L.T("Tuş + sağ tık ve kısayollar için Erişilebilirlik izni gerekiyor. Açılan pencerede GKR Studio'yı etkinleştirin."));
                    MacInput.OpenAccessibilitySettings();
                }
            }
            else (p with { FirstRunDone = true }).Save();
        }
        catch (Exception ex) { await Ask.Info(ex.Message); }
        loading = true;
        autostart.IsChecked = Preferences.Load().StartWithWindows;
        loading = false;
        Refresh();
    }
}
