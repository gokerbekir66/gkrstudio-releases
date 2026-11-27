using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using GKRStudio.Core;
using Forms = System.Windows.Forms;

namespace GKRStudio.UI;

/// <summary>
/// Background (tray) instance: a tray icon, the modifier + right-click hook for Explorer/desktop,
/// and the Ctrl+Alt+L hotkey for the clipboard-link wheel. Starts with Windows if enabled in settings.
/// </summary>
public static class Resident
{
    public const string MutexName = @"Local\GKRStudio.Resident";
    private static Mutex? mutex;
    private static Forms.NotifyIcon? tray;
    private static HwndSource? messageWindow;
    private static IntPtr hook;
    private static HookProc? hookProc; // keep the delegate alive
    private static bool swallowUp;
    private static Preferences prefs = Preferences.Load();
    private static DateTime prefsStamp;

    public static bool IsRunning() => Mutex.TryOpenExisting(MutexName, out var m) && DisposeTrue(m);
    private static bool DisposeTrue(Mutex m) { m.Dispose(); return true; }

    public static void StartInBackground()
    {
        if (Environment.ProcessPath is { } exe && !IsRunning())
            Process.Start(new ProcessStartInfo(exe) { ArgumentList = { "--tray" }, UseShellExecute = false });
    }

    /// <returns>false if another background instance already runs.</returns>
    public static bool Start()
    {
        mutex = new Mutex(true, MutexName, out bool created);
        if (!created) { mutex.Dispose(); mutex = null; return false; }

        Launcher.Resident = true;
        SelfHeal();
        StartUpdateChecks();
        CreateTray();
        CreateMessageWindow();
        InstallHook();
        Application.Current.Exit += (_, _) => Stop();
        return true;
    }

    /// <summary>
    /// If the app folder was moved/copied, the right-click menu and the autostart entry would point to a
    /// dead path. Rewrite them to this exe on every start (only if the user had enabled them).
    /// </summary>
    public static void SelfHeal()
    {
        try
        {
            var p = Preferences.Load();
            if (p.StartWithWindows) p.ApplyAutostart();
            if (ShellIntegration.State() == RegistrationState.Stale) ShellIntegration.Register();
        }
        catch (Exception) { /* registry not writable: nothing critical */ }
    }

    private static DispatcherTimer? updateTimer;

    /// <summary>Background update check: 1 minute after start, then every 6 hours (if enabled in settings).</summary>
    private static void StartUpdateChecks()
    {
        updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        updateTimer.Tick += async (_, _) =>
        {
            updateTimer.Interval = TimeSpan.FromHours(6);
            if (Prefs().AutoUpdateCheck) await UpdateFlow.CheckAsync(silent: true);
        };
        updateTimer.Start();
    }

    public static void Stop()
    {
        if (hook != IntPtr.Zero) { UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
        if (keyHook != IntPtr.Zero) { UnhookWindowsHookEx(keyHook); keyHook = IntPtr.Zero; }
        if (messageWindow != null) { UnregisterHotKey(messageWindow.Handle, HotkeyId); messageWindow.Dispose(); messageWindow = null; }
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
        mutex?.Dispose();
        mutex = null;
    }

    private static Preferences Prefs()
    {
        // cheap freshness check so changes made in the settings window apply immediately
        try
        {
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GKRStudio", "settings.json");
            var stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
            if (stamp != prefsStamp) { prefs = Preferences.Load(); prefsStamp = stamp; }
        }
        catch (IOException) { }
        return prefs;
    }

    // ---------------- tray ----------------

    private static void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        void Build()
        {
            Prefs(); // refreshes the language
            menu.Items.Clear();
            menu.Items.Add(L.T("Panodaki linki indir"), null, (_, _) => Ui(() => MenuHost.ShowForBackground(OutputPaths.Downloads)));
            menu.Items.Add(L.T("Dosya seç…"), null, (_, _) => Ui(() => ActionFlow.Run(new MenuAction(Catalog.PickFiles, "Dosya seç", ""),
                                                                                    new WheelContext([], [], OutputPaths.Downloads, null))));
            menu.Items.Add(L.T("Kısayollar"), null, (_, _) => Ui(ShortcutsWindow.Toggle));
            menu.Items.Add(L.T("Ayarlar"), null, (_, _) => Ui(App.ShowSettings));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(L.T("Çıkış"), null, (_, _) => Ui(() => { Launcher.Resident = false; Application.Current.Shutdown(); }));
        }
        Build();
        menu.Opening += (_, _) => Build();

        tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            Text = L.T("GKR Studio — arka planda"),
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => Ui(App.ShowSettings);
        var p = Prefs();
        tray.ShowBalloonTip(2500, L.T("GKR Studio arka planda"), L.F("{0}+sağ tık: Dönüştür · {1}+sağ tık: Araçlar · {2}+sağ tık: İşlemler", p.ConvertModifier, p.ToolsModifier, p.ActionsModifier), Forms.ToolTipIcon.None);
    }

