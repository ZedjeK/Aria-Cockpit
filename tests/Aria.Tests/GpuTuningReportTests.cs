using Aria.Core.Bench;
using Aria.Core.Nvidia;
using Aria.Core.Performance;
using Aria.Core.Reporting;

namespace Aria.Tests;

public class GpuTuningReportTests
{
    private static PerformanceStats Stats(double clock, double watts, uint maxTemp, double powerCap, double load) =>
        new(180, 180, load, maxTemp - 3, maxTemp, clock, (uint)clock, watts, 320, 1_600_000_000, 0.09, 67, powerCap, 0, 0, 2, 24, clock);

    private static GpuTuningState S(uint cap, int core, int memory) => new(320000, cap, core, memory, false);

    private static readonly DateTime Day = new(2026, 10, 30);
    private static TuningStep Step(int h, int m, TuningPhase phase, string label, GpuTuningState s, StepVerdict v, string detail = "") =>
        new(Day.AddHours(h).AddMinutes(m), phase, label, s, v, detail);

    /// <summary>Équilibre du 30/10 sur la tour (RTX 4080 SUPER, banc Ultra 4K), tel que l'assistant l'a déroulé.</summary>
    private static TuningRun Tower(BalancedRecord? previous = null) => new(
        TuningGoal.Balanced, "NVIDIA GeForce RTX 4080 SUPER", "621.01", Day.AddHours(13).AddMinutes(49), Day.AddHours(14).AddMinutes(37),
        "banc d'essai Aria (Ultra 4K)", S(0, 0, 0), S(0, 0, 0), S(2685, 145, 750), Changed: true, Aborted: false,
        Outcome: "Validé : 15 minutes sans plantage ni artefact.", Validated: true,
        Baseline: Stats(2592, 315.6, 71, 0.944, 96.7), BaselineFps: 98.2,
        ResultStats: Stats(2675, 304.4, 72, 0.639, 95.2), ResultFps: 104.8,
        ResultChecks: new BenchStability("reussi", "memoire", false, 4976, 0, 472, 0, 693.1, false),
        Previous: previous, Verdict: null,
        Steps:
        [
            Step(13, 53, TuningPhase.Start, "Mesure de départ", S(0, 0, 0), StepVerdict.Reference, "98,2 FPS, 2592 MHz, 316 W, 71 °C max"),
            Step(13, 59, TuningPhase.Search, "Palier 1", S(2685, 100, 0), StepVerdict.Stable),
            Step(14, 0, TuningPhase.Search, "Palier 2", S(2685, 200, 0), StepVerdict.Unstable, "calculs faux à pleine puissance"),
            Step(14, 3, TuningPhase.Search, "Palier 3", S(2685, 130, 0), StepVerdict.Stable),
            Step(14, 6, TuningPhase.Search, "Palier 4", S(2685, 160, 0), StepVerdict.Stable),
            Step(14, 7, TuningPhase.Search, "Palier 5", S(2685, 190, 0), StepVerdict.Unstable, "calculs faux à pleine puissance"),
            Step(14, 8, TuningPhase.Search, "Palier 6", S(2685, 175, 0), StepVerdict.Unstable, "calculs faux à pleine puissance"),
            Step(14, 9, TuningPhase.Check, "Référence mémoire", S(2685, 145, 0), StepVerdict.Reference, "690 Go/s en lecture"),
            Step(14, 11, TuningPhase.Memory, "Mémoire, palier 1", S(2685, 145, 250), StepVerdict.Stable),
            Step(14, 14, TuningPhase.Memory, "Mémoire, palier 2", S(2685, 145, 500), StepVerdict.Stable),
            Step(14, 16, TuningPhase.Memory, "Mémoire, palier 3", S(2685, 145, 750), StepVerdict.Stable),
            Step(14, 18, TuningPhase.Memory, "Mémoire, palier 4", S(2685, 145, 1000), StepVerdict.NotFaster, "la mémoire corrige ses erreurs au lieu d'accélérer"),
            Step(14, 37, TuningPhase.Validation, "Validation 15 min", S(2685, 145, 750), StepVerdict.Stable, "104,8 FPS")
        ],
        Notes: [], Incidents: []);

