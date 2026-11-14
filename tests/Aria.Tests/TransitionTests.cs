using Aria.Bench;
using Aria.Bench.Stability;
using Aria.Core.Bench;

namespace Aria.Tests;

/// <summary>
/// Contrôle des transitions (repos, puis rafales de calcul de plus en plus chargées, mémoire relue après chaque phase) :
/// trace des états de la carte, verdict d'Aria-Bench, options passées au banc et lecture du journal par Aria Cockpit.
/// Les cycles eux-mêmes ne se jouent que sur une carte : voir « Aria-Bench --transitions ».
/// </summary>
public class TransitionTests
{
    private static StabilityReport Transitions(long words = 0, long mismatches = 0, long cycles = 2, int? changes = 21, params string[] failures) =>
        StabilityReport.Conclude("transitions", selfCheck: false, memoryTested: false, burnTested: false, [], 0, 0, 0, 0,
            failures, null, requestedSeconds: 40, realSeconds: 40,
            transitionsTested: true, transitionCycles: cycles, transitionWrongWords: words, transitionMismatches: mismatches, stateChanges: changes);

    private static StabilityReport SelfCheck(long words, long mismatches) =>
        StabilityReport.Conclude("transitions", selfCheck: true, memoryTested: false, burnTested: false, [], 0, 0, 0, 0,
            [], null, requestedSeconds: 20, realSeconds: 20,
            transitionsTested: true, transitionCycles: 3, transitionWrongWords: words, transitionMismatches: mismatches, stateChanges: 14);

    private static BenchResult Journal(StabilityReport report) =>
        BenchResult.Parse(new BenchLog { Statut = BenchLog.StatusDone, Stabilite = report }.ToJson());

    [Fact]
    public void TraceCountsEveryChangeOfStateOrOfMemoryClock()
    {
        var trace = new TransitionTrace();
        trace.Add(2, 2610, 11376);
        trace.Add(2, 2610, 11376);   // rien n'a bougé
        trace.Add(2, 1260, 11376);   // le cœur seul : pas un changement d'état
        trace.Add(3, 1260, 5001);    // état et mémoire
        trace.Add(3, 915, 810);      // mémoire seule
        trace.Add(8, 210, 405);

        Assert.Equal(6, trace.Samples);
        Assert.Equal(3, trace.Changes);
        Assert.Equal("P2/P3/P8", trace.StatesText);
        Assert.Equal("210-2610", trace.CoreRange);
        Assert.Equal("405-11376", trace.MemoryRange);
    }

    [Fact]
    public void TraceIgnoresAnUnknownState()
    {
        var trace = new TransitionTrace();
        trace.Add(32, 2700, 11376);   // NVML donne 32 quand il ne sait pas

        Assert.Equal(0, trace.Samples);
        Assert.Null(trace.CoreRange);
        Assert.Empty(trace.StatesText);
    }

    [Fact]
    public void CleanRunWithRealTransitionsPasses()
    {
        var report = Transitions();

        Assert.Equal(StabilityReport.Passed, report.Statut);
        Assert.Equal(2, report.CyclesTransitions);
        Assert.Equal(21, report.ChangementsEtat);
        Assert.Empty(report.Raisons);
    }

    [Fact]
    public void WrongMemoryAfterAStateChangeFails()
    {
        var report = Transitions(words: 5);

        Assert.Equal(StabilityReport.Failed, report.Statut);
        Assert.StartsWith("Transitions : 5 mot(s)", report.Raisons.Single());
    }

    [Fact]
    public void WrongComputeAtVariableClocksFails()
    {
        var report = Transitions(mismatches: 3);

        Assert.Equal(StabilityReport.Failed, report.Statut);
        Assert.StartsWith("Transitions : 3 résultat(s)", report.Raisons.Single());
    }

    [Fact]
    public void ACardThatBarelyChangedStateIsAWarningNotAPass()
    {
        // Programme qui charge la carte, mode « performances maximales » : la carte reste en P0, le test ne prouve rien
        var report = Transitions(changes: 1);

        Assert.Equal(StabilityReport.Warning, report.Statut);
        Assert.Contains("presque pas changé d'état", report.Avertissements.Single());
    }

    [Fact]
    public void UnreadableStateIsAWarningNotAFailure()
    {
        var report = Transitions(changes: null);

        Assert.Equal(StabilityReport.Warning, report.Statut);
        Assert.Contains("nvml.dll indisponible", report.Avertissements.Single());
        Assert.Empty(report.Raisons);
    }

