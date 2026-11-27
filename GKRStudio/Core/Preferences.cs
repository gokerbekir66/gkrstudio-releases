using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace GKRStudio.Core;

/// <summary>User settings, stored as JSON in %APPDATA%\GKRStudio\settings.json.
/// Read fresh on every use (cheap), so a running background instance always sees the latest values.</summary>
public sealed record Preferences
{
    public static readonly string[] ModifierChoices = ["Shift", "Ctrl", "Alt", "Ctrl+Shift", "Ctrl+Alt", "Shift+Alt", "F1", "F2", "F3", "Kapalı"];

    /// <summary>F1–F3 can also open a wheel (held + right-click). True when that key is assigned to one of the wheels.</summary>
    public bool UsesFunctionKey(string key) => ConvertModifier == key || ToolsModifier == key || ActionsModifier == key;

    public string ConvertModifier { get; init; } = "Shift";
    public string ToolsModifier { get; init; } = "Ctrl";
    public string ActionsModifier { get; init; } = "Alt";
    public bool ModifierClicksEnabled { get; init; } = true;
    public bool BiteAnimation { get; init; } = true;
    public bool LinkHotkeyEnabled { get; init; } = true;   // Ctrl+Alt+L: clipboard link wheel
    public bool StartWithWindows { get; init; } = false;
    public List<string> HiddenActions { get; init; } = [];
    public string AccentColor { get; init; } = "#3A5FD9";
    public string Theme { get; init; } = "Koyu";      // Koyu | Açık | Sistem
    public bool WheelIcons { get; init; } = true;
    public bool TripleTapShortcuts { get; init; } = true; // 3× Ctrl/Alt/Shift → shortcuts window
    public string Language { get; init; } = "tr";        // tr | en | nl | fr
    public bool FirstRunDone { get; init; } = false;      // first-run setup question already answered
    public string UpdateFeed { get; init; } = "";         // empty = Updater.DefaultFeed
    public bool AutoUpdateCheck { get; init; } = true;
    public bool MacDefaults { get; init; } = false;
    /// <summary>macOS: which click opens a wheel together with the key — "Right" (two-finger / secondary click), "Left" (one-finger click) or "Both".</summary>
    public string MacWheelClick { get; init; } = "Right";
    /// <summary>Browser whose cookies yt-dlp may use (sites behind a cookie consent or a login); "" = none.</summary>
    public string CookieBrowser { get; init; } = "";
    public static readonly string[] CookieBrowsers = ["", "chrome", "edge", "firefox", "brave", "opera", "vivaldi", "safari"];
    public static string BrowserName(string id) => id switch
    {
        "chrome" => "Chrome", "edge" => "Edge", "firefox" => "Firefox", "brave" => "Brave", "opera" => "Opera", "vivaldi" => "Vivaldi", "safari" => "Safari", _ => id,
    };       // macOS: Tools wheel moved to Command once

    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GKRStudio");
    private static string FilePath => Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Preferences Load()
    {
        var p = new Preferences();
        try
        {
            if (File.Exists(FilePath))
                p = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(FilePath)) ?? p;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        L.Lang = p.Language; // every load refreshes the UI language (also in the background instance)
        return p;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, Json));
        File.Move(temp, FilePath, overwrite: true);
        L.Lang = Language;
        ApplyAutostart();
    }

    /// <summary>Which wheel a modifier chord opens ("Ctrl", "Shift", "Alt" joined with '+'), or null.</summary>
    public Catalog.Mode? ModeFor(string chord)
    {
        if (chord.Length == 0) return null;
        if (chord == ConvertModifier) return Catalog.Mode.Convert;
        if (chord == ToolsModifier) return Catalog.Mode.Tools;
        if (chord == ActionsModifier) return Catalog.Mode.Actions;
        // macOS: Control + click is the system right-click, so a wheel left on "Ctrl" is reached with Command
        if (OperatingSystem.IsMacOS() && chord == "Cmd")
        {
            if (ToolsModifier == "Ctrl") return Catalog.Mode.Tools;
            if (ConvertModifier == "Ctrl") return Catalog.Mode.Convert;
            if (ActionsModifier == "Ctrl") return Catalog.Mode.Actions;
        }
        return null;
    }

    public IReadOnlyList<MenuAction> Filter(IReadOnlyList<MenuAction> actions)
    {
        if (HiddenActions.Count == 0) return actions;
        var result = new List<MenuAction>();
        foreach (var a in actions)
        {
            if (a.IsGroup)
            {
                var kids = Filter(a.Children!);
                if (kids.Count > 0) result.Add(a with { Children = kids });
            }
            else if (!HiddenActions.Contains(a.Id)) result.Add(a);
        }
        return result;
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public void ApplyAutostart()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (StartWithWindows && Environment.ProcessPath is { } exe) key.SetValue("GKRStudio", $"\"{exe}\" --tray");
            else key.DeleteValue("GKRStudio", throwOnMissingValue: false);
        }
        else if (OperatingSystem.IsMacOS())
        {
            // ~/Library/LaunchAgents/com.gkrstudio.agent.plist: start in the menu bar at login
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
            string plist = Path.Combine(dir, "com.gkrstudio.agent.plist");
            if (StartWithWindows && Environment.ProcessPath is { } exe)
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(plist, $"""
                    <?xml version="1.0" encoding="UTF-8"?>
                    <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                    <plist version="1.0"><dict>
                      <key>Label</key><string>com.gkrstudio.agent</string>
                      <key>ProgramArguments</key><array><string>{System.Security.SecurityElement.Escape(exe)}</string><string>--tray</string></array>
                      <key>RunAtLoad</key><true/><key>AbandonProcessGroup</key><true/>
                      <key>ProcessType</key><string>Interactive</string>
                    </dict></plist>
                    """);
            }
            else if (File.Exists(plist)) File.Delete(plist);
        }
    }
}
