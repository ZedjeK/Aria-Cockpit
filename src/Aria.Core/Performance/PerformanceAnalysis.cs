using System.Globalization;
using Aria.Core.Models;
using Aria.Core.Nvidia;

namespace Aria.Core.Performance;

/// <summary>Une seconde de l'analyse : la carte graphique et, si mesurable, le processeur.</summary>
public sealed record PerfSample(GpuSample Gpu, CpuLoad? Cpu);

/// <summary>Moyennes et maximums sur les secondes où la carte travaillait vraiment.</summary>
public sealed record PerformanceStats(
    int Seconds,
    int ActiveSeconds,
    double AvgLoad,
    double AvgTemp,
    uint MaxTemp,
    double AvgClock,
    uint MaxClock,
    double? AvgPower,
    double? PowerLimit,
    ulong? MaxVramBytes,
    double? MaxVramRatio,
    uint? MaxFan,
    double PowerCapShare,
    double ThermalShare,
    double EmergencyShare,
    double? AvgCpuTotal,
    double? AvgCpuBusiestCore);

public sealed record PerformanceFinding(string Title, string Detail, string? Advice, Severity Severity);

/// <param name="Conclusive">false si la carte n'a pas assez travaillé pendant l'analyse pour juger.</param>
public sealed record PerformanceReport(DateTime At, PerformanceStats Stats, bool Conclusive, string Headline, IReadOnlyList<PerformanceFinding> Findings);

/// <summary>
/// Transforme les mesures d'une session en verdict. Fonction pure (testable) : ne lit ni ne modifie rien.
/// Les seuils sont volontairement prudents : on décrit ce qui a été mesuré, sans promettre de gain.
/// </summary>
public static class PerformanceAnalyzer
{
    /// <summary>Charge GPU à partir de laquelle une seconde compte comme « la carte travaille ».</summary>
    public const uint ActiveLoadPercent = 30;
    /// <summary>Secondes de travail minimum pour un verdict.</summary>
    public const int MinActiveSeconds = 30;

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static PerformanceReport Analyze(IReadOnlyList<PerfSample> samples, DateTime at)
    {
        var active = samples.Where(s => s.Gpu.LoadPercent is { } load && load >= ActiveLoadPercent
                                        && !Has(s, GpuLimitReasons.Idle)).ToList();
        var stats = Stats(samples.Count, active);

        if (active.Count < MinActiveSeconds)
        {
            return new PerformanceReport(at, stats, false,
                "Analyse non concluante : la carte graphique n'a pas assez travaillé",
                [
                    new("Pas assez de charge pour juger",
                        $"La carte a travaillé {active.Count} s sur {samples.Count} s (il en faut au moins {MinActiveSeconds}).",
                        "Relancez l'analyse puis, tout de suite, lancez un jeu exigeant ou un benchmark (3DMark, Unigine, le benchmark intégré d'un jeu) et laissez-le tourner jusqu'à la fin.",
                        Severity.Info)
                ]);
        }

        var findings = new List<PerformanceFinding>();

        // 1. Ralentissements d'urgence (fréquence divisée par 2 ou plus)
        if (stats.EmergencyShare >= 0.05)
            findings.Add(new("Ralentissements matériels d'urgence",
                $"Pendant {Pct(stats.EmergencyShare)} du temps de jeu, la carte a divisé sa fréquence par 2 ou plus. " +
                "Ce frein se déclenche sur une alimentation insuffisante ou mal branchée, ou une chauffe extrême.",
                "Vérifiez que les câbles d'alimentation de la carte sont enfoncés à fond (surtout un connecteur 12V-2x6/12VHPWR), " +
                "sans rallonge ni adaptateur douteux, et que l'alimentation a la puissance recommandée par le fabricant de la carte.",
                Severity.Critical));

        // 2. Température
        var thermal = stats.ThermalShare >= 0.05 || stats.MaxTemp >= GpuTelemetry.HotTemperatureC;
        if (thermal)
            findings.Add(new("Limitée par la température",
                $"Jusqu'à {stats.MaxTemp} °C (moyenne {stats.AvgTemp:0} °C)" +
                (stats.ThermalShare > 0 ? $", fréquence réduite pour cause de chaleur pendant {Pct(stats.ThermalShare)} du temps." : "."),
                "Dépoussiérez la carte et les filtres, vérifiez le flux d'air du boîtier (entrées à l'avant, sortie à l'arrière et en haut) " +
                "et la courbe des ventilateurs. Baisser la limite de puissance réduit aussi la chauffe pour une perte de performance faible.",
                Severity.Warning));
        else if (stats.EmergencyShare < 0.05)
            findings.Add(new("Aucune surchauffe",
                $"Maximum {stats.MaxTemp} °C, moyenne {stats.AvgTemp:0} °C : le refroidissement suit.", null, Severity.Ok));

        // 3. Mémoire vidéo
        if (stats.MaxVramRatio >= 0.95)
            findings.Add(new("Mémoire vidéo saturée",
                $"Jusqu'à {Gb(stats.MaxVramBytes!.Value)} Go utilisés, soit {Pct(stats.MaxVramRatio.Value)} de la mémoire de la carte. " +
                "Une fois pleine, le jeu déborde sur la mémoire du PC : saccades et textures lentes à apparaître.",
                "Baissez d'un cran la qualité des textures (c'est le réglage qui consomme le plus de mémoire vidéo), ou la résolution de rendu.",
                Severity.Warning));

        // 4. Qui limite : la carte, le processeur, ou rien (limiteur d'images)
        if (stats.AvgLoad >= 90)
        {
            findings.Add(new("La carte graphique est exploitée à fond",
                $"Charge moyenne {stats.AvgLoad:0} % : c'est bien elle qui fixe les performances, le reste du PC suit.", null, Severity.Ok));
            if (stats.PowerCapShare >= 0.5 && !thermal)
                findings.Add(new("Limitée par sa puissance maximale",
                    $"Pendant {Pct(stats.PowerCapShare)} du temps, la carte a atteint sa limite de puissance" +
                    (stats.PowerLimit is { } limit ? $" ({limit:0} W)" : "") + " : c'est le fonctionnement normal d'une carte poussée à fond.",
                    "Rien à corriger. Une limite de puissance un peu plus basse réduit nettement bruit et chauffe pour une perte de performance faible.",
                    Severity.Info));
        }
        else if (stats.AvgCpuBusiestCore >= 85 || stats.AvgCpuTotal >= 85)
        {
            findings.Add(new("Probablement limitée par le processeur",
                $"Charge moyenne de la carte {stats.AvgLoad:0} % seulement, alors que le processeur sature " +
                $"(cœur le plus chargé {stats.AvgCpuBusiestCore:0} %, ensemble {stats.AvgCpuTotal:0} %).",
                "Baissez les options qui chargent le processeur (distance d'affichage, densité de foule, physique), fermez les programmes en arrière-plan. " +
                "La carte a de la marge : monter la résolution ou activer le DLAA ne coûtera presque rien.",
                Severity.Warning));
        }
        else
        {
            findings.Add(new("Carte graphique peu sollicitée",
                $"Charge moyenne {stats.AvgLoad:0} %" +
                (stats.AvgCpuBusiestCore is { } core ? $", processeur non saturé (cœur le plus chargé {core:0} %)." : ".") +
                " Le plus souvent : un limiteur d'images, la synchronisation verticale, ou un jeu peu exigeant.",
                "Rien d'anormal si le jeu est fluide. Sinon, vérifiez le limiteur d'images du jeu et du Panneau de configuration NVIDIA.",
                Severity.Info));
        }

        // 5. Bruit
        if (stats.MaxFan >= 90)
            findings.Add(new("Ventilateurs à fond",
                $"Jusqu'à {stats.MaxFan} % de leur vitesse : la carte est bruyante en charge.",
                "Une limite de puissance un peu plus basse ou un meilleur flux d'air dans le boîtier les calmera.",
                Severity.Info));

        var ordered = findings.OrderByDescending(f => f.Severity).ToList();
        var worst = ordered.First();
        var headline = worst.Severity >= Severity.Info ? worst.Title : "Tout va bien : la carte graphique tourne à plein régime, sans surchauffe";
        return new PerformanceReport(at, stats, true, headline, ordered);
    }

