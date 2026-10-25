namespace Aria.Bench;

/// <summary>Temps de chaque image d'une passe, et leur résumé (FPS moyens, 1 % les plus bas…).</summary>
public sealed class FrameStats
{
    private readonly List<double> _ms = [];
    public const double StutterFactor = 2.5, StutterMinMs = 20;

    public int Count => _ms.Count;

    public void Add(double milliseconds)
    {
        if (double.IsFinite(milliseconds) && milliseconds > 0) _ms.Add(milliseconds);
    }

    public FrameSummary Summarize()
    {
        if (_ms.Count == 0) return FrameSummary.Empty;
        var total = _ms.Sum();
        var sorted = _ms.Order().ToArray();
        // « 1 % les plus bas » : la moyenne des 1 % d'images les plus lentes, convertie en FPS
        var worstCount = Math.Max(1, (int)Math.Ceiling(sorted.Length * 0.01));
        var worstAverage = sorted.TakeLast(worstCount).Average();
        var median = Percentile(sorted, 0.5);
        return new FrameSummary(
            Frames: sorted.Length,
            Seconds: Math.Round(total / 1000, 2),
            AverageFps: Math.Round(sorted.Length * 1000 / total, 1),
            AverageMs: Math.Round(total / sorted.Length, 2),
            MedianFps: Math.Round(1000 / median, 1),
            Low1PercentFps: Math.Round(1000 / worstAverage, 1),
            P99Ms: Math.Round(Percentile(sorted, 0.99), 2),
            MaxMs: Math.Round(sorted[^1], 2),
            // Saccade : une image plus de 2,5 fois plus longue que la normale (et au moins 20 ms), visible à l'œil
            Stutters: sorted.Count(ms => ms > Math.Max(median * StutterFactor, StutterMinMs)));
    }

    /// <summary>Centile par interpolation linéaire sur une liste triée.</summary>
    internal static double Percentile(double[] sorted, double fraction)
    {
        if (sorted.Length == 1) return sorted[0];
        var position = fraction * (sorted.Length - 1);
        var low = (int)Math.Floor(position);
        var high = Math.Min(low + 1, sorted.Length - 1);
        return sorted[low] + (sorted[high] - sorted[low]) * (position - low);
    }
}

public sealed record FrameSummary(int Frames, double Seconds, double AverageFps, double AverageMs, double MedianFps,
    double Low1PercentFps, double P99Ms, double MaxMs, int Stutters)
{
    public static FrameSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, 0);
}

/// <summary>
/// Repère une mesure bridée : FPS médians collés à une limite connue (fréquence de l'écran, limite du pilote…).
/// Dans ce cas la carte n'est pas mesurée à fond et le chiffre ne dit rien de ses performances.
/// </summary>
public static class FrameCap
{
    public const double Tolerance = 0.015;

    /// <returns>Explication en français, ou null si rien n'indique une limite.</returns>
    public static string? Detect(double medianFps, int refreshHz, bool onBattery)
    {
        if (medianFps <= 0) return null;
        var candidates = new List<(double Fps, string Why)>();
        if (refreshHz > 0)
        {
            candidates.Add((refreshHz, "la fréquence de l'écran (V-Sync)"));
            if (refreshHz >= 60) candidates.Add((refreshHz - 3, "la fréquence de l'écran − 3 (limite d'images du pilote, réglée avec G-SYNC)"));
            candidates.Add((refreshHz / 2.0, "la moitié de la fréquence de l'écran (V-Sync)"));
        }
        candidates.Add((60, "60 (limite d'images ou écran à 60 Hz)"));
        candidates.Add((30, "30 (limite d'images ou économie d'énergie)"));

        foreach (var (fps, why) in candidates)
        {
            if (Math.Abs(medianFps - fps) / fps > Tolerance) continue;
            var text = $"FPS plafonnés à environ {Math.Round(fps)} : {why}. La carte n'est pas mesurée à fond.";
            return onBattery ? text + " Le PC est sur batterie : branchez-le sur secteur." : text;
        }
        return null;
    }
}
