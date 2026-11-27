using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GKRStudio.Core;

namespace GKRStudio.Mac;

/// <summary>
/// Menu-bar instance: tray icon, global input (modifier + right-click in Finder, ⌃⌥L, 3× modifier tap),
/// periodic update checks and self-healing of the login item / Finder Quick Actions after the app moves.
/// </summary>
public static class Resident
{
    private const string MutexName = @"Local\GKRStudio.Resident";
    private static Mutex? mutex;
    private static TrayIcon? tray;
    private static Preferences prefs = Preferences.Load();
    private static DateTime prefsStamp;

    public static bool IsRunning() => Mutex.TryOpenExisting(MutexName, out var m) && Dispose(m);
    private static bool Dispose(Mutex m) { m.Dispose(); return true; }

    public static void StartInBackground()
    {
        if (IsRunning() || Environment.ProcessPath is not { } exe) return;
        Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--tray" }, UseShellExecute = false });
    }

    public static bool Start()
    {
        mutex = new Mutex(true, MutexName, out bool created);
        if (!created) { mutex.Dispose(); mutex = null; return false; }
        AppHost.Resident = true;
        ApplyMacDefaults();
        SelfHeal();
        CreateTray();
        StartInput();
        StartUpdateChecks();
        return true;
    }

    private static Preferences Prefs()
    {
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GKRStudio", "settings.json");
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (stamp != prefsStamp) { prefs = Preferences.Load(); prefsStamp = stamp; }
        }
        catch (IOException) { }
        return prefs;
    }

    /// <summary>On a Mac, Control+click already means right-click, so the Tools wheel defaults to ⌘ Command (once).</summary>
    public static void ApplyMacDefaults()
    {
        try
        {
            var p = Preferences.Load();
            if (p.MacDefaults || !OperatingSystem.IsMacOS()) return;
            (p with
            {
                MacDefaults = true,
                ToolsModifier = p.ToolsModifier == "Ctrl" ? "Cmd" : p.ToolsModifier,
            }).Save();
        }
        catch { }
    }

    private static void SelfHeal()
    {
        try
        {
            var p = Preferences.Load();
            if (p.StartWithWindows) p.ApplyAutostart();
            if (ShellIntegration.State() == RegistrationState.Stale) ShellIntegration.Register();
        }
        catch { }
    }

    private static void Ui(Action a) => Dispatcher.UIThread.Post(a);

    // ---------------- menu bar ----------------

    private static void CreateTray()
    {
        var menu = new NativeMenu();
        void Item(string text, Action click)
        {
            var item = new NativeMenuItem(L.T(text));
            item.Click += (_, _) => Ui(click);
            menu.Items.Add(item);
        }
        Item("Panodaki linki indir", () => MenuHost.ShowForBackground(OutputPaths.Downloads));
        Item("Dosya seç…", () => ActionFlow.Run(new MenuAction(Catalog.PickFiles, "", ""), new WheelContext([], [], OutputPaths.Downloads, null)));
        Item("Kısayollar", ShortcutsWindow.Toggle);
        Item("Ayarlar", App.ShowSettings);
        menu.Items.Add(new NativeMenuItemSeparator());
        Item("Çıkış", () => { AppHost.Resident = false; AppHost.Exit(); });
        tray = new TrayIcon { Icon = Assets.TrayIcon(), ToolTipText = L.T("GKR Studio — arka planda"), Menu = menu, IsVisible = true };
        TrayIcon.SetIcons(Application.Current!, [tray]);
    }

    // ---------------- updates ----------------

    private static void StartUpdateChecks()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = TimeSpan.FromHours(6);
            if (Prefs().AutoUpdateCheck) await UpdateFlow.CheckAsync(silent: true);
        };
        timer.Start();
    }

    // ---------------- global input (event tap) ----------------

    private static bool swallowUp, swallowLeftUp;
    private static ulong lastFlags;
    private static int tapKey, taps;
    private static ulong tapDown, lastTap;

    private static DispatcherTimer? axTimer;

    /// <summary>
    /// Starts the event tap. Without Accessibility permission macOS refuses it: then ask (system prompt, which also
    /// lists GKR Studio in Settings) and poll every 2 s, starting the tap the moment permission is granted — no restart.
    /// </summary>
    private static void StartInput()
    {
        if (!OperatingSystem.IsMacOS()) return;
        bool trusted = MacInput.AXIsProcessTrusted();
        bool started = trusted && MacInput.Start(OnEvent);
        Program.Log($"event tap: trusted={trusted} started={started}");
        if (started) return;

        MacInput.RequestAccessibility();
        Ui(() => Toast.Show(L.T("Kısayollar için Erişilebilirlik iznini açın: Sistem Ayarları → Gizlilik ve Güvenlik → Erişilebilirlik → GKR Studio.")));
        axTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        int ticks = 0;
        axTimer.Tick += (_, _) =>
        {
            if (++ticks == 5) MacInput.OpenAccessibilitySettings(); // show the right pane if nothing happened yet
            if (!MacInput.AXIsProcessTrusted()) return;
            bool ok = MacInput.Start(OnEvent);
            Program.Log($"event tap after permission: started={ok}");
            if (!ok) return;
            axTimer!.Stop();
            Toast.Show(L.T("Kısayollar etkin ✔"));
        };
        axTimer.Start();
    }

    private static string Chord(ulong flags)
    {
        var parts = new List<string>();
        if ((flags & MacInput.FlagControl) != 0) parts.Add("Ctrl");
        if ((flags & MacInput.FlagShift) != 0) parts.Add("Shift");
        if ((flags & MacInput.FlagOption) != 0) parts.Add("Alt");
        if ((flags & MacInput.FlagCommand) != 0) parts.Add("Cmd");
        for (int i = 0; i < 3; i++) if (fHeld[i]) parts.Add("F" + (i + 1));
        return string.Join("+", parts);
    }

    // F1–F3 as wheel keys (keycodes 122, 120, 99). While assigned to a wheel, their key-down is held back; if no
    // click opened a wheel, the key is given back on release so it keeps its normal job.
    private static readonly long[] FKeyCodes = [122, 120, 99];
    private static readonly bool[] fHeld = new bool[3], fUsed = new bool[3];
    public const ulong FlagF1 = 1UL << 40, FlagF2 = 1UL << 41, FlagF3 = 1UL << 42;

    /// <summary>Modifier flags plus pseudo-flags for held F1–F3 (for the wheel's hold mode).</summary>
    public static ulong HeldFlags() =>
        MacInput.CurrentFlags() | (fHeld[0] ? FlagF1 : 0) | (fHeld[1] ? FlagF2 : 0) | (fHeld[2] ? FlagF3 : 0);

    /// <summary>Key down/up of an assigned F key: returns true when the event must be swallowed.</summary>
    private static bool HandleFunctionKey(IntPtr evt, bool down)
    {
        long key = MacInput.CGEventGetIntegerValueField(evt, 9 /* keycode */);
        int i = Array.IndexOf(FKeyCodes, key);
        if (i < 0) return false;
        if (MacInput.CGEventGetIntegerValueField(evt, 41 /* source pid */) == Environment.ProcessId) return false; // our own replay
        var p = Prefs();
        if (!p.ModifierClicksEnabled || !p.UsesFunctionKey("F" + (i + 1))) return false;
        if (down) { if (!fHeld[i]) { fHeld[i] = true; fUsed[i] = false; } }
        else
        {
            fHeld[i] = false;
            if (!fUsed[i]) { ushort code = (ushort)key; Task.Run(() => MacInput.PostKey(code)); }
        }
        return true;
    }

    /// <summary>Runs on the event-tap thread: must be quick. Return IntPtr.Zero to swallow the event.</summary>
    private static IntPtr OnEvent(IntPtr proxy, int type, IntPtr evt, IntPtr info)
    {
        try
        {
            if (type is -2 or -1) // tap disabled by timeout / user input
            {
                Program.Log($"event tap disabled ({(type == -2 ? "timeout" : "user input")}) → re-enable");
                MacInput.ReEnable();
                return evt;
            }
            if (type is 1 or 3 or 25) LogRawMouse(type, evt);
            switch (type)
            {
                case 1: // left mouse down: breaks a triple-tap sequence; opens a wheel when "one-finger click" is chosen
                {
                    taps = 0;
                    var lp = Prefs();
                    if (lp.MacWheelClick is not ("Left" or "Both")) break;
                    if (MacInput.CGEventGetIntegerValueField(evt, 41 /* source pid */) == Environment.ProcessId) break; // our own SelectUnderCursor
                    if (HandleWheelClick(lp, evt) is not { } opened) break;
                    swallowLeftUp = true;
                    return opened;
                }

                case 2 when swallowLeftUp: // matching left mouse up
                    swallowLeftUp = false;
                    return IntPtr.Zero;

                case 3: // right mouse down
                {
                    var p = Prefs();
                    if (p.MacWheelClick == "Left") break; // two-finger click stays a normal right-click
                    if (HandleWheelClick(p, evt) is not { } opened) break;
                    swallowUp = true;
                    return opened;
                }

                case 4 when swallowUp: // matching right mouse up
                    swallowUp = false;
                    return IntPtr.Zero;

                case 11 when HandleFunctionKey(evt, down: false): // key up of a held-back F key
                    return IntPtr.Zero;

                case 10: // key down
                {
                    taps = 0;
                    if (HandleFunctionKey(evt, down: true)) return IntPtr.Zero;
                    ulong flags = MacInput.CGEventGetFlags(evt);
                    long key = MacInput.CGEventGetIntegerValueField(evt, 9 /* keycode */);
                    bool ctrlOpt = (flags & MacInput.FlagControl) != 0 && (flags & MacInput.FlagOption) != 0 && (flags & (MacInput.FlagCommand | MacInput.FlagShift)) == 0;
                    if (key == 37 /* L */ && ctrlOpt && Prefs().LinkHotkeyEnabled)
                    {
                        Ui(() => MenuHost.ShowForBackground(OutputPaths.Downloads));
                        return IntPtr.Zero;
                    }
                    break;
                }

                case 12: // modifier flags changed → triple-tap detection
                {
                    ulong flags = MacInput.CGEventGetFlags(evt) & (MacInput.FlagShift | MacInput.FlagControl | MacInput.FlagOption);
                    ulong pressed = flags & ~lastFlags, released = lastFlags & ~flags;
                    lastFlags = flags;
                    ulong now = MacInput.CGEventGetTimestamp(evt) / 1_000_000; // ns → ms
                    if (pressed != 0)
                    {
                        int key = (int)pressed;
                        if (key != tapKey || now - lastTap > 450) { tapKey = key; taps = 0; }
                        tapDown = now;
                    }
                    else if (released != 0 && (int)released == tapKey)
                    {
                        if (now - tapDown > 300) taps = 0;
                        else if (++taps >= 3) { taps = 0; if (Prefs().TripleTapShortcuts) Ui(ShortcutsWindow.Toggle); }
                        lastTap = now;
                    }
                    break;
                }
            }
        }
        catch (Exception ex) { Program.Log("event tap callback error: " + ex.GetType().Name + ": " + ex.Message); } // never let it escape into the tap
        return evt;
    }

    /// <summary>
    /// Key + click in Finder (Desktop or a Finder window): opens the matching wheel and returns IntPtr.Zero to swallow
    /// the click, or null to let it through.
    /// </summary>
    private static IntPtr? HandleWheelClick(Preferences p, IntPtr evt)
    {
        // A wheel of ours is open: its own clicks (even with a key still held) are not a new key + click.
        if (MenuHost.Current != null) return null;
        string chord = Chord(MacInput.CGEventGetFlags(evt));
        if (chord.Length == 0) chord = Chord(MacInput.CGEventSourceFlagsState(1 /* HID system state */));
        if (chord.Length == 0) return null; // plain click: leave it to Finder
        var at = MacInput.CGEventGetLocation(evt);
        string? front = MacInput.FrontmostBundleId();
        if (front == "com.gkrstudio.app") return null; // our own window is in front
        string? under = front == "com.apple.finder" ? front : MacInput.BundleIdAt(at);
        if (under == "com.gkrstudio.app") return null;
        bool enabled = p.ModifierClicksEnabled, mapped = p.ModeFor(chord) is not null;
        bool inFinder = front == "com.apple.finder" || under == "com.apple.finder";
        // Key + clicks in other apps (Cmd+click in a browser…) are normal use: log them at most every few seconds.
        if (inFinder || DateTime.UtcNow - lastOtherLog > TimeSpan.FromSeconds(5))
        {
            if (!inFinder) lastOtherLog = DateTime.UtcNow;
            Program.Log($"wheel click chord={chord} enabled={enabled} mapped={mapped} front={front} under={under}");
        }
        if (enabled && !mapped && inFinder)
            Ui(() => Toast.Show(L.F("{0} + sağ tık bir tekerleğe atanmamış (Ayarlar → Kısayollar).", Settings.ModifierName(chord))));
        if (!enabled || !inFinder || p.ModeFor(chord) is not { } mode) return null;
        taps = 0;
        for (int i = 0; i < 3; i++) if (fHeld[i]) fUsed[i] = true; // the F key opened a wheel: don't give it back
        Task.Run(() => OpenForFinder(mode, chord, at));
        return IntPtr.Zero;
    }

    private static DateTime lastOtherLog;
    private static DateTime rawWindow;
    private static int rawCount;

    /// <summary>Raw data of mouse-down events (rate-limited) to see what real hardware sends compared with synthetic clicks.</summary>
    private static void LogRawMouse(int type, IntPtr evt)
    {
        ulong flags = MacInput.CGEventGetFlags(evt);
        bool modifier = (flags & (MacInput.FlagShift | MacInput.FlagControl | MacInput.FlagOption | MacInput.FlagCommand)) != 0;
        if (type == 1 && !modifier) return; // plain left clicks are noise
        var now = DateTime.UtcNow;
        if (now - rawWindow > TimeSpan.FromSeconds(10)) { rawWindow = now; rawCount = 0; }
        if (++rawCount > 20) return;
        Program.Log($"raw mouse type={type} flags=0x{flags:X} hid=0x{MacInput.CGEventSourceFlagsState(1):X} " +
                    $"button={MacInput.CGEventGetIntegerValueField(evt, 3)} subtype={MacInput.CGEventGetIntegerValueField(evt, 7)} " +
                    $"srcPid={MacInput.CGEventGetIntegerValueField(evt, 41)} srcState={MacInput.CGEventGetIntegerValueField(evt, 45)}");
    }

    /// <summary>Selects the item under the cursor (like a real right-click), reads Finder's selection and opens the wheel.</summary>
    private static void OpenForFinder(Catalog.Mode mode, string chord, MacInput.CGPoint at)
    {
        try
        {
            MacInput.SelectUnderCursor(at);
            Thread.Sleep(120);
            var (folder, items) = FinderSelection.Get();
            Program.Log($"finder selection: folder={folder} items={items.Count}");
            Ui(() =>
            {
                try
                {
                    if (items.Count > 0) MenuHost.ShowForFiles(items, mode);
                    else MenuHost.ShowForBackground(folder ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                    MenuHost.Current?.HoldWhilePressed(chord);
                }
                catch (Exception ex) { Program.Log("wheel show failed: " + ex); }
            });
        }
        catch (FinderSelection.NotAuthorizedException)
        {
            Program.Log("finder selection: Automation permission for Finder missing (-1743)");
            Ui(() => Toast.Show(L.T("Finder'daki seçimi okumak için izin gerekiyor: Sistem Ayarları → Gizlilik ve Güvenlik → Otomasyon → GKR Studio → Finder.")));
            MacInput.OpenSettingsPane("Privacy_Automation");
        }
        catch (Exception ex) { Program.Log("wheel failed: " + ex); Ui(async () => await Ask.Info(ex.Message)); }
    }
}
