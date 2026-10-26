using Aria.Core.Nvidia;

namespace Aria.Tests;

public class TuningSearchTests
{
    private static readonly GpuTuningLimits Limits = new(150_000, 370_000, 320_000, 210, 3105, -1000, 1000, -2000, 3000);
    private static readonly GpuTuningState Factory = new(320_000, 0, 0, 0, false);

    /// <summary>Joue la recherche : chaque palier proposé reçoit le verdict de <paramref name="stable"/>.</summary>
    private static List<int> Run(TuningSearch search, Func<int, bool> stable)
    {
        var tested = new List<int>();
        while (!search.Finished)
        {
            var offset = search.Current.CoreOffsetMHz;
            tested.Add(offset);
            search.Report(stable(offset));
        }
        return tested;
    }

    [Fact]
    public void BalancedGoesFromCoarseToFineAndBacksOffAtTheEdge()
    {
        // Exemple du README : +100 ✓, +200 ✓, +300 ✗, +230 ✓, +260 ✗, +245 ✓
        var search = TuningSearch.Balanced(Factory, Limits, 2550);

        var tested = Run(search, offset => offset is not (300 or 260));

        Assert.Equal([100, 200, 300, 230, 260, 245], tested);
        Assert.Equal(245, search.LastStable.CoreOffsetMHz);
        Assert.Equal(230, search.Safe.CoreOffsetMHz);
        Assert.Equal(2550u, search.Safe.MaxClockMHz);
        Assert.True(search.HitLimit);
    }

    [Fact]
    public void BalancedStopsAtAriasOffsetLimitWithoutBackingOff()
    {
        // Cas de la tour : trois paliers stables, la recherche bute sur +300 sans instabilité
        var search = TuningSearch.Balanced(Factory, Limits, 2550);

        var tested = Run(search, _ => true);

        Assert.Equal([100, 200, TuningPlan.CoreLimit], tested);
        Assert.False(search.HitLimit);
        Assert.Equal(TuningPlan.CoreLimit, search.Safe.CoreOffsetMHz);
    }

    [Fact]
    public void PerformanceStartsFromTheFactoryCurveAtMaximumPower()
    {
        var balanced = new GpuTuningState(320_000, 2550, 300, 250, false);

        var start = TuningSearch.PerformanceStart(balanced, Limits);

        Assert.Equal(new GpuTuningState(370_000, 0, 0, 250, false), start);
    }

    [Fact]
    public void PerformanceKeepsThePowerLimitWhenItCannotBeChanged()
    {
        var locked = Limits with { SupportsPowerLimit = false };
        var balanced = new GpuTuningState(115_000, 0, 150, 0, false);

        Assert.Equal(115_000u, TuningSearch.PerformanceStart(balanced, locked).PowerLimitMilliwatts);
    }

    [Fact]
    public void PerformanceClimbsFromZeroUntilTheFirstUnstableStep()
    {
        var search = TuningSearch.Performance(new GpuTuningState(320_000, 2550, 300, 0, false), Limits);

        var tested = Run(search, offset => offset < 150);

        Assert.Equal([60, 120, 180, 150, 135], tested);
        Assert.Equal(135, search.LastStable.CoreOffsetMHz);
        Assert.Equal(0u, search.LastStable.MaxClockMHz);
        Assert.Equal(370_000u, search.LastStable.PowerLimitMilliwatts);
        Assert.Equal(120, search.Safe.CoreOffsetMHz);
    }

    [Fact]
    public void FactoryStartIsNeverKeptAtStartup()
    {
        Assert.False(TuningPlan.Start(Factory with { AtStartup = true }).AtStartup);
    }
}
