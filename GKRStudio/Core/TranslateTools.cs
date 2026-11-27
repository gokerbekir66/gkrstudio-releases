using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GKRStudio.Core;

/// <summary>
/// Offline translation of documents (txt/md/html/docx/rtf/epub/pdf) and subtitles (srt/vtt).
/// Missing language packs are downloaded first; sentences are translated in parallel.
/// Output keeps the format (subtitles stay subtitles, Word stays Word); PDF and RTF become .docx.
/// </summary>
public static partial class TranslateTools
{
    public static async Task<string> RunAsync(string input, string from, string to, Action<double> frac, Action<string> log, CancellationToken ct)
    {
        var route = LanguagePacks.Route(from, to)
                    ?? throw new InvalidOperationException(L.F("Bu dil çifti için dil paketi yok: {0} → {1}", LanguagePacks.LanguageName(from), LanguagePacks.LanguageName(to)));
        if (route.Count == 0) throw new InvalidOperationException("Kaynak ve hedef dil aynı.");

        var missing = route.Where(p => !p.Installed).ToList();
        double share = missing.Count > 0 ? 0.35 : 0; // part of the bar used by downloads
        for (int i = 0; i < missing.Count; i++)
        {
            int k = i;
            await LanguagePacks.InstallAsync(missing[i], f => frac(share * (k + f) / missing.Count), log, ct);
        }

        log(L.F("Çeviri: {0}", string.Join(" → ", new[] { from }.Concat(route.Select(p => p.To)).Select(LanguagePacks.LanguageName))));
        var models = new List<MarianModel>();
        try
        {
            foreach (var p in route) models.Add(new MarianModel(p.Dir));
            string One(string s) { foreach (var m in models) s = m.Translate(s); return s; }
            void Progress(double f) => frac(share + (1 - share) * f);
            string tag = $"({to.ToUpperInvariant()})";
            string ext = Path.GetExtension(input).ToLowerInvariant();

            if (ext is ".srt" or ".vtt")
                return SubtitleTools.MapText(input, texts => TranslateAll(texts, One, Progress, log, ct), tag);

            var blocks = await Sta.Run(() => DocTools.Read(input));
            if (blocks.Count == 0)
                throw new InvalidOperationException(ext == ".pdf" ? "PDF'te seçilebilir metin yok (taranmış belge olabilir)." : "Belgede metin bulunamadı.");
            log(L.F("{0} paragraf okundu.", blocks.Count));
            var translated = TranslateAll(blocks.Select(b => b.Text).ToList(), One, Progress, log, ct);
            var result = blocks.Select((b, i) => b with { Text = translated[i] }).ToList();

            string target = ext switch { ".txt" => "txt", ".md" or ".markdown" => "md", ".html" or ".htm" => "html", ".epub" => "epub", _ => "docx" };
            string output = OutputPaths.Beside(input, target, tag);
            string title = Path.GetFileNameWithoutExtension(input);
            await Sta.Run(() =>
            {
                switch (target)
                {
                    case "txt": File.WriteAllText(output, DocTools.ToText(result), new UTF8Encoding(false)); break;
                    case "md": File.WriteAllText(output, DocTools.ToMarkdown(result), new UTF8Encoding(false)); break;
                    case "html": File.WriteAllText(output, DocTools.ToHtml(result, title), new UTF8Encoding(false)); break;
                    case "epub": DocTools.WriteEpub(result, title, output); break;
                    default: DocTools.WriteDocx(result, output); break;
                }
                return 0;
            });
            return output;
        }
        finally
        {
            foreach (var m in models) m.Dispose();
        }
    }

    // ---------- helpers for the question dialog (same on Windows and macOS)

    /// <summary>Internal option values of the language choice: the Turkish names (translated for display by the dialog).</summary>
    public static readonly string[] LanguageOptions = ["Türkçe", "İngilizce", "Felemenkçe", "Fransızca", "Almanca", "İspanyolca", "Arapça"];
    public static string CodeOf(string option) => LanguagePacks.Languages[Math.Max(0, Array.IndexOf(LanguageOptions, option))];

    /// <summary>Guessed source language (from the first part of the file) and a sensible target: the app's language, else Turkish/English.</summary>
    public static async Task<(int From, int To)> DefaultsAsync(string file)
    {
        string? guess = null;
        try
        {
            var read = Task.Run(async () =>
            {
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext is ".srt" or ".vtt" or ".txt" or ".md") return File.ReadAllText(file);
                var blocks = await Sta.Run(() => DocTools.Read(file));
                return string.Join(" ", blocks.Take(60).Select(b => b.Text));
            });
            if (await Task.WhenAny(read, Task.Delay(4000)) == read) guess = LanguagePacks.Detect(await read);
        }
        catch { /* the user picks it */ }
        string from = guess ?? "en";
        string to = LanguagePacks.Languages.Contains(L.Lang) && L.Lang != from ? L.Lang : from == "tr" ? "en" : "tr";
        return (Array.IndexOf(LanguagePacks.Languages, from), Array.IndexOf(LanguagePacks.Languages, to));
    }

    /// <summary>Null when everything is installed; otherwise the confirmation text listing what will be downloaded.</summary>
    public static string? DownloadNotice(string from, string to)
    {
        var route = LanguagePacks.Route(from, to);
        if (route == null) throw new InvalidOperationException(L.F("Bu dil çifti için dil paketi yok: {0} → {1}", LanguagePacks.LanguageName(from), LanguagePacks.LanguageName(to)));
        if (route.Count == 0) throw new InvalidOperationException(L.T("Kaynak ve hedef dil aynı."));
        var missing = route.Where(p => !p.Installed).ToList();
        if (missing.Count == 0) return null;
        return L.T("Bu çeviri için dil paketi indirilecek:") + "\n\n" + string.Join("\n", missing.Select(p => $"• {p.Name}  (~{p.SizeMb} MB)"))
               + "\n\n" + L.T("Bir kez indirilir, sonra internetsiz çalışır. Devam edilsin mi?");
    }

    [GeneratedRegex(@"(?<=[.!?…])\s+(?=[\p{Lu}\d""“‘'(«¿¡])")]
    private static partial Regex SentenceEnd();

    /// <summary>Translates paragraphs sentence by sentence, several sentences at a time.</summary>
    public static List<string> TranslateAll(List<string> paragraphs, Func<string, string> translate, Action<double> frac, Action<string> log, CancellationToken ct)
    {
        var sentences = new List<(int Para, string Text)>();
        for (int i = 0; i < paragraphs.Count; i++)
            foreach (var s in SentenceEnd().Split(paragraphs[i]).Select(s => s.Trim()).Where(s => s.Length > 0))
                sentences.Add((i, s));
        log(L.F("{0} cümle çevriliyor…", sentences.Count));
        var output = new string[sentences.Count];
        int done = 0;
        Parallel.For(0, sentences.Count, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8), CancellationToken = ct }, i =>
        {
            string s = sentences[i].Text;
            // nothing to translate in numbers, page numbers, punctuation
            output[i] = s.Any(char.IsLetter) ? translate(s) : s;
            frac((double)Interlocked.Increment(ref done) / sentences.Count);
        });
        var result = Enumerable.Repeat("", paragraphs.Count).ToList();
        var sb = new StringBuilder();
        for (int p = 0, k = 0; p < paragraphs.Count; p++)
        {
            sb.Clear();
            while (k < sentences.Count && sentences[k].Para == p) { if (sb.Length > 0) sb.Append(' '); sb.Append(output[k++]); }
            result[p] = sb.ToString();
        }
        return result;
    }
}
