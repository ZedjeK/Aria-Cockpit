namespace Aria.Bench;

/// <summary>Temps de chaque image d'une passe, et leur résumé (FPS moyens, 1 % les plus bas…).</summary>
public sealed class FrameStats
{
    private readonly List<double> _ms = [];

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
        return new FrameSummary(
            Frames: sorted.Length,
            Seconds: Math.Round(total / 1000, 2),
            AverageFps: Math.Round(sorted.Length * 1000 / total, 1),
            AverageMs: Math.Round(total / sorted.Length, 2),
            Low1PercentFps: Math.Round(1000 / worstAverage, 1),
            P99Ms: Math.Round(Percentile(sorted, 0.99), 2),
            MaxMs: Math.Round(sorted[^1], 2));
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

public sealed record FrameSummary(int Frames, double Seconds, double AverageFps, double AverageMs,
    double Low1PercentFps, double P99Ms, double MaxMs)
{
    public static FrameSummary Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}