    private static PerformanceStats Stats(int seconds, List<PerfSample> active)
    {
        double Share(GpuLimitReasons reason) =>
            active.Count == 0 ? 0 : (double)active.Count(s => Has(s, reason)) / active.Count;

        var temps = active.Select(s => s.Gpu.TemperatureC).OfType<uint>().ToList();
        var clocks = active.Select(s => s.Gpu.GraphicsClockMHz).OfType<uint>().ToList();
        var powers = active.Select(s => s.Gpu.PowerWatts).OfType<double>().ToList();
        var vram = active.Where(s => s.Gpu.VramUsedBytes is not null && s.Gpu.VramTotalBytes > 0).ToList();
        var fans = active.Select(s => s.Gpu.FanPercent).OfType<uint>().ToList();
        var cpus = active.Select(s => s.Cpu).OfType<CpuLoad>().ToList();
        var maxVram = vram.Count == 0 ? null : vram.MaxBy(s => s.Gpu.VramUsedBytes);

        return new PerformanceStats(
            seconds,
            active.Count,
            active.Count == 0 ? 0 : active.Average(s => (double)s.Gpu.LoadPercent!.Value),
            temps.Count == 0 ? 0 : temps.Average(t => (double)t),
            temps.Count == 0 ? 0 : temps.Max(),
            clocks.Count == 0 ? 0 : clocks.Average(c => (double)c),
            clocks.Count == 0 ? 0 : clocks.Max(),
            powers.Count == 0 ? null : powers.Average(),
            active.Select(s => s.Gpu.PowerLimitWatts).OfType<double>().Cast<double?>().LastOrDefault(),
            maxVram?.Gpu.VramUsedBytes,
            maxVram is null ? null : (double)maxVram.Gpu.VramUsedBytes!.Value / maxVram.Gpu.VramTotalBytes!.Value,
            fans.Count == 0 ? null : fans.Max(),
            Share(GpuLimitReasons.PowerCap),
            Share(GpuLimitReasons.Thermal),
            Share(GpuLimitReasons.Emergency),
            cpus.Count == 0 ? null : cpus.Average(c => c.Total),
            cpus.Count == 0 ? null : cpus.Average(c => c.BusiestCore));
    }

    private static bool Has(PerfSample s, GpuLimitReasons reason) =>
        s.Gpu.LimitReasons is { } r && (r & (ulong)reason) != 0;

    private static string Pct(double ratio) => $"{ratio * 100:0} %";

    private static string Gb(ulong bytes) => (bytes / 1073741824.0).ToString("0.0", Fr);
}