    private static void Ui(Action a) => Application.Current.Dispatcher.BeginInvoke(a);
    private static void Ui(Func<Task> a) => Application.Current.Dispatcher.BeginInvoke(a);

    // ---------------- hotkey ----------------

    private const int HotkeyId = 0x4C4D; // "LM"

    private static void CreateMessageWindow()
    {
        var parameters = new HwndSourceParameters("GKRStudioHotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) /* HWND_MESSAGE */ };
        messageWindow = new HwndSource(parameters);
        messageWindow.AddHook((IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0312 && w.ToInt32() == HotkeyId) // WM_HOTKEY
            {
                handled = true;
                if (Prefs().LinkHotkeyEnabled) MenuHost.ShowForBackground(OutputPaths.Downloads);
            }
            return IntPtr.Zero;
        });
        RegisterHotKey(messageWindow.Handle, HotkeyId, 0x0001 | 0x0002 | 0x4000 /* ALT|CTRL|NOREPEAT */, 0x4C /* L */);
    }

    // ---------------- modifier + right-click hook ----------------

    private static void InstallHook()
    {
        hookProc = HookCallback;
        hook = SetWindowsHookEx(14 /* WH_MOUSE_LL */, hookProc, GetModuleHandle(null), 0);
        keyProc = KeyCallback;
        keyHook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL */, keyProc, GetModuleHandle(null), 0);
    }

    // ---------------- 3× Ctrl / Alt / Shift → shortcuts window ----------------

    private static IntPtr keyHook;
    private static HookProc? keyProc;
    private static int tapFamily, taps;
    private static bool tapKeyDown;
    private static uint tapDownTime, lastTapTime;
    private const uint TapMaxHold = 300, TapMaxGap = 450;

    private static int Family(int vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => 0x11, // Ctrl
        0x10 or 0xA0 or 0xA1 => 0x10, // Shift
        0x12 or 0xA4 or 0xA5 => 0x12, // Alt
        _ => 0,
    };

    /// <summary>Only observes (never swallows) keys. A "tap" = quick press+release of a modifier with nothing in between.</summary>
    private static IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KeyHookInfo>(lParam);
            if ((k.Flags & 0x10) == 0) // ignore injected keys (e.g. our own Alt-menu cancel)
            {
                int msg = wParam.ToInt32();
                bool down = msg is 0x0100 or 0x0104;
                if (FunctionKey(k.VkCode) is { } fkey && Prefs().ModifierClicksEnabled && Prefs().UsesFunctionKey(fkey))
                {
                    int i = k.VkCode - 0x70;
                    if (down) { if (!fHeld[i]) { fHeld[i] = true; fUsed[i] = false; } }
                    else
                    {
                        fHeld[i] = false;
                        if (!fUsed[i]) ReplayKey((byte)k.VkCode); // no wheel opened: the key keeps its normal job (F2 = rename …)
                    }
                    taps = 0;
                    return 1; // held back while it may be a wheel key
                }
                int family = Family(k.VkCode);
                if (family == 0)
                {
                    if (down) taps = 0; // any other key (Ctrl+C, Alt+Tab…) breaks the sequence
                }
                else if (down)
                {
                    if (!(tapKeyDown && family == tapFamily)) // not auto-repeat
                    {
                        if (family != tapFamily || k.Time - lastTapTime > TapMaxGap) { tapFamily = family; taps = 0; }
                        tapKeyDown = true;
                        tapDownTime = k.Time;
                    }
                }
                else if (tapKeyDown && family == tapFamily)
                {
                    tapKeyDown = false;
                    if (k.Time - tapDownTime > TapMaxHold) taps = 0; // held, not tapped
                    else if (++taps >= 3)
                    {
                        taps = 0;
                        if (Prefs().TripleTapShortcuts) Ui(ShortcutsWindow.Toggle);
                    }
                    lastTapTime = k.Time;
                }
            }
        }
        return CallNextHookEx(keyHook, nCode, wParam, lParam);
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();
            if (msg == 0x0205 && swallowUp) // WM_RBUTTONUP belonging to a swallowed down
            {
                swallowUp = false;
                return 1; // releasing the right button does nothing: selection is by left click
            }
            if (msg is 0x0201 or 0x0204 or 0x0207) taps = 0; // a click between taps (Ctrl+click…) is not a triple tap
            if (msg == 0x0204) // WM_RBUTTONDOWN
            {
                var info = Marshal.PtrToStructure<MouseHookInfo>(lParam);
                if ((info.Flags & 0x1) == 0 && TryHandle(info.X, info.Y)) { swallowUp = true; return 1; }
            }
        }
        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    /// <summary>Runs inside the hook: must be fast. Heavy work is posted to the UI thread.</summary>
    private static bool TryHandle(int x, int y)
    {
        var p = Prefs();
        if (!p.ModifierClicksEnabled) return false;
        string chord = Chord();
        if (p.ModeFor(chord) is not { } mode) return false;

        IntPtr root = GetAncestor(WindowFromPoint(new NativePoint { X = x, Y = y }), 2 /* GA_ROOT */);
        if (!ExplorerSelection.IsExplorerWindow(root, out _)) return false;

        if (chord.Contains("Alt")) CancelAltMenu();
        for (int i = 0; i < 3; i++) if (fHeld[i]) fUsed[i] = true; // the F key opened a wheel: don't replay it on release
        int press = ++pressId;
        Ui(async () =>
        {
            try
            {
                // UI Automation + Shell COM on their own STA thread, so the mouse hook (UI thread) never stalls.
                var result = await Sta.Run(() => ExplorerSelection.Get(root, x, y));
                if (result == null || press != pressId) return;
                if (result.Items.Count > 0) MenuHost.ShowForFiles(result.Items, mode);
                else MenuHost.ShowForBackground(result.Folder);
                MenuHost.Current?.HoldWhilePressed(chord); // open while the key is held; choose with left click
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "GKR Studio", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        });
        return true;
    }

    private static int pressId;

    // F1–F3 as wheel keys: their state is tracked here because the hook holds the key presses back.
    private static readonly bool[] fHeld = new bool[3], fUsed = new bool[3];

    private static string? FunctionKey(int vk) => vk is >= 0x70 and <= 0x72 ? "F" + (vk - 0x6F) : null;

    /// <summary>True while the key is physically held (also for F1–F3, which the hook holds back).</summary>
    public static bool KeyHeld(int vk) => vk is >= 0x70 and <= 0x72 ? fHeld[vk - 0x70] : (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static void ReplayKey(byte vk) =>
        Ui(() => { keybd_event(vk, 0, 0, UIntPtr.Zero); keybd_event(vk, 0, 2 /* KEYUP */, UIntPtr.Zero); });

    private static string Chord()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        var parts = new List<string>();
        if (Down(0x11)) parts.Add("Ctrl");
        if (Down(0x10)) parts.Add("Shift");
        if (Down(0x12)) parts.Add("Alt");
        for (int i = 0; i < 3; i++)
            if (fHeld[i]) parts.Add("F" + (i + 1));
        return string.Join("+", parts);
    }

    /// <summary>Releasing Alt alone would activate Explorer's menu bar; a dummy key press cancels that.</summary>
    private static void CancelAltMenu()
    {
        keybd_event(0xE8, 0, 0, UIntPtr.Zero);
        keybd_event(0xE8, 0, 2 /* KEYUP */, UIntPtr.Zero);
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyHookInfo { public int VkCode; public int ScanCode; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookInfo { public int X; public int Y; public uint MouseData; public uint Flags; public uint Time; public UIntPtr ExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint p);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
