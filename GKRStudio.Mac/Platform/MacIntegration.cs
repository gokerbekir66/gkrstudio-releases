using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;

namespace GKRStudio.Core;

public enum RegistrationState { NotRegistered, Registered, Stale }

/// <summary>
/// Finder integration: Quick Actions (right-click → Quick Actions / Hızlı İşlemler) written as Automator service
/// workflows into ~/Library/Services. Each one just launches this app with the selected files.
/// </summary>
public static class ShellIntegration
{
    private static string ServicesDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Services");
    private static string Exe => Environment.ProcessPath ?? throw new InvalidOperationException("Uygulama yolu bulunamadı.");

    private static (string File, string Title, string Args)[] Entries =>
    [
        ("GKRStudio.workflow", "GKR Studio", "--menu"),
        ("GKR Studio - Convert.workflow", "GKR Studio — " + L.T("Dönüştür"), "--mode convert --menu"),
        ("GKR Studio - Tools.workflow", "GKR Studio — " + L.T("Araçlar"), "--mode tools --menu"),
        ("GKR Studio - Actions.workflow", "GKR Studio — " + L.T("İşlemler"), "--mode actions --menu"),
    ];

    public static void Register()
    {
        foreach (var (file, title, args) in Entries)
        {
            string contents = Path.Combine(ServicesDir, file, "Contents");
            Directory.CreateDirectory(contents);
            File.WriteAllText(Path.Combine(contents, "Info.plist"), InfoPlist(title));
            File.WriteAllText(Path.Combine(contents, "document.wflow"), Workflow($"\"{Exe}\" {args} \"$@\" >/dev/null 2>&1 &"));
        }
        Refresh();
    }

