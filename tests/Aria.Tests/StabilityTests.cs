using Aria.Bench;
using Aria.Bench.Stability;
using Aria.Core.Bench;

namespace Aria.Tests;

/// <summary>
/// Contrôles de stabilité : verdict d'Aria-Bench (calcul vérifié, mémoire vidéo, auto-contrôle), options passées par
/// Aria Cockpit, et lecture du journal par l'assistant.
/// </summary>
public class StabilityTests
{
    /// <summary>Passes dans l'ordre (bande passante maximale), chacune suivie d'une passe mélangée comme dans le vrai test.</summary>
    private static List<MemoryPass> Passes(params double[] reads) =>
        [.. reads.SelectMany((read, i) => new[]
        {
            new MemoryPass(2 * i, false, read, read, 0, 0, i),
            new MemoryPass(2 * i + 1, true, read / 2, read / 2, 0, 0, i)
        })];

    private static StabilityReport Memory(List<MemoryPass> passes, long wrongWords = 0, double? previous = null) =>
        StabilityReport.Conclude("memoire", selfCheck: false, memoryTested: true, burnTested: true, passes, wrongWords, wrongWords,
            dispatches: 800, mismatches: 0, [], previous, requestedSeconds: 90, realSeconds: 90);

    private static StabilityReport Compute(long mismatches, params string[] failures) =>
        StabilityReport.Conclude("calcul", selfCheck: false, memoryTested: false, burnTested: true, [], 0, 0,
            dispatches: 2384, mismatches, failures, null, requestedSeconds: 60, realSeconds: 60);

    private static StabilityReport SelfCheck(long wrongWords, long mismatches) =>
        StabilityReport.Conclude("memoire", selfCheck: true, memoryTested: true, burnTested: true, Passes(700, 700), wrongWords, wrongWords,
            dispatches: 300, mismatches, [], null, requestedSeconds: 20, realSeconds: 20);

    private static BenchResult Render() => BenchResult.Parse("""
        { "statut": "terminé", "resultats": [ { "qualite": "Ultra", "definition": "1440p", "images": 25828, "fpsMoyen": 141.1, "verifications": 61, "erreursCalcul": 0 } ] }
        """);

    /// <summary>Le journal tel qu'Aria-Bench l'écrit, relu par Aria Cockpit.</summary>
    private static BenchResult Check(StabilityReport report, string statut = BenchLog.StatusDone) =>
        BenchResult.Parse(new BenchLog { Statut = statut, Stabilite = report }.ToJson());

    [Fact]
    public void CleanComputeRunPasses()
    {
        // Tour aux valeurs d'origine : 2384 exécutions en 60 s, aucun écart
        var report = Compute(0);

        Assert.Equal(StabilityReport.Passed, report.Statut);
        Assert.Equal(2384, report.ExecutionsCalcul);
        Assert.Empty(report.Raisons);
    }

    [Fact]
    public void ComputeMismatchesFailEvenWithoutCrash()
    {
        // Undervolt d'Équilibre validé au banc 3D : 17 130 calculs faux à pleine puissance, et des « illegal memory access » sous LLM
        var report = Compute(17_130);

        Assert.Equal(StabilityReport.Failed, report.Statut);
        Assert.StartsWith("Calcul : 17130", report.Raisons.Single());
    }

    [Fact]
    public void DriverGivingUpFailsTheCheck()
    {
        var report = Compute(0, "calcul vérifié : la carte ne répond plus depuis 20 s");

        Assert.Equal(StabilityReport.Failed, report.Statut);
        Assert.Contains("pilote a lâché", report.Raisons.Single());
    }

    [Fact]
    public void WrongWordReadBackFailsTheMemory()
    {
        var report = Memory(Passes(702, 702, 701), wrongWords: 1);

        Assert.Equal(StabilityReport.Failed, report.Statut);
        Assert.Equal(1, report.MotsFaux);
    }

    [Fact]
    public void BandwidthIsTheMedianOfInOrderPasses()
    {
        var report = Memory(Passes(700, 702, 701));

        Assert.Equal(StabilityReport.Passed, report.Statut);
        Assert.Equal(6, report.Passes);
        Assert.Equal(701, report.BandePassanteLectureMedianeGoS);
    }

    [Theory]
    [InlineData(702, 702, true)]    // même bande passante : la mémoire corrige au lieu d'accélérer
    [InlineData(702, 700, true)]    // +0,3 % : sous le seuil de 0,5 %
    [InlineData(702, 690, false)]   // +1,7 % : un palier de +250 MHz qui sert
    public void MemoryStepMustBeFasterThanThePreviousOne(double now, double previous, bool notFaster)
    {
        var report = Memory(Passes(now, now, now), previous: previous);

        Assert.Equal(notFaster, report.PasPlusRapide);
        Assert.Equal(notFaster ? StabilityReport.Warning : StabilityReport.Passed, report.Statut);
    }

