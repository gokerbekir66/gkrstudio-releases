using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;

namespace GKRStudio.UI;

/// <summary>
/// Reads what the user right-clicked in Explorer / on the desktop, at click time (no caching):
/// the item under the cursor if any (selecting it, like Explorer would), else the current selection;
/// right-clicking empty space returns no items (= background).
/// </summary>
public static class ExplorerSelection
{
    public sealed record Result(string Folder, IReadOnlyList<string> Items);

    public static bool IsExplorerWindow(IntPtr root, out bool desktop)
    {
        string cls = ClassName(root);
        desktop = cls is "Progman" or "WorkerW";
        return desktop || cls is "CabinetWClass" or "ExploreWClass";
    }

    public static Result? Get(IntPtr root, int x, int y)
    {
        IsExplorerWindow(root, out bool desktop);
        dynamic? view = desktop ? DesktopView() : FolderView(root);
        if (view == null) return null;

        string folder;
        try { folder = (string)view.Folder.Self.Path; }
        catch { return null; }

        // 1) what is under the cursor?
        string? nameUnder = ItemNameAt(x, y, out bool onItem);
        if (!onItem) return new Result(folder, []); // empty space → background wheel

        var selected = new List<string>();
        var selectedNames = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        try
        {
            foreach (dynamic item in view.SelectedItems())
            {
                selected.Add((string)item.Path);
                selectedNames.Add((string)item.Name);
            }
        }
        catch (COMException) { }

        // 2) item under cursor not part of the selection → select just that one (Explorer's behaviour)
        if (nameUnder != null && !selectedNames.Contains(nameUnder))
        {
            try
            {
                dynamic? match = null;
                int hits = 0;
                foreach (dynamic item in view.Folder.Items())
                {
                    if (string.Equals((string)item.Name, nameUnder, StringComparison.CurrentCultureIgnoreCase)) { match = item; hits++; }
                }
                if (hits == 1 && match != null)
                {
                    view.SelectItem(match, 1 | 4 | 8 | 16); // select | deselect others | ensure visible | focus
                    string path = match!.Path;
                    return new Result(folder, [path]);
                }
            }
            catch (COMException) { }
        }
        return new Result(folder, selected);
    }

    /// <summary>UI Automation: name of the list item under the point (Explorer list/grid or desktop icon).</summary>
    private static string? ItemNameAt(int x, int y, out bool onItem)
    {
        onItem = true; // unknown → assume item, fall back to the selection
        try
        {
            var el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            for (int i = 0; i < 6 && el != null; i++)
            {
                var type = el.Current.ControlType;
                if (type == ControlType.ListItem || type == ControlType.DataItem) return el.Current.Name;
                if (type == ControlType.List || type == ControlType.DataGrid || type == ControlType.Pane && el.Current.ClassName == "SHELLDLL_DefView")
                {
                    onItem = false;
                    return null;
                }
                el = TreeWalker.ControlViewWalker.GetParent(el);
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException) { }
        return null;
    }

    private static dynamic? FolderView(IntPtr root)
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType == null) return null;
        dynamic shell = Activator.CreateInstance(shellType)!;
        string title = WindowText(root);
        dynamic? fallback = null;
        foreach (dynamic w in shell.Windows())
        {
            try
            {
                if ((long)w.HWND != root.ToInt64()) continue;
                // Windows 11 tabs share one HWND: prefer the tab whose name matches the window title (= active tab).
                if (title.StartsWith((string)w.LocationName, StringComparison.CurrentCultureIgnoreCase)) return w.Document;
                fallback ??= w.Document;
            }
            catch (COMException) { }
            catch (Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        }
        return fallback;
    }

    private static dynamic? DesktopView()
    {
        try
        {
            var windows = (IShellWindows)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"))!)!;
            object loc = 0; // CSIDL_DESKTOP
            object root = Type.Missing;
            int hr = windows.FindWindowSW(ref loc, ref root, 8 /* SWC_DESKTOP */, out _, 1 /* SWFO_NEEDDISPATCH */, out object disp);
            if (hr != 0 || disp == null) return null;
            dynamic browser = disp;
            return browser.Document;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException) { return null; }
    }

    private static string ClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string WindowText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        int Count { get; }
        [return: MarshalAs(UnmanagedType.IDispatch)] object Item(object index);
        [return: MarshalAs(UnmanagedType.IUnknown)] object _NewEnum();
        void Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int swClass, out int pdwCookie);
        void RegisterPending(int lThreadId, ref object pvarloc, ref object pvarlocRoot, int swClass, out int pdwCookie);
        void Revoke(int lCookie);
        void OnNavigate(int lCookie, ref object pvarLoc);
        void OnActivated(int lCookie, bool fActive);
        [PreserveSig]
        int FindWindowSW(ref object pvarLoc, ref object pvarLocRoot, int swClass, out int pHWND, int swfwOptions,
                         [MarshalAs(UnmanagedType.IDispatch)] out object ppdispOut);
    }
}