    public static void Unregister()
    {
        foreach (var (file, _, _) in Entries)
        {
            string path = Path.Combine(ServicesDir, file);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        Refresh();
    }

    public static RegistrationState State()
    {
        string doc = Path.Combine(ServicesDir, Entries[0].File, "Contents", "document.wflow");
        if (!File.Exists(doc)) return RegistrationState.NotRegistered;
        return File.ReadAllText(doc).Contains(SecurityElement.Escape(Exe)) ? RegistrationState.Registered : RegistrationState.Stale;
    }

    private static void Refresh()
    {
        try { Process.Start("/System/Library/CoreServices/pbs", "-update")?.WaitForExit(5000); } catch { }
    }

    private static string InfoPlist(string title) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>NSServices</key>
          <array>
            <dict>
              <key>NSBackgroundColorName</key><string>background</string>
              <key>NSIconName</key><string>NSActionTemplate</string>
              <key>NSMenuItem</key><dict><key>default</key><string>{SecurityElement.Escape(title)}</string></dict>
              <key>NSMessage</key><string>runWorkflowAsService</string>
              <key>NSRequiredContext</key><dict><key>NSApplicationIdentifier</key><string>com.apple.finder</string></dict>
              <key>NSSendFileTypes</key><array><string>public.item</string></array>
            </dict>
          </array>
        </dict>
        </plist>
        """;

    private static string Workflow(string command) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
          <key>AMApplicationBuild</key><string>523</string>
          <key>AMApplicationVersion</key><string>2.10</string>
          <key>AMDocumentVersion</key><string>2</string>
          <key>actions</key>
          <array>
            <dict>
              <key>action</key>
              <dict>
                <key>AMAccepts</key>
                <dict><key>Container</key><string>List</string><key>Optional</key><true/><key>Types</key><array><string>com.apple.cocoa.path</string></array></dict>
                <key>AMActionVersion</key><string>2.0.3</string>
                <key>AMApplication</key><array><string>Automator</string></array>
                <key>AMParameterProperties</key>
                <dict><key>COMMAND_STRING</key><dict/><key>CheckedForUserDefaultShell</key><dict/><key>inputMethod</key><dict/><key>shell</key><dict/><key>source</key><dict/></dict>
                <key>AMProvides</key>
                <dict><key>Container</key><string>List</string><key>Types</key><array><string>com.apple.cocoa.string</string></array></dict>
                <key>ActionBundlePath</key><string>/System/Library/Automator/Run Shell Script.action</string>
                <key>ActionName</key><string>Run Shell Script</string>
                <key>ActionParameters</key>
                <dict>
                  <key>COMMAND_STRING</key><string>{SecurityElement.Escape(command)}</string>
                  <key>CheckedForUserDefaultShell</key><true/>
                  <key>inputMethod</key><integer>1</integer>
                  <key>shell</key><string>/bin/bash</string>
                  <key>source</key><string></string>
                </dict>
                <key>BundleIdentifier</key><string>com.apple.RunShellScript</string>
                <key>CFBundleVersion</key><string>2.0.3</string>
                <key>CanShowSelectedItemsWhenRun</key><false/>
                <key>CanShowWhenRun</key><true/>
                <key>Category</key><array><string>AMCategoryUtilities</string></array>
                <key>Class Name</key><string>RunShellScriptAction</string>
                <key>InputUUID</key><string>{Guid.NewGuid().ToString().ToUpperInvariant()}</string>
                <key>Keywords</key><array><string>Shell</string><string>Script</string></array>
                <key>OutputUUID</key><string>{Guid.NewGuid().ToString().ToUpperInvariant()}</string>
                <key>UUID</key><string>{Guid.NewGuid().ToString().ToUpperInvariant()}</string>
                <key>UnlocalizedApplications</key><array><string>Automator</string></array>
                <key>arguments</key><dict/>
                <key>isViewVisible</key><integer>1</integer>
                <key>location</key><string>309.000000:253.000000</string>
              </dict>
              <key>isViewVisible</key><integer>1</integer>
            </dict>
          </array>
          <key>connectors</key><dict/>
          <key>workflowMetaData</key>
          <dict>
            <key>applicationBundleIDsByPath</key><dict/>
            <key>applicationPaths</key><array/>
            <key>inputTypeIdentifier</key><string>com.apple.Automator.fileSystemObject</string>
            <key>outputTypeIdentifier</key><string>com.apple.Automator.nothing</string>
            <key>presentationMode</key><integer>15</integer>
            <key>processesInput</key><false/>
            <key>serviceApplicationBundleID</key><string>com.apple.finder</string>
            <key>serviceApplicationPath</key><string>/System/Library/CoreServices/Finder.app</string>
            <key>serviceInputTypeIdentifier</key><string>com.apple.Automator.fileSystemObject</string>
            <key>serviceOutputTypeIdentifier</key><string>com.apple.Automator.nothing</string>
            <key>serviceProcessesInput</key><false/>
            <key>systemImageName</key><string>NSActionTemplate</string>
            <key>useAutomaticInputType</key><false/>
            <key>workflowTypeIdentifier</key><string>com.apple.Automator.servicesMenu</string>
          </dict>
        </dict>
        </plist>
        """;
}

/// <summary>Finder selection via AppleScript (asks once for "Automation → Finder" permission).</summary>
public static class FinderSelection
{
    public sealed class NotAuthorizedException() : Exception("Not authorized to send Apple events to Finder (-1743).");

    public static (string? Folder, List<string> Items) Get()
    {
        const string script = """
            tell application "Finder"
              set out to ""
              try
                set out to (POSIX path of (insertion location as alias)) & linefeed
              on error
                set out to (POSIX path of (path to desktop)) & linefeed
              end try
              repeat with f in (selection as alias list)
                set out to out & (POSIX path of f) & linefeed
              end repeat
              return out
            end tell
            """;
        var psi = new ProcessStartInfo("/usr/bin/osascript") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi)!;
        var errTask = p.StandardError.ReadToEndAsync();
        var lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToList();
        p.WaitForExit(5000);
        string err = errTask.Wait(1000) ? errTask.Result : "";
        if (err.Length > 0) GKRStudio.Mac.Program.Log("finder selection osascript: " + err.Trim());
        if (err.Contains("-1743") || err.Contains("Not authorized", StringComparison.OrdinalIgnoreCase)) throw new NotAuthorizedException();
        if (lines.Count == 0) return (null, []);
        return (lines[0].TrimEnd('/'), lines.Skip(1).Select(l => l.TrimEnd('/')).Where(l => File.Exists(l) || Directory.Exists(l)).ToList());
    }
}