    [Fact]
    public void BandwidthDroppingWhileWarmingUpIsAWarning()
    {
        // 8 passes dans l'ordre : 700 Go/s au début, 660 à la fin (−6 %)
        var report = Memory(Passes(700, 700, 690, 680, 670, 665, 660, 660));

        Assert.Equal(StabilityReport.Warning, report.Statut);
        Assert.Equal(700, report.BandePassanteDebutGoS);
        Assert.Equal(660, report.BandePassanteFinGoS);
    }

    [Fact]
    public void SelfCheckPassesOnlyWhenEveryInjectedErrorIsFound()
    {
        // Tour : les 64 mots écrasés et tous les fils faussés retrouvés
        Assert.Equal(StabilityReport.Passed, SelfCheck(MemoryTest.InjectedWords, BurnTest.Threads).Statut);
        // Un test qui ne voit pas les erreurs ne doit pas servir de juge
        Assert.Equal(StabilityReport.Failed, SelfCheck(0, BurnTest.Threads).Statut);
        Assert.Equal(StabilityReport.Failed, SelfCheck(MemoryTest.InjectedWords, 0).Statut);
    }

    [Fact]
    public void CockpitArgumentsSelectTheRightCheck()
    {
        var memory = BenchOptions.Parse(BenchRunner.CheckArguments(BenchCheck.Memory, TimeSpan.FromSeconds(90), @"C:\temp\m.json", @"C:\temp\p.json"));
        Assert.Equal(StabilityMode.Memory, memory.Stability);
        Assert.Equal(TimeSpan.FromSeconds(90), memory.Duration);
        Assert.Equal(@"C:\temp\m.json", memory.OutputPath);
        Assert.Equal(@"C:\temp\p.json", memory.PreviousPath);

        // Pas de comparaison de bande passante sans test mémoire
        var compute = BenchOptions.Parse(BenchRunner.CheckArguments(BenchCheck.Compute, TimeSpan.FromSeconds(60), @"C:\temp\c.json", @"C:\temp\p.json"));
        Assert.Equal(StabilityMode.Compute, compute.Stability);
        Assert.Null(compute.PreviousPath);

        var self = BenchOptions.Parse(BenchRunner.CheckArguments(BenchCheck.SelfCheck, TimeSpan.FromSeconds(20), @"C:\temp\a.json"));
        Assert.True(self.SelfCheck);
        Assert.Equal(StabilityMode.Memory, self.Stability);
    }

    [Fact]
    public void CockpitReadsTheBenchVerdict()
    {
        var result = Check(Memory(Passes(702, 702), previous: 702));

        Assert.True(result.Completed);
        Assert.True(result.NotFaster);
        Assert.False(result.StabilityFailed);
        Assert.Equal(4, result.Stabilite!.Passes);
        Assert.Equal(702, result.Stabilite.BandePassanteLectureMedianeGoS);
    }

    [Fact]
    public void SelfCheckVerdictIsRead()
    {
        var passed = Check(SelfCheck(MemoryTest.InjectedWords, BurnTest.Threads));
        var missed = Check(SelfCheck(0, 0));

        Assert.True(passed.SelfCheckPassed);
        Assert.False(passed.StabilityFailed);
        Assert.False(missed.SelfCheckPassed);
        Assert.False(missed.StabilityFailed);
    }

    [Fact]
    public void ChecksMergeWithTheRenderPass()
    {
        var merged = BenchResult.Merge(Render(), Check(Compute(0)));

        Assert.True(merged.Completed);
        Assert.False(merged.Unstable);
        Assert.Equal(141.1, merged.Pass!.FpsMoyen);
        Assert.Equal(2384, merged.Stabilite!.ExecutionsCalcul);
    }

    [Fact]
    public void FailedCheckMakesTheStepUnstable()
    {
        // Le rendu 3D tenait, le calcul à pleine puissance non
        var merged = BenchResult.Merge(Render(), Check(Compute(17_130)));

        Assert.True(merged.StabilityFailed);
        Assert.True(merged.Unstable);
        Assert.Equal(17_130, merged.Stabilite!.EcartsCalcul);
    }

    [Fact]
    public void InterruptedCheckInterruptsTheStepWithoutJudgingIt()
    {
        var merged = BenchResult.Merge(Render(), Check(Compute(0), BenchLog.StatusInterrupted));

        Assert.True(merged.Interrupted);
        Assert.False(merged.Unstable);
    }

    [Fact]
    public void CheckThatLeftNoLogIsADriverFailure()
    {
        var merged = BenchResult.Merge(Render(), BenchResult.Missing("Le banc d'essai s'est arrêté sans écrire son journal (plantage probable du pilote)."));

        Assert.True(merged.DriverFailure);
        Assert.True(merged.Unstable);
        Assert.Equal(141.1, merged.Pass!.FpsMoyen);
    }
}
