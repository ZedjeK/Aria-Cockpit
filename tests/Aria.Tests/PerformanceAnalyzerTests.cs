using Aria.Core.Nvidia;
using Aria.Core.Performance;

namespace Aria.Tests;

public class PerformanceAnalyzerTests
{
    private static PerfSample Sample(uint load, uint clock = 2500, GpuLimitReasons reasons = GpuLimitReasons.None) =>
        new(new GpuSample(65, load, 2_000_000_000, 16_000_000_000, clock, 3105, 260, 272, 50, (ulong)reasons), null);

    private static PerformanceReport Analyze(IEnumerable<PerfSample> samples) =>
        PerformanceAnalyzer.Analyze(samples.ToList(), DateTime.Now);

    [Fact]
    public void IdleReasonUnderFullLoadStillCountsAsActive()
    {
        // Vu sur RTX 4080 SUPER avec plafond + décalage : 100 % de charge, raison « Idle » à chaque seconde
        var report = Analyze(Enumerable.Repeat(Sample(100, reasons: GpuLimitReasons.Idle), 60));

        Assert.True(report.Conclusive);
        Assert.Equal(60, report.Stats.ActiveSeconds);
    }

    [Fact]
    public void LowLoadDoesNotCount()
    {
        var report = Analyze(Enumerable.Repeat(Sample(PerformanceAnalyzer.ActiveLoadPercent - 1), 60));

        Assert.False(report.Conclusive);
        Assert.Equal(0, report.Stats.ActiveSeconds);
        Assert.Equal(60, report.Stats.Seconds);
    }

    [Fact]
    public void FewerActiveSecondsThanRequiredIsInconclusive()
    {
        var samples = Enumerable.Repeat(Sample(100), PerformanceAnalyzer.MinActiveSeconds - 1)
            .Concat(Enumerable.Repeat(Sample(5), 31));

        var report = Analyze(samples);

        Assert.False(report.Conclusive);
        Assert.Equal(PerformanceAnalyzer.MinActiveSeconds - 1, report.Stats.ActiveSeconds);
    }

    [Fact]
    public void EnoughActiveSecondsIsConclusive()
    {
        var report = Analyze(Enumerable.Repeat(Sample(PerformanceAnalyzer.ActiveLoadPercent), PerformanceAnalyzer.MinActiveSeconds));

        Assert.True(report.Conclusive);
    }

    [Fact]
    public void BalancedCapIsTheSustainedClockRoundedDownToA15MHzBin()
    {
        var samples = Enumerable.Range(0, 100).Select(i => Sample(98, (uint)(2400 + i * 2)));
        var stats = Analyze(samples).Stats;
        var limits = new GpuTuningLimits(150_000, 370_000, 320_000, 210, 3105, -1000, 1000, -2000, 3000);

        var cap = TuningPlan.BalancedCap(stats, limits);

        Assert.NotNull(cap);
        Assert.Equal(0u, cap!.Value % TuningPlan.ClockBin);
        Assert.InRange(stats.SustainedClock - cap.Value, 0, TuningPlan.ClockBin - 1);
    }
}
