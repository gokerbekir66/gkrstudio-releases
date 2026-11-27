using System.IO;
using System.Text.RegularExpressions;

namespace GKRStudio.Core;

public static partial class LinkParser
{
    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"(?:^|[?&])(?:format|fm|ext)=(jpe?g|png|webp|gif|avif|bmp)(?:&|$)", RegexOptions.IgnoreCase)]
    private static partial Regex ImageQueryRegex();

    private static readonly string[] YouTubeHosts =
        ["youtube.com", "www.youtube.com", "m.youtube.com", "music.youtube.com", "youtu.be", "youtube-nocookie.com", "www.youtube-nocookie.com"];

    /// <summary>Parses the first http(s) link in <paramref name="text"/>. Pure function, no hidden inputs.</summary>
    public static LinkInfo? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 32768) return null;
        var match = UrlRegex().Match(text);
        if (!match.Success) return null;

        string candidate = match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '!', '?');
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)) return null;
        if (uri.IsLoopback || !uri.Host.Contains('.')) return null;

        string host = uri.Host.ToLowerInvariant();
        if (YouTubeHosts.Contains(host)) return new LinkInfo(uri.AbsoluteUri, LinkKind.YouTube);

        string ext = Path.GetExtension(uri.AbsolutePath);
        if (Catalog.ImageExtensions.Contains(ext) || ImageQueryRegex().IsMatch(uri.Query.TrimStart('?')))
            return new LinkInfo(uri.AbsoluteUri, LinkKind.Image);

        return new LinkInfo(uri.AbsoluteUri, LinkKind.Media);
    }
}
