using Aria.Core.Nvidia;
using Aria.Core.Reporting;

namespace Aria.Tests;

public class EconomyPowerTests
{
    // RTX 4080 SUPER : 320 W d'usine, plancher d'Économie à 60 % = 190 W ; pas de 30 W, 15 W puis 5 W
    private static readonly GpuTuningLimits Limits = new(150_000, 370_000, 320_000, 210, 3105, -1000, 1000, -2000, 3000);
    private static readonly GpuTuningState Capped = new(320_000, 2550, 145, 750, false);

    private static List<double> Run(TuningSearch search, Func<double, bool> accepted)
    {
        var tested = new List<double>();
        while (!search.Finished)
        {
            var watts = search.Current.PowerLimitWatts;
            tested.Add(watts);
            search.Report(accepted(watts));
        }
        return tested;
    }

    [Fact]
    public void TheFirstStepStartsAtTheMeasuredPower()
    {
        // 262 W mesurés au plafond retenu : une limite entre 320 et 260 W ne changerait rien
        var search = TuningSearch.EconomyPower(Capped, Limits, 262_400);

        var tested = Run(search, watts => watts >= 225);

        Assert.Equal([260, 230, 200, 215, 225], tested);
        Assert.Equal(225, search.LastStable.PowerLimitWatts);
        Assert.Equal(Capped with { PowerLimitMilliwatts = 225_000 }, search.LastStable);
    }

    [Fact]
    public void ARefusedFirstStepIsRefinedFromTheTop()
    {
        var search = TuningSearch.EconomyPower(Capped, Limits, 262_400);

        var tested = Run(search, watts => watts >= 270);

        Assert.Equal([260, 305, 290, 275, 270], tested);
        Assert.Equal(270, search.LastStable.PowerLimitWatts);
    }

    [Fact]
    public void NothingIsTestedBelowTheFloor()
    {
        Assert.Equal(190_000, TuningSearch.PowerFloorMilliwatts(Limits));
        var search = TuningSearch.EconomyPower(Capped, Limits, 150_000);

        var tested = Run(search, _ => true);

        Assert.Equal(190, tested[0]);
        Assert.Single(tested);
    }

    [Fact]
    public void TheReportNamesTheWattsPhase()
    {
        var at = new DateTime(2026, 10, 31, 10, 0, 0);
        var run = new TuningRun(TuningGoal.Economy, "NVIDIA GeForce RTX 4080 SUPER", "621.01", at, at.AddMinutes(40), "banc d'essai Aria (Ultra 4K)",
            Capped with { MaxClockMHz = 2685 }, Capped with { MaxClockMHz = 2685 }, Capped with { PowerLimitMilliwatts = 225_000 },
            Changed: true, Aborted: false, Outcome: "", Validated: false, Baseline: null, BaselineFps: null, ResultStats: null, ResultFps: null,
            ResultChecks: null, Previous: null, Verdict: null,
            Steps:
            [
                new(at.AddMinutes(10), TuningPhase.Search, "Palier 1", Capped, StepVerdict.Stable),
                new(at.AddMinutes(20), TuningPhase.Power, "Watts, palier 1", Capped with { PowerLimitMilliwatts = 230_000 }, StepVerdict.Stable),
                new(at.AddMinutes(23), TuningPhase.Power, "Watts, palier 2", Capped with { PowerLimitMilliwatts = 200_000 }, StepVerdict.Unstable, "perte jugée trop grande ; 92 FPS"),
                new(at.AddMinutes(26), TuningPhase.Power, "Watts, palier 3", Capped with { PowerLimitMilliwatts = 215_000 }, StepVerdict.Unstable, "perte jugée trop grande"),
                new(at.AddMinutes(29), TuningPhase.Power, "Watts, palier 4", Capped with { PowerLimitMilliwatts = 225_000 }, StepVerdict.Stable)
            ],
            Notes: [], Incidents: []);

        var lines = GpuTuningReport.Conclusion(run);

        Assert.Contains("Plafond : 2550 MHz est le plus bas palier accepté. Retenu : 2550 MHz.", lines);
        Assert.Contains("Limite de puissance : 225 W est le plus bas palier accepté ; 215 W a échoué (perte jugée trop grande). Retenu : 225 W.", lines);
    }
}
