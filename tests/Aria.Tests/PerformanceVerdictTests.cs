using System.Text.Json;
using Aria.Core.Nvidia;
using Aria.Core.Performance;

namespace Aria.Tests;

public class PerformanceVerdictTests
{
    private static PerformanceStats Stats(double clock, double? watts, uint maxTemp) =>
        new(180, 180, 98, maxTemp - 3, maxTemp, clock, (uint)clock, watts, 370, 2_000_000_000, 0.13, 65, 0.5, 0, 0, 5, 30, clock);

    // Mesures réelles de la tour (RTX 4080 SUPER, banc Ultra 4K) : Équilibre validé contre Performance
    private static readonly PerformanceStats Balanced = Stats(2700, 292, 69);
    private static readonly PerformanceStats Performance = Stats(2790, 366, 78);

    [Fact]
    public void TowerResultIsNotWorthIt()
    {
        var verdict = PerformanceVerdict.Judge(Performance, 101, Balanced, 100);

        Assert.Equal(VerdictKind.NotWorth, verdict.Kind);
        Assert.True(verdict.RecommendsBalanced);
        Assert.Equal("+1 FPS (+1 %) par rapport à Équilibre.", verdict.Details[0]);
        Assert.Equal("Consommation +74 W (+25 %), température max +9 °C.", verdict.Details[1]);
        Assert.Contains("74 W par FPS gagné", verdict.Details[2]);
        Assert.Equal("Efficacité : 0,28 FPS/W, contre 0,34 FPS/W avec Équilibre.", verdict.Details[3]);
    }

    [Fact]
    public void BigGainForFewWattsIsWorthIt()
    {
        // +10 FPS (+10 %) pour +20 W : 2 W par FPS
        var verdict = PerformanceVerdict.Judge(Stats(2900, 312, 72), 110, Balanced, 100);

        Assert.Equal(VerdictKind.Worth, verdict.Kind);
        Assert.False(verdict.RecommendsBalanced);
    }

    [Fact]
    public void MediumGainIsCostly()
    {
        // +5 FPS (+5 %) pour +12 W : 2,4 W par FPS, gain réel mais sous le seuil du « net »
        Assert.Equal(VerdictKind.Costly, PerformanceVerdict.Judge(Stats(2850, 304, 71), 105, Balanced, 100).Kind);
    }

    [Fact]
    public void BigGainThatCostsTooManyWattsIsNotWorthIt()
    {
        // +10 FPS mais +60 W : 6 W par FPS
        Assert.Equal(VerdictKind.NotWorth, PerformanceVerdict.Judge(Stats(2900, 352, 76), 110, Balanced, 100).Kind);
    }

    [Fact]
    public void FewerFpsIsNotWorthIt()
    {
        Assert.Equal(VerdictKind.NotWorth, PerformanceVerdict.Judge(Performance, 98, Balanced, 100).Kind);
    }

    [Fact]
    public void WithoutFpsTheClockGivesAnEstimate()
    {
        // Mode jeu : +3,3 % de fréquence pour +25 % de watts
        var verdict = PerformanceVerdict.Judge(Performance, null, Balanced, null);

        Assert.Equal(VerdictKind.NotWorth, verdict.Kind);
        Assert.Contains("estimation sans FPS mesurés", verdict.Details[0]);
    }

    [Fact]
    public void WithoutBalancedMeasurementsThereIsNoVerdict()
    {
        var verdict = PerformanceVerdict.Judge(Performance, 101, null, null);

        Assert.Equal(VerdictKind.Unknown, verdict.Kind);
        Assert.False(verdict.RecommendsBalanced);
        Assert.Contains("refaites Équilibre", verdict.Details[0]);
    }

    [Fact]
    public void RecordSavedBeforeThisVersionStillLoads()
    {
        // equilibre.json d'avant les mesures du résultat : les nouveaux champs restent vides
        var old = new BalancedRecord(DateTime.Now, "NVIDIA GeForce RTX 4080 SUPER", "GPU-test", "621.01", null,
            "p=320000;c=0;o=0;m=0;b=0", "p=320000;c=2700;o=215;m=0;b=0", true, Balanced, 97.9, 2160);
        using var json = JsonDocument.Parse(old.ToJson());
        var withoutNewFields = JsonSerializer.Serialize(json.RootElement.EnumerateObject()
            .Where(p => p.Name is not ("ResultStats" or "ResultFps" or "Validated"))
            .ToDictionary(p => p.Name, p => p.Value));

        var loaded = BalancedRecord.FromJson(withoutNewFields);

        Assert.NotNull(loaded);
        Assert.Null(loaded!.ResultStats);
        Assert.Null(loaded.ResultFps);
        Assert.False(loaded.Validated);
        Assert.Equal(215, loaded.ResultState!.CoreOffsetMHz);
    }
}
