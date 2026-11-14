using Aria.Bench;
using Aria.Bench.Stability;
using Aria.Core.Bench;
using Aria.Core.Nvidia;

namespace Aria.Tests;

/// <summary>
/// Contrôle des cœurs Tensor (calcul INT8 vérifié bit à bit) : résultat de référence, verdict d'Aria-Bench, options passées au banc
/// et lecture du journal par Aria Cockpit. Le noyau lui-même ne se teste que sur une carte NVIDIA : voir « Aria-Bench --tenseurs ».
/// </summary>
public class TensorTests
{
    private const long Elements = 655_360;   // 2 560 warps × 256 résultats, sur une RTX 4080 SUPER

    private static StabilityReport Tensor(long wrong, long executions = 332, params string[] failures) =>
        StabilityReport.Conclude("tenseurs", selfCheck: false, memoryTested: false, burnTested: false, [], 0, 0, 0, 0,
            failures, null, requestedSeconds: 60, realSeconds: 60,
            tensorTested: true, tensorExecutions: executions, tensorWrong: wrong, tensorElements: Elements);

    private static StabilityReport Skipped(string why) =>
        StabilityReport.Conclude("tenseurs", selfCheck: false, memoryTested: false, burnTested: false, [], 0, 0, 0, 0,
            [], null, requestedSeconds: 60, realSeconds: 0.4, tensorSkipped: why);

    private static StabilityReport SelfCheck(long wrong, string? skipped = null) =>
        StabilityReport.Conclude("tenseurs", selfCheck: true, memoryTested: false, burnTested: false, [], 0, 0, 0, 0,
            [], null, requestedSeconds: 20, realSeconds: 20,
            tensorTested: skipped is null, tensorExecutions: 241, tensorWrong: wrong, tensorElements: Elements, tensorSkipped: skipped);

    private static BenchResult Journal(StabilityReport report) =>
        BenchResult.Parse(new BenchLog { Statut = BenchLog.StatusDone, Stabilite = report }.ToJson());

    [Fact]
    public void ReferenceMatchesWhatTheCardComputedAndWhatThePrototypeVerified()
    {
        // Valeurs relues sur une RTX 4080 SUPER, identiques à la référence calculée sur le processeur (0 résultat faux sur 2 560 warps)
        var (a, b) = TensorReference.BuildPool();
        var totals = TensorReference.Totals(a, b);

        Assert.Equal(9_367_616, TensorReference.Expected(totals, 1)[0]);
        Assert.Equal(37_470_464, TensorReference.Expected(totals, 4)[0]);
        Assert.Equal(599_527_424, TensorReference.Expected(totals, 64)[0]);
        Assert.Equal(TensorReference.OutputElements, TensorReference.Expected(totals, 1).Length);
    }

    [Fact]
    public void PoolIsDeterministicAndUsesTheWholeInt8Range()
    {
        var (a1, b1) = TensorReference.BuildPool();
        var (a2, b2) = TensorReference.BuildPool();

        Assert.Equal(a1, a2);
        Assert.Equal(b1, b2);
        Assert.Equal(TensorReference.Pool * TensorReference.TileBytes, a1.Length);
        Assert.True(a1.Min() < -100 && a1.Max() > 100, "le réservoir doit faire basculer les bits de poids fort");
    }

    [Fact]
    public void CleanRunPasses()
    {
        var report = Tensor(0);

        Assert.Equal(StabilityReport.Passed, report.Statut);
        Assert.Equal(332, report.ExecutionsTenseurs);
        Assert.Empty(report.Raisons);
        Assert.Null(report.TenseursNonTestes);
    }

    [Fact]
    public void WrongResultsFailTheCheck()
    {
        var report = Tensor(12);

        Assert.Equal(StabilityReport.Failed, report.Statut);
        Assert.StartsWith("Cœurs Tensor : 12", report.Raisons.Single());
        Assert.Equal(12, report.EcartsTenseurs);
    }

    [Fact]
    public void DriverGivingUpFailsTheCheck()
    {
        var report = Tensor(0, 3, "cœurs Tensor : Échec pendant exécution sur la carte : an illegal memory access was encountered, code 700.");

        Assert.Equal(StabilityReport.Failed, report.Statut);
        Assert.Contains("pilote a lâché", report.Raisons.Single());
    }

