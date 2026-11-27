using System.IO;
using GKRStudio.Core;

namespace GKRStudio.UI;

/// <summary>
/// Every feature is reached through the radial wheel. Each wheel is built from the current input only
/// (file extensions, the clipboard read at this moment, current settings) and discarded after use.
/// </summary>
public static class MenuHost
{
    public static void ShowForFiles(IReadOnlyList<string> paths, Catalog.Mode mode = Catalog.Mode.All)
    {
        var prefs = Preferences.Load();
        var kind = Catalog.Classify(paths);
        var inputs = paths.Where(p => Catalog.KindOf(p) == kind).ToList();
        string first = paths[0];
        string outputDir = (Directory.Exists(first) ? Path.GetDirectoryName(first.TrimEnd('\\')) : Path.GetDirectoryName(first)) ?? OutputPaths.Downloads;
        var link = mode is Catalog.Mode.Actions or Catalog.Mode.All ? ClipboardReader.ReadLink() : null; // read NOW

        IReadOnlyList<MenuAction> actions = Catalog.ForMode(mode, kind, inputs.Count);
        if (kind is InputKind.Mixed or InputKind.Unsupported && mode is not Catalog.Mode.Actions)
            actions = mode == Catalog.Mode.All ? Catalog.ForOther() : [];

        // A copied link adds its download options to the "İşlemler" wheel.
        if (link != null)
        {
            var downloads = Catalog.ForLink(link);
            actions = mode == Catalog.Mode.Actions
                ? [.. actions, .. downloads]
                : actions.Select(a => a.Id == "group:actions" ? a with { Children = [.. a.Children!, .. downloads] } : a).ToList();
        }
        actions = prefs.Filter(actions);

        string title = kind is InputKind.Mixed or InputKind.Unsupported && mode == Catalog.Mode.All ? "GKR Studio" : Catalog.ModeTitle(mode);
        string subtitle = Catalog.Describe(kind, kind is InputKind.Mixed or InputKind.Unsupported ? paths.Count : inputs.Count);
        if (actions.Count == 0) subtitle = subtitle + "\n" + L.F("Bu tür için {0} yok", Catalog.ModeTitle(mode).ToLowerInvariant());

        var ctx = new WheelContext(inputs.Count > 0 ? inputs : paths, paths, outputDir, link);
        Show(title, subtitle, paths.Count, actions, ctx);
    }

    /// <summary>Empty-area right click: clipboard link downloads + app features.</summary>
    public static void ShowForBackground(string directory)
    {
        var link = ClipboardReader.ReadLink();
        string title = link == null ? "GKR Studio" : L.T("İndir");
        string subtitle = link == null ? L.T("Panoda link yok") : Shorten(link.Url);
        Show(title, subtitle, 0, Preferences.Load().Filter(Catalog.ForBackground(link)), new WheelContext([], [], directory, link));
    }

    /// <summary>Link wheel used by the main window.</summary>
    public static void ShowForLink(LinkInfo link, string directory) =>
        Show(L.T("İndir"), Shorten(link.Url), 0, Catalog.ForLink(link), new WheelContext([], [], directory, link));

    private static void Show(string title, string subtitle, int badge, IReadOnlyList<MenuAction> actions, WheelContext ctx)
    {
        var window = new RadialMenuWindow(title, subtitle, badge, actions, a => ActionFlow.Run(a, ctx), onSettings: App.ShowSettings,
                                          bite: Preferences.Load().BiteAnimation);
        window.Closed += (_, _) => { if (Current == window) Current = null; if (!window.Chosen) Launcher.ShutdownIfIdle(); };
        Current = window;
        window.Show();
    }

    /// <summary>The wheel opened most recently (used by hold-and-release mode).</summary>
    public static RadialMenuWindow? Current { get; private set; }

    /// <summary>Only the kind of link is shown, never the address itself.</summary>
    private static string Shorten(string url) => LinkParser.Parse(url)?.Kind switch
    {
        LinkKind.YouTube => L.T("YouTube linki"),
        LinkKind.Image => L.T("Resim linki"),
        _ => L.T("Medya linki"),
    };
}