    [Fact]
    public void LessThanOneFullCycleIsAWarning()
    {
        var report = Transitions(cycles: 0);

        Assert.Equal(StabilityReport.Warning, report.Statut);
        Assert.Contains("aucun cycle complet", report.Avertissements.Single());
    }

    [Fact]
    public void DriverGivingUpFailsTheCheck()
    {
        var report = Transitions(failures: "transitions : la carte ne répond plus depuis 20 s");

        Assert.Equal(StabilityReport.Failed, report.Statut);
        Assert.Contains("pilote a lâché", report.Raisons.Single());
    }

    [Fact]
    public void SelfCheckPassesWhenEveryInjectedErrorIsFound()
    {
        // 64 mots écrasés, relus après chacune des cinq phases d'un cycle (320), et toutes les exécutions de calcul faussées
        var report = SelfCheck(words: 320, mismatches: BurnTest.Threads);

        Assert.Equal(StabilityReport.Passed, report.Statut);
        Assert.Empty(report.Avertissements);
    }

    [Fact]
    public void SelfCheckFailsWhenMemoryErrorsAreMissed()
    {
        var report = SelfCheck(words: 10, mismatches: BurnTest.Threads);

        Assert.Equal(StabilityReport.Failed, report.Statut);
        Assert.StartsWith("Transitions : 10 mot(s) faux trouvés pour 64", report.Raisons.Single());
    }

    [Fact]
    public void SelfCheckFailsWhenComputeErrorsAreMissed()
    {
        var report = SelfCheck(words: 320, mismatches: 100);

        Assert.Equal(StabilityReport.Failed, report.Statut);
        Assert.StartsWith("Transitions : 100 écart(s) trouvés", report.Raisons.Single());
    }

    [Fact]
    public void PhasesGoFromALightLoadToFullLoad()
    {
        Assert.Equal(["charge 5 %", "charge 20 %", "charge 50 %", "pleine charge"], TransitionTest.Phases.Select(p => p.Name));
        Assert.Equal(TransitionTest.Phases.Select(p => p.Duty).Order(), TransitionTest.Phases.Select(p => p.Duty));
        Assert.Equal(1.0, TransitionTest.Phases[^1].Duty);
    }

    [Fact]
    public void OptionsSelectTheTransitionCheck()
    {
        var options = BenchOptions.Parse(["--transitions", "--duree", "120", "--sortie", "out.json"]);

        Assert.Equal(StabilityMode.Transitions, options.Stability);
        Assert.Equal(TimeSpan.FromSeconds(120), options.Duration);
    }

    [Fact]
    public void SelfCheckOptionKeepsTheTransitionMode()
    {
        var options = BenchOptions.Parse(["--transitions", "--auto-controle"]);

        Assert.Equal(StabilityMode.Transitions, options.Stability);
        Assert.True(options.SelfCheck);
    }

    [Fact]
    public void CockpitAsksTheBenchForTheTransitionCheck()
    {
        var args = BenchRunner.CheckArguments(BenchCheck.Transitions, TimeSpan.FromMinutes(2), "controle.json");

        Assert.Equal("--transitions", args[0]);
        Assert.DoesNotContain("--precedent", args);
    }

    [Fact]
    public void CockpitReadsTheTransitionResultFromTheJournal()
    {
        var report = Transitions(words: 5);
        report.EtatsObserves = ["P0", "P3", "P8"];
        report.PlageCoeurMHz = "210-2700";
        report.PlageMemoireMHz = "405-11626";

        var bad = Journal(report);

        Assert.Equal(2, bad.Stabilite!.CyclesTransitions);
        Assert.Equal(5, bad.Stabilite.MotsFauxTransitions);
        Assert.Equal(21, bad.Stabilite.ChangementsEtat);
        Assert.Equal(["P0", "P3", "P8"], bad.Stabilite.EtatsObserves);
        Assert.Equal("405-11626", bad.Stabilite.PlageMemoireMHz);
        Assert.True(bad.StabilityFailed);
        Assert.False(Journal(Transitions()).Unstable);
    }

    [Fact]
    public void ABarelyMovingCardDoesNotJudgeTheStepWhenCockpitReadsIt()
    {
        var result = Journal(Transitions(changes: 1));

        Assert.True(result.Completed);
        Assert.False(result.Unstable);
    }
}