/// <summary>
/// Low-level macOS input via CoreGraphics event taps (needs Privacy → Accessibility permission):
/// modifier + right-click in Finder, Ctrl+Option+L, triple-tapping a modifier. Plus cursor/frontmost helpers.
/// </summary>
public static class MacInput
{
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AS = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    public const ulong FlagShift = 0x20000, FlagControl = 0x40000, FlagOption = 0x80000, FlagCommand = 0x100000;

    [StructLayout(LayoutKind.Sequential)] public struct CGPoint { public double X; public double Y; }

    public delegate IntPtr TapCallback(IntPtr proxy, int type, IntPtr evt, IntPtr info);

    [DllImport(CG)] private static extern IntPtr CGEventTapCreate(int tap, int place, int options, ulong mask, TapCallback callback, IntPtr info);
    [DllImport(CG)] private static extern void CGEventTapEnable(IntPtr tap, bool enable);
    [DllImport(CG)] public static extern ulong CGEventGetFlags(IntPtr evt);
    [DllImport(CG)] public static extern CGPoint CGEventGetLocation(IntPtr evt);
    [DllImport(CG)] public static extern long CGEventGetIntegerValueField(IntPtr evt, int field);
    [DllImport(CG)] public static extern ulong CGEventGetTimestamp(IntPtr evt);
    [DllImport(CG)] private static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CG)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, int type, CGPoint pos, int button);
    [DllImport(CG)] private static extern void CGEventSetFlags(IntPtr evt, ulong flags);
    [DllImport(CG)] private static extern void CGEventPost(int tap, IntPtr evt);
    [DllImport(CG)] private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort key, bool down);

    /// <summary>Posts a key press + release (used to give back an F key that did not open a wheel).</summary>
    public static void PostKey(ushort keycode)
    {
        foreach (bool down in new[] { true, false })
        {
            IntPtr e = CGEventCreateKeyboardEvent(IntPtr.Zero, keycode, down);
            CGEventPost(0 /* kCGHIDEventTap */, e);
            CFRelease(e);
        }
    }
    [DllImport(CG)] public static extern ulong CGEventSourceFlagsState(int state);
    [DllImport(CF)] private static extern void CFRelease(IntPtr obj);
    [DllImport(CF)] private static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr alloc, IntPtr port, long order);
    [DllImport(CF)] private static extern IntPtr CFRunLoopGetCurrent();
    [DllImport(CF)] private static extern void CFRunLoopAddSource(IntPtr rl, IntPtr source, IntPtr mode);
    [DllImport(CF)] private static extern void CFRunLoopRun();
    [DllImport(AS)] public static extern bool AXIsProcessTrusted();
    [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlopen(string path, int mode);
    [DllImport("/usr/lib/libSystem.dylib")] private static extern IntPtr dlsym(IntPtr handle, string symbol);
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    private static IntPtr tap;
    private static TapCallback? keepAlive;

    /// <summary>Current global cursor position in points (top-left origin).</summary>
    public static CGPoint Cursor()
    {
        IntPtr e = CGEventCreate(IntPtr.Zero);
        var p = CGEventGetLocation(e);
        CFRelease(e);
        return p;
    }

    public static ulong CurrentFlags() => CGEventSourceFlagsState(0 /* combined session state */);

    /// <summary>Bundle id of the frontmost app (e.g. com.apple.finder).</summary>
    public static string? FrontmostBundleId()
    {
        try
        {
            IntPtr ws = Send(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
            IntPtr app = Send(ws, sel_registerName("frontmostApplication"));
            IntPtr id = Send(app, sel_registerName("bundleIdentifier"));
            IntPtr utf8 = Send(id, sel_registerName("UTF8String"));
            return Marshal.PtrToStringUTF8(utf8);
        }
        catch { return null; }
    }

    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendInt(IntPtr receiver, IntPtr selector, int arg);
    [DllImport(CG)] private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
    [DllImport(CG)] private static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dict, out CGRect rect);
    [DllImport(CF)] private static extern long CFArrayGetCount(IntPtr array);
    [DllImport(CF)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);
    [DllImport(CF)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
    [DllImport(CF)] private static extern bool CFNumberGetValue(IntPtr number, int type, out double value);
    [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string s, uint encoding);
    [StructLayout(LayoutKind.Sequential)] public struct CGRect { public double X, Y, W, H; }

    private static IntPtr keyPid, keyLayer, keyBounds, keyAlpha;
    private static IntPtr Key(ref IntPtr cache, string name) =>
        cache != IntPtr.Zero ? cache : (cache = CFStringCreateWithCString(IntPtr.Zero, name, 0x08000100 /* UTF-8 */));

    private static double Num(IntPtr dict, IntPtr key, double fallback)
    {
        IntPtr v = CFDictionaryGetValue(dict, key);
        return v != IntPtr.Zero && CFNumberGetValue(v, 13 /* kCFNumberDoubleType */, out double d) ? d : fallback;
    }

    /// <summary>Process id of the topmost on-screen window under the point (Desktop icons included), or 0.</summary>
    public static int PidAt(CGPoint at)
    {
        IntPtr list = CGWindowListCopyWindowInfo(1 /* kCGWindowListOptionOnScreenOnly */, 0);
        if (list == IntPtr.Zero) return 0;
        try
        {
            long n = CFArrayGetCount(list);
            for (long i = 0; i < n; i++) // front to back
            {
                IntPtr w = CFArrayGetValueAtIndex(list, i);
                if (Num(w, Key(ref keyLayer, "kCGWindowLayer"), 0) > 0) continue; // menu bar, Dock, overlays
                if (Num(w, Key(ref keyAlpha, "kCGWindowAlpha"), 1) <= 0) continue;
                IntPtr b = CFDictionaryGetValue(w, Key(ref keyBounds, "kCGWindowBounds"));
                if (b == IntPtr.Zero || !CGRectMakeWithDictionaryRepresentation(b, out var r)) continue;
                if (at.X < r.X || at.Y < r.Y || at.X >= r.X + r.W || at.Y >= r.Y + r.H) continue;
                return (int)Num(w, Key(ref keyPid, "kCGWindowOwnerPID"), 0);
            }
            return 0;
        }
        finally { CFRelease(list); }
    }

    /// <summary>
    /// Bundle id of the app whose window is under the point. The frontmost app is not enough: a click on the Desktop
    /// or on a Finder window behind another app arrives before Finder becomes frontmost.
    /// Uses the window list, not an Accessibility hit-test: an AX hit-test on one of our own (Avalonia) windows is
    /// answered in-process on the event-tap thread and crashes inside Avalonia.
    /// </summary>
    public static string? BundleIdAt(CGPoint at)
    {
        try
        {
            int pid = PidAt(at);
            if (pid <= 0) return null;
            if (pid == Environment.ProcessId) return "com.gkrstudio.app";
            IntPtr app = SendInt(objc_getClass("NSRunningApplication"), sel_registerName("runningApplicationWithProcessIdentifier:"), pid);
            if (app == IntPtr.Zero) return null;
            IntPtr id = Send(app, sel_registerName("bundleIdentifier"));
            return id == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(Send(id, sel_registerName("UTF8String")));
        }
        catch { return null; }
    }

    /// <summary>Clicks (left, no modifiers) at a point so Finder selects the item under the cursor, like a real right-click would.</summary>
    public static void SelectUnderCursor(CGPoint at)
    {
        foreach (int type in new[] { 1 /* left down */, 2 /* left up */ })
        {
            IntPtr e = CGEventCreateMouseEvent(IntPtr.Zero, type, at, 0);
            CGEventSetFlags(e, 0);
            CGEventPost(0 /* kCGHIDEventTap */, e);
            CFRelease(e);
        }
    }

    public static void OpenAccessibilitySettings() => OpenSettingsPane("Privacy_Accessibility");

    public static void OpenSettingsPane(string anchor) =>
        Process.Start("/usr/bin/open", "x-apple.systempreferences:com.apple.preference.security?" + anchor);

    [DllImport(AS)] private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);
    [DllImport(CF)] private static extern IntPtr CFDictionaryCreate(IntPtr alloc, IntPtr[] keys, IntPtr[] values, long count, IntPtr keyCallBacks, IntPtr valueCallBacks);

    /// <summary>
    /// Asks macOS to show its own "GKR Studio would like to control this computer" prompt, which also adds
    /// GKR Studio to System Settings → Privacy &amp; Security → Accessibility (so the user only flips the switch).
    /// </summary>
    public static bool RequestAccessibility()
    {
        try
        {
            IntPtr cf = dlopen(CF, 1), ax = dlopen(AS, 1);
            IntPtr key = Marshal.ReadIntPtr(dlsym(ax, "kAXTrustedCheckOptionPrompt"));
            IntPtr yes = Marshal.ReadIntPtr(dlsym(cf, "kCFBooleanTrue"));
            IntPtr dict = CFDictionaryCreate(IntPtr.Zero, [key], [yes], 1, dlsym(cf, "kCFTypeDictionaryKeyCallBacks"), dlsym(cf, "kCFTypeDictionaryValueCallBacks"));
            bool trusted = AXIsProcessTrustedWithOptions(dict);
            CFRelease(dict);
            return trusted;
        }
        catch { return AXIsProcessTrusted(); }
    }

    /// <summary>Starts the event tap on its own thread. Returns false without Accessibility permission.</summary>
    public static bool Start(TapCallback callback)
    {
        if (!OperatingSystem.IsMacOS()) return false;
        keepAlive = callback;
        ulong mask = (1UL << 1) | (1UL << 2) | (1UL << 3) | (1UL << 4) | (1UL << 10) | (1UL << 11) | (1UL << 12) | (1UL << 25); // left down, right down/up, key down, flags changed
        var ready = new ManualResetEventSlim();
        bool ok = false;
        var thread = new Thread(() =>
        {
            tap = CGEventTapCreate(1 /* session */, 0 /* head insert */, 0 /* default (can modify) */, mask, keepAlive, IntPtr.Zero);
            ok = tap != IntPtr.Zero;
            ready.Set();
            if (!ok) return;
            IntPtr source = CFMachPortCreateRunLoopSource(IntPtr.Zero, tap, 0);
            IntPtr cfLib = dlopen(CF, 1);
            IntPtr commonModes = Marshal.ReadIntPtr(dlsym(cfLib, "kCFRunLoopCommonModes"));
            CFRunLoopAddSource(CFRunLoopGetCurrent(), source, commonModes);
            CGEventTapEnable(tap, true);
            CFRunLoopRun();
        }) { IsBackground = true, Name = "GKR Studio event tap" };
        thread.Start();
        ready.Wait(3000);
        return ok;
    }

    /// <summary>macOS disables slow taps; call when the callback receives type 0xFFFFFFFE/0xFFFFFFFF.</summary>
    public static void ReEnable()
    {
        if (tap != IntPtr.Zero) CGEventTapEnable(tap, true);
    }
}