    [Fact]
    public void TheConclusionExplainsTheTowerResult()
    {
        var lines = GpuTuningReport.Conclusion(Tower());

        Assert.Equal("Équilibre validé : 320 W · plafond 2685 MHz · GPU +145 MHz · mémoire +750 MHz.", lines[0]);
        Assert.StartsWith("Mesuré : 104,8 FPS au banc contre 98,2 au départ (+6,7 %), 304 W contre 316 W (-11 W)", lines[1]);
        Assert.Contains("Plus rapide et moins gourmande à la fois : c'est le résultat d'un undervolt réussi.", lines);
        Assert.Contains("Décalage GPU : +160 MHz est le plus haut palier passé ; +175 MHz a échoué (calculs faux à pleine puissance). " +
                        "Retenu : +145 MHz, un cran de marge sous le dernier palier passé.", lines);
        Assert.Contains("Mémoire : +750 MHz est le plus haut palier passé ; +1000 MHz n'allait pas plus vite " +
                        "(la mémoire corrige ses erreurs au lieu d'accélérer). Retenu : +750 MHz.", lines);
        Assert.Contains(lines, l => l.StartsWith("Validation réussie", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Conseil :", StringComparison.Ordinal));
    }

    [Fact]
    public void ARepeatedResultIsCalledReproducible()
    {
        var before = new BalancedRecord(new DateTime(2026, 10, 27), "NVIDIA GeForce RTX 4080 SUPER", "", "621.01", null,
            "p=320000;c=0;o=0;m=0;b=0", "p=320000;c=2685;o=145;m=1000;b=0", true, Stats(2592, 315, 71, 0.9, 97));

        Assert.Contains("Même décalage GPU que l'Équilibre du 27/10 (+145 MHz) ; seule la mémoire diffère (+1000 → +750 MHz).",
            GpuTuningReport.Conclusion(Tower(before)));
    }

    [Fact]
    public void TheReportIsListedWithItsSummary()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"aria-rapports-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "reglage-gpu_2026-10-30_143700.txt");
            using (var w = new StreamWriter(path)) GpuTuningReport.Write(Tower(), "Z-HOME", w);
            var text = File.ReadAllText(path);

            var info = Assert.Single(ReportIndex.List(folder));
            Assert.Equal(ReportKind.GpuTuning, info.Kind);
            Assert.Equal("Équilibre validé · 320 W · plafond 2685 MHz · GPU +145 MHz · mémoire +750 MHz · 104,8 FPS (+6,7 %) · 304 W (-11 W)", info.Summary);
            Assert.Contains("14:00  Palier 2 · 320 W · plafond 2685 MHz · GPU +200 MHz · mémoire +0 MHz → instable", text);
            Assert.Contains("Aucun incident du pilote NVIDIA", text);
            Assert.Contains("Calcul vérifié : 4976 exécutions à pleine puissance, 0 écart(s)", text);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AnAbortedRunSaysWhereTheCardIs()
    {
        var run = Tower() with
        {
            Aborted = true, Changed = false, Validated = false, Result = S(0, 0, 0),
            Outcome = "Assistant abandonné (Palier 3) : la carte est revenue à son réglage d'avant (320 W · sans plafond · GPU +0 MHz · mémoire +0 MHz).",
            Steps = Tower().Steps.Take(3).ToList()
        };

        var lines = GpuTuningReport.Conclusion(run);

        Assert.Equal(run.Outcome, lines[0]);
        Assert.DoesNotContain(lines, l => l.StartsWith("Conseil :", StringComparison.Ordinal));
        Assert.Equal("Équilibre interrompu avant la fin", GpuTuningReport.Summary(run));
    }
}
