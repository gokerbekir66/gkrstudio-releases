using System.Diagnostics;

namespace GKRStudio.Core;

/// <summary>Percentage and estimated time left for a progress window ("%42 · about 1 min 20 s left").</summary>
public sealed class Eta
{
    private readonly Stopwatch watch = Stopwatch.StartNew();
    private double fraction;
    private TimeSpan reportedAt;

    public void Report(double f)
    {
        fraction = Math.Clamp(f, 0, 1);
        reportedAt = watch.Elapsed;
    }

    /// <summary>Call on every report and once a second, so the time counts down between reports.</summary>
    public string Text()
    {
        if (fraction <= 0) return "";
        string pct = L.F("%{0}", (int)Math.Floor(fraction * 100));
        if (fraction >= 1) return pct;
        if (fraction < 0.01 || reportedAt.TotalSeconds < 3) return pct + " · " + L.T("kalan süre hesaplanıyor…");
        var left = reportedAt * ((1 - fraction) / fraction) - (watch.Elapsed - reportedAt);
        if (left < TimeSpan.FromSeconds(1)) left = TimeSpan.FromSeconds(1);
        return pct + " · " + L.F("yaklaşık {0} kaldı", Format(left));
    }

    private static string Format(TimeSpan t)
    {
        if (t.TotalHours >= 1) return L.F("{0} sa {1} dk", (int)t.TotalHours, t.Minutes);
        if (t.TotalMinutes >= 1) return L.F("{0} dk {1} sn", t.Minutes, t.Seconds / 5 * 5); // 5-second steps: less jitter
        return L.F("{0} sn", Math.Max(1, (int)Math.Ceiling(t.TotalSeconds)));
    }
}