    [Fact]
    public void ImpossibleCheckIsAWarningAndNeverAFailure()
    {
        var report = Skipped("nvcuda.dll est absente : le pilote NVIDIA est incomplet.");

        Assert.Equal(StabilityReport.Warning, report.Statut);
        Assert.Empty(report.Raisons);
        Assert.Contains("non testés", report.Avertissements.Single());
        Assert.StartsWith("nvcuda.dll", report.TenseursNonTestes);
        // Ni « test arrêté avant la fin » : il n'a jamais démarré
        Assert.DoesNotContain(report.Avertissements, w => w.StartsWith("Test arrêté", StringComparison.Ordinal));
    }

    [Fact]
    public void SelfCheckPassesWhenEveryInjectedResultIsFound()
    {
        // Un lancement faussé exprès : les 655 360 résultats doivent tous sortir faux, rien d'autre
        Assert.Equal(StabilityReport.Passed, SelfCheck(Elements).Statut);
    }

    [Fact]
    public void SelfCheckFailsWhenTheTestMissesInjectedErrors()
    {
        var report = SelfCheck(1_000);

        Assert.Equal(StabilityReport.Failed, report.Statut);
        Assert.StartsWith("Cœurs Tensor : 1000", report.Raisons.Single());
    }

    [Fact]
    public void SelfCheckIsImpossibleWithoutCuda()
    {
        var report = SelfCheck(0, skipped: "aucune carte NVIDIA compatible CUDA n'est visible par le pilote.");

        Assert.Equal(StabilityReport.Failed, report.Statut);
        Assert.Contains("auto-contrôle impossible", report.Raisons.Single());
    }

    [Fact]
    public void OptionsSelectTheTensorCheck()
    {
        var options = BenchOptions.Parse(["--tenseurs", "--duree", "60", "--sortie", "out.json"]);

        Assert.Equal(StabilityMode.Tensor, options.Stability);
        Assert.Equal(TimeSpan.FromSeconds(60), options.Duration);
    }

    [Fact]
    public void SelfCheckOptionKeepsTheTensorMode()
    {
        // --auto-controle seul passe en mode mémoire ; avec --tenseurs, il reste sur les cœurs Tensor
        var options = BenchOptions.Parse(["--tenseurs", "--auto-controle"]);

        Assert.Equal(StabilityMode.Tensor, options.Stability);
        Assert.True(options.SelfCheck);
    }

    [Fact]
    public void CockpitAsksTheBenchForTheTensorCheck()
    {
        var args = BenchRunner.CheckArguments(BenchCheck.Tensor, TimeSpan.FromSeconds(60), "controle.json");

        Assert.Equal("--tenseurs", args[0]);
        Assert.DoesNotContain("--precedent", args);
    }

    [Fact]
    public void CockpitReadsTheTensorResultFromTheJournal()
    {
        var bad = Journal(Tensor(12));

        Assert.Equal(332, bad.Stabilite!.ExecutionsTenseurs);
        Assert.Equal(12, bad.Stabilite.EcartsTenseurs);
        Assert.True(bad.StabilityFailed);
        Assert.True(bad.Unstable);

        var good = Journal(Tensor(0));
        Assert.False(good.Unstable);
    }

    [Fact]
    public void ImpossibleCheckDoesNotJudgeTheStepWhenCockpitReadsIt()
    {
        var result = Journal(Skipped("capacité de calcul 6.1 : les cœurs Tensor INT8 commencent avec Turing (RTX 20, capacité 7.5)."));

        Assert.True(result.Completed);
        Assert.False(result.Unstable);
        Assert.StartsWith("capacité de calcul 6.1", result.Stabilite!.TenseursNonTestes);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4080 SUPER", true)]
    [InlineData("NVIDIA GeForce RTX 2060", true)]
    [InlineData("NVIDIA GeForce RTX 3050 Laptop GPU", true)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", false)]
    [InlineData("NVIDIA GeForce GTX 1060 6GB", false)]
    [InlineData("NVIDIA GeForce GTX 750 Ti", false)]
    public void OnlyRtxCardsHaveTensorCores(string name, bool expected) =>
        Assert.Equal(expected, GpuCatalog.HasTensorCores(name));

    [Fact]
    public void UnknownRtxNameIsTreatedAsHavingTensorCores() =>
        Assert.True(GpuCatalog.HasTensorCores("NVIDIA RTX 6000 Ada Generation"));
}
