using System.IO;
using System.Net.Http;

namespace GKRStudio.Core;

/// <summary>
/// Offline translation language packs (OPUS-MT models in ONNX form). Nothing is installed up front:
/// each pack is downloaded only when the user picks it (Settings or the first translation that needs it).
/// Pairs without a direct pack go through English (e.g. TR → EN → NL).
/// </summary>
public static class LanguagePacks
{
    public sealed record Pack(string From, string To, string Repo, string Encoder, string Decoder, int SizeMb)
    {
        public string Id => From + "-" + To;
        public string Dir => Path.Combine(Root, Id);
        public bool Installed => File.Exists(Path.Combine(Dir, "decoder.onnx")) && File.Exists(Path.Combine(Dir, "encoder.onnx"))
                                 && File.Exists(Path.Combine(Dir, "source.spm")) && File.Exists(Path.Combine(Dir, "vocab.json"));
        public string Name => LanguageName(From) + " → " + LanguageName(To);
    }

    /// <summary>Language codes in menu order (the internal value; names are translated for display).</summary>
    public static readonly string[] Languages = ["tr", "en", "nl", "fr", "de", "es", "ar"];

    public static string LanguageName(string code) => code switch
    {
        "tr" => L.T("Türkçe"), "en" => L.T("İngilizce"), "nl" => L.T("Felemenkçe"), "fr" => L.T("Fransızca"),
        "de" => L.T("Almanca"), "es" => L.T("İspanyolca"), "ar" => L.T("Arapça"), _ => code,
    };

    private const string Q = "onnx/encoder_model_quantized.onnx", D = "onnx/decoder_model_quantized.onnx";

    public static readonly Pack[] All =
    [
        new("tr", "en", "Xenova/opus-mt-tr-en", Q, D, 110),
        new("en", "tr", "onnx-community/opus-mt-tc-big-en-tr", Q, D, 530), // the only good offline EN→TR model is the big one
        new("nl", "en", "Xenova/opus-mt-nl-en", Q, D, 115),
        new("en", "nl", "Xenova/opus-mt-en-nl", Q, D, 115),
        new("fr", "en", "Xenova/opus-mt-fr-en", Q, D, 110),
        new("en", "fr", "Xenova/opus-mt-en-fr", Q, D, 110),
        new("de", "en", "Xenova/opus-mt-de-en", Q, D, 110),
        new("en", "de", "Xenova/opus-mt-en-de", Q, D, 110),
        new("es", "en", "Xenova/opus-mt-es-en", Q, D, 110),
        new("en", "es", "Xenova/opus-mt-en-es", Q, D, 110),
        new("ar", "en", "Xenova/opus-mt-ar-en", Q, D, 110),
        new("en", "ar", "Xenova/opus-mt-en-ar", Q, D, 110),
    ];

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GKR Studio", "languages");

    public static Pack? Find(string from, string to) => All.FirstOrDefault(p => p.From == from && p.To == to);

    /// <summary>The packs needed for from → to: one direct pack, or two through English. Null when not possible.</summary>
    public static List<Pack>? Route(string from, string to)
    {
        if (from == to) return [];
        if (Find(from, to) is { } direct) return [direct];
        if (from != "en" && to != "en" && Find(from, "en") is { } a && Find("en", to) is { } b) return [a, b];
        return null;
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>Downloads a pack (resumable per file: finished files are kept). <paramref name="frac"/> goes 0 → 1.</summary>
    public static async Task InstallAsync(Pack pack, Action<double> frac, Action<string> log, CancellationToken ct)
    {
        Directory.CreateDirectory(pack.Dir);
        var files = new (string Remote, string Local)[]
        {
            ("config.json", "config.json"), ("source.spm", "source.spm"), ("target.spm", "target.spm"), ("vocab.json", "vocab.json"),
            (pack.Encoder, "encoder.onnx"), (pack.Decoder, "decoder.onnx"),
        };
        log(L.F("Dil paketi indiriliyor: {0} (~{1} MB)", pack.Name, pack.SizeMb));
        long expected = pack.SizeMb * 1_000_000L, doneBytes = 0;
        foreach (var (remote, local) in files)
        {
            string target = Path.Combine(pack.Dir, local);
            if (File.Exists(target)) { doneBytes += new FileInfo(target).Length; continue; }
            string url = $"https://huggingface.co/{pack.Repo}/resolve/main/{remote}";
            string part = target + ".part";
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, true);
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    doneBytes += read;
                    frac(Math.Min(0.99, (double)doneBytes / expected));
                }
            }
            File.Move(part, target, overwrite: true);
        }
        frac(1);
        log(L.F("Dil paketi hazır: {0}", pack.Name));
    }

    public static void Remove(Pack pack)
    {
        if (Directory.Exists(pack.Dir)) Directory.Delete(pack.Dir, recursive: true);
    }

    /// <summary>Rough language guess from common words (and the Arabic script); null when unsure.</summary>
    public static string? Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string sample = text.Length > 4000 ? text[..4000] : text;
        if (sample.Count(c => c is >= '؀' and <= 'ۿ') > sample.Length / 5) return "ar";
        var words = System.Text.RegularExpressions.Regex.Matches(sample.ToLowerInvariant(), @"\p{L}+").Select(m => m.Value).ToList();
        if (words.Count == 0) return null;
        var common = new Dictionary<string, string[]>
        {
            ["tr"] = ["ve", "bir", "bu", "için", "ile", "da", "de", "olarak", "daha", "çok", "gibi", "olan", "ama", "ne", "mi"],
            ["en"] = ["the", "and", "of", "to", "is", "in", "that", "it", "for", "with", "was", "on", "are", "as", "this"],
            ["nl"] = ["de", "het", "een", "en", "van", "is", "dat", "niet", "op", "te", "zijn", "met", "voor", "ik", "je"],
            ["fr"] = ["le", "la", "les", "et", "des", "est", "un", "une", "du", "que", "pour", "pas", "dans", "qui", "en"],
            ["de"] = ["der", "die", "und", "das", "ist", "nicht", "ein", "zu", "den", "mit", "ich", "sie", "es", "von", "auf"],
            ["es"] = ["el", "la", "de", "que", "y", "en", "los", "es", "un", "por", "con", "para", "una", "las", "del"],
        };
        var score = common.ToDictionary(kv => kv.Key, kv => words.Count(w => kv.Value.Contains(w)));
        // Turkish letters are a strong hint
        score["tr"] += sample.Count(c => "ğışçöüİĞŞ".Contains(c)) / 3;
        var best = score.OrderByDescending(kv => kv.Value).First();
        return best.Value >= 2 ? best.Key : null;
    }
}
