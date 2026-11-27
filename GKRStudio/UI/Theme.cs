using System.Windows;
using System.Windows.Media;
using GKRStudio.Core;
using Microsoft.Win32;

namespace GKRStudio.UI;

/// <summary>Colours for the app windows and the wheel, from the user's settings (theme + accent).</summary>
public static class Theme
{
    public static readonly (string Name, string Hex)[] Accents =
    [
        ("Mavi", "#3A5FD9"), ("Aslan turuncusu", "#E8741E"), ("Mor", "#7B4DDB"), ("Yeşil", "#2E9E6A"),
        ("Pembe", "#D9467A"), ("Kırmızı", "#D93A3A"), ("Turkuaz", "#1FA2B8"), ("Altın", "#C9971E"),
    ];

    public static readonly string[] Themes = ["Koyu", "Açık", "Sistem"];

    public sealed record WheelColors(Color Halo, Color Segment, Color Gap, Color Hub, Color HubHot, Color Rim, Color Text, Color Muted, Color Accent);

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static Color Accent(Preferences p)
    {
        try { return C(p.AccentColor); } catch (FormatException) { return C("#3A5FD9"); }
    }

    public static bool IsDark(Preferences p) => p.Theme switch
    {
        "Açık" => false,
        "Sistem" => Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme") is not 1,
        _ => true,
    };

    public static WheelColors Wheel(Preferences p) => IsDark(p)
        ? new(Color.FromArgb(0xF0, 0x12, 0x14, 0x18), C("#24272E"), C("#121418"), C("#1B1E24"), C("#2A2E37"), Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF), Colors.White, C("#9AA3B2"), Accent(p))
        : new(Color.FromArgb(0xF4, 0xF3, 0xF4, 0xF7), C("#FFFFFF"), C("#E2E5EB"), C("#F7F8FA"), C("#ECEEF2"), Color.FromArgb(0x40, 0x00, 0x00, 0x00), C("#1B1E24"), C("#6B7280"), Accent(p));

    public static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    public static Color Darken(Color c, double amount) => Color.FromRgb((byte)(c.R * (1 - amount)), (byte)(c.G * (1 - amount)), (byte)(c.B * (1 - amount)));

    /// <summary>Replaces the app's colour resources; every window uses DynamicResource, so this applies live.</summary>
    public static void Apply(Preferences p)
    {
        var r = Application.Current.Resources;
        void Set(string key, Color color) => r[key] = new SolidColorBrush(color);
        var accent = Accent(p);
        if (IsDark(p))
        {
            Set("Bg", C("#16181D")); Set("Card", C("#1F232B")); Set("CardHover", C("#272C36")); Set("Line", C("#2F3540"));
            Set("Fg", C("#E8EAF0")); Set("Muted", C("#9AA3B2")); Set("Input", C("#12141A"));
        }
        else
        {
            Set("Bg", C("#F3F4F7")); Set("Card", C("#FFFFFF")); Set("CardHover", C("#EEF0F4")); Set("Line", C("#DCE0E7"));
            Set("Fg", C("#1B1E24")); Set("Muted", C("#5F6775")); Set("Input", C("#FFFFFF"));
        }
        Set("Accent", accent);
        Set("AccentHover", Lighten(accent, 0.2));
    }
}

/// <summary>A small icon under every wheel label, so each slice says what it does at a glance.</summary>
public static class Icons
{
    public static string For(MenuAction a)
    {
        string id = a.Id;
        string target = id[(id.IndexOf(':') + 1)..];
        switch (id)
        {
            case "group:convert": return "🔁";
            case "group:tools": return "🧰";
            case "group:actions": return "⚡";
            case Catalog.PickFiles: return "📂";
            case Catalog.OpenSettings: return "⚙";
            case "img:jpeg" or "av:compress" or "pdf:compress": return "🗜";
            case "img:strip" or "av:strip" or "pdf:strip": return "🧹";
            case "img:adjust": return "◐";
            case "img:markup": return "✏";
            case "img:frame": return "🎨";
            case "img:crop" or "av:trim": return "✂";
            case "av:crop": return "🔲";
            case "img:redact" or "av:redact": return "🙈";
            case "img:qr" or "pdf:qr": return "▦";
            case "img:ocr" or "pdf:ocr": return "🔤";
            case "pdf:ocrclean": return "✨";
            case "tr:translate": return "🌐";
            case "av:mute": return "🔇";
            case "av:speed": return "⏩";
            case "av:frame": return "📸";
            case "av:split" or "pdf:split": return "➗";
            case "av:merge" or "pdf:merge": return "🔗";
            case "av:loudnorm": return "🔊";
            case "av:visualize": return "〰";
            case "av:channels": return "🎧";
            case "av:bleep": return "📢";
            case "pdf:organize" or "pdf:pages": return "📑";
            case "arc:extract" or "zip:extract": return "📂";
            case "zip:create": return "📦";
            case "act:share": return "📤";
            case "act:float": return "🪟";
            case "act:copy": return "📋";
            case "act:move": return "🚚";
            case "act:copypath": return "📍";
            case "act:terminal": return "💻";
            case "act:reveal": return "🗂";
            case "dl:video": return "🎬";
            case "dl:audio": return "🎵";
            case "dl:thumb" or "dl:image": return "🖼";
        }
        return target switch
        {
            "png" or "jpg" or "webp" or "tiff" or "avif" or "bmp" or "ico" => "🖼",
            "gif" => id.StartsWith("av:") ? "🎞" : "🖼",
            "pdf" => "📄",
            "docx" or "txt" or "md" => "📝",
            "html" => "🌐",
            "epub" => "📖",
            "mp4" or "mov" or "mkv" or "webm" or "avi" or "wmv" => "🎬",
            "mp3" or "m4a" or "wav" or "flac" or "ogg" or "opus" or "aiff" or "wma" => "🎵",
            "xlsx" or "ods" or "csv" or "tsv" => "📊",
            "srt" or "vtt" => "💬",
            "zip" or "tar" or "gz" => "📦",
            _ => "•",
        };
    }
}
