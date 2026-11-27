using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace GKRStudio.Core;

/// <summary>SRT ⇄ VTT, and plain text from either.</summary>
public static partial class SubtitleTools
{
    private sealed record Cue(TimeSpan Start, TimeSpan End, string Text);

    [GeneratedRegex(@"(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})\s*-->\s*(?:(\d+):)?(\d{1,2}):(\d{2})[.,](\d{1,3})")]
    private static partial Regex TimingRegex();

    public static string Convert(string input, string target)
    {
        var cues = Parse(File.ReadAllText(input));
        if (cues.Count == 0) throw new InvalidOperationException("Altyazı satırı bulunamadı.");
        string output = OutputPaths.Beside(input, target);
        var sb = new StringBuilder();
        switch (target)
        {
            case "srt":
                for (int i = 0; i < cues.Count; i++)
                    sb.Append(i + 1).Append("\r\n").Append(Ts(cues[i].Start, ',')).Append(" --> ").Append(Ts(cues[i].End, ','))
                      .Append("\r\n").Append(cues[i].Text.Replace("\n", "\r\n")).Append("\r\n\r\n");
                break;
            case "vtt":
                sb.Append("WEBVTT\n\n");
                foreach (var c in cues) sb.Append(Ts(c.Start, '.')).Append(" --> ").Append(Ts(c.End, '.')).Append('\n').Append(c.Text).Append("\n\n");
                break;
            case "txt":
                foreach (var c in cues) sb.AppendLine(Regex.Replace(c.Text, "<[^>]+>", "").Replace('\n', ' '));
                break;
        }
        File.WriteAllText(output, sb.ToString(), new UTF8Encoding(false));
        return output;
    }

    /// <summary>Rewrites every cue's text through <paramref name="map"/> (all texts at once), keeping the timings and format.</summary>
    public static string MapText(string input, Func<List<string>, List<string>> map, string suffix)
    {
        var cues = Parse(File.ReadAllText(input));
        if (cues.Count == 0) throw new InvalidOperationException("Altyazı satırı bulunamadı.");
        var texts = cues.Select(c => Regex.Replace(c.Text, "<[^>]+>", "").Replace('\n', ' ').Trim()).ToList();
        var mapped = map(texts);
        string ext = Path.GetExtension(input).TrimStart('.').ToLowerInvariant();
        string output = OutputPaths.Beside(input, ext, suffix);
        var sb = new StringBuilder();
        if (ext == "vtt")
        {
            sb.Append("WEBVTT\n\n");
            for (int i = 0; i < cues.Count; i++) sb.Append(Ts(cues[i].Start, '.')).Append(" --> ").Append(Ts(cues[i].End, '.')).Append('\n').Append(mapped[i]).Append("\n\n");
        }
        else
        {
            for (int i = 0; i < cues.Count; i++)
                sb.Append(i + 1).Append("\r\n").Append(Ts(cues[i].Start, ',')).Append(" --> ").Append(Ts(cues[i].End, ',')).Append("\r\n").Append(mapped[i]).Append("\r\n\r\n");
        }
        File.WriteAllText(output, sb.ToString(), new UTF8Encoding(false));
        return output;
    }

    private static List<Cue> Parse(string text)
    {
        var cues = new List<Cue>();
        foreach (var block in Regex.Split(text.Replace("\r\n", "\n").TrimStart('﻿'), @"\n\s*\n"))
        {
            var lines = block.Split('\n').ToList();
            int t = lines.FindIndex(l => TimingRegex().IsMatch(l));
            if (t < 0) continue;
            var m = TimingRegex().Match(lines[t]);
            string body = string.Join("\n", lines.Skip(t + 1)).Trim();
            if (body.Length == 0) continue;
            cues.Add(new Cue(Time(m, 1), Time(m, 5), body));
        }
        return cues;
    }

    private static TimeSpan Time(Match m, int g)
    {
        int h = m.Groups[g].Success ? int.Parse(m.Groups[g].Value) : 0;
        int ms = int.Parse(m.Groups[g + 3].Value.PadRight(3, '0'));
        return new TimeSpan(0, h, int.Parse(m.Groups[g + 1].Value), int.Parse(m.Groups[g + 2].Value), ms);
    }

    private static string Ts(TimeSpan t, char sep) =>
        $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}{sep}{t.Milliseconds:000}";
}
