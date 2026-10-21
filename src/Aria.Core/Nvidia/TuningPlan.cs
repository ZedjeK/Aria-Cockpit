using Aria.Core.Performance;

namespace Aria.Core.Nvidia;

/// <summary>Objectif de l'assistant guidé.</summary>
public enum TuningGoal
{
    /// <summary>Moins de bruit et de chauffe : limite de puissance plus basse, palier par palier.</summary>
    Silence,
    /// <summary>Même fréquence avec moins de tension : plafond à la fréquence habituelle + décalage GPU croissant.</summary>
    Undervolt,
    /// <summary>Plus de fréquence : décalage GPU croissant, sans plafond.</summary>
    Performance
}

/// <summary>
/// Paliers proposés par l'assistant. Fonctions pures : le palier suivant n'est appliqué qu'après le verdict
/// de l'utilisateur sur le précédent. Les bornes restent dans celles d'Aria (±300 MHz GPU, +1000 MHz mémoire).
/// </summary>
public static class TuningPlan
{
    public const int CoreStep = 30, CoreLimit = 300, MemoryStep = 250, MemoryLimit = 1000, ClockBin = 15;
    private static readonly double[] SilenceRatios = [0.90, 0.85, 0.80, 0.75, 0.70];

    /// <summary>Le point de départ : le réglage d'origine (avant Aria), sans « au démarrage ».</summary>
    public static GpuTuningState Start(GpuTuningState origin) => origin with { AtStartup = false };

    /// <summary>Paliers de l'objectif. <paramref name="baseline"/> : mesures au départ (fréquence habituelle en jeu).</summary>
    public static IReadOnlyList<GpuTuningState> Steps(TuningGoal goal, GpuTuningState start, GpuTuningLimits limits, PerformanceStats? baseline)
    {
        switch (goal)
        {
            case TuningGoal.Silence:
                return SilenceRatios
                    .Select(r => (uint)Math.Round(limits.DefaultPowerMilliwatts * r / 5000.0) * 5000)
                    .Where(p => p >= limits.MinPowerMilliwatts && p < start.PowerLimitMilliwatts)
                    .Distinct()
                    .Select(p => start with { PowerLimitMilliwatts = p })
                    .ToList();

            case TuningGoal.Undervolt:
                if (UndervoltCap(baseline, limits) is not { } cap) return [];
                return CoreOffsets(start.CoreOffsetMHz + 2 * CoreStep, limits)
                    .Select(o => start with { MaxClockMHz = cap, CoreOffsetMHz = o })
                    .ToList();

            default:
                return CoreOffsets(start.CoreOffsetMHz + CoreStep, limits)
                    .Select(o => start with { MaxClockMHz = 0, CoreOffsetMHz = o })
                    .ToList();
        }
    }

    /// <summary>Paliers mémoire facultatifs, à partir du dernier réglage stable.</summary>
    public static IReadOnlyList<GpuTuningState> MemorySteps(GpuTuningState stable, GpuTuningLimits limits)
    {
        var max = Math.Min(MemoryLimit, limits.MaxMemoryOffsetMHz);
        var steps = new List<GpuTuningState>();
        for (var m = stable.MemoryOffsetMHz + MemoryStep; m <= max; m += MemoryStep)
            steps.Add(stable with { MemoryOffsetMHz = m });
        return steps;
    }

    /// <summary>
    /// Plafond de l'undervolt : la fréquence moyenne mesurée en jeu au départ, arrondie au cran de 15 MHz inférieur.
    /// null si la mesure de départ ne permet pas de la connaître.
    /// </summary>
    public static uint? UndervoltCap(PerformanceStats? baseline, GpuTuningLimits limits)
    {
        if (baseline is null || baseline.ActiveSeconds < PerformanceAnalyzer.MinActiveSeconds || baseline.AvgClock <= 0) return null;
        var cap = (uint)(Math.Floor(baseline.AvgClock / ClockBin) * ClockBin);
        return cap <= limits.MinClockMHz ? null : Math.Min(cap, limits.MaxClockMHz);
    }

    private static IEnumerable<int> CoreOffsets(int first, GpuTuningLimits limits)
    {
        var max = Math.Min(CoreLimit, limits.MaxCoreOffsetMHz);
        for (var o = first; o <= max; o += CoreStep) yield return o;
    }

    /// <summary>Écart lisible entre deux mesures : « -38 W », « +2 °C », « +0 MHz ».</summary>
    public static string Delta(double after, double before, string unit) => $"{after - before:+0;-0;+0} {unit}";
}
