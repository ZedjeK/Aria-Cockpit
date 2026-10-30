using Aria.Bench;
using Aria.Core.Bench;

namespace Aria.Tests;

/// <summary>Contrôle en ray tracing de la validation : réservé aux RTX, il juge la stabilité sans toucher aux FPS mesurés en Ultra.</summary>
public class RayTracingCheckTests
{
    private static BenchResult Pass(string quality, double fps, int errors) =>
        new("terminé", [new BenchPass(quality, "1440p", 8000, fps, fps, fps * 0.9, 0, 19, errors, errors > 0 ? 1200 : 0, null, 130)], [], []);

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4080 SUPER", true)]
    [InlineData("NVIDIA GeForce RTX 2060", true)]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER", false)]
    [InlineData("NVIDIA GeForce GTX 1080 Ti", false)]
    // Carte plus récente que le catalogue : le nom décide
    [InlineData("NVIDIA GeForce RTX 6090", true)]
    [InlineData("NVIDIA GeForce GTX 2690", false)]
    public void OnlyRtxCardsGetTheRayTracingCheck(string gpu, bool expected)
    {
        Assert.Equal(expected, BenchRunner.SupportsRayTracing(gpu));
    }

    [Fact]
    public void RayTracingCheckRunsAPresetTheBenchKnows()
    {
        Assert.Same(QualityPreset.UltraRt, QualityPreset.Find(BenchRunner.RayTracedQuality));
        Assert.Equal(QualityPreset.UltraRt.Label, BenchRunner.RayTracedLabel);
    }

    [Fact]
    public void WrongRayTracedImageMakesTheValidationUnstable()
    {
        var merged = BenchResult.Merge(Pass("Ultra", 104.8, 0), Pass(BenchRunner.RayTracedLabel, 67.5, 1));

        Assert.True(merged.Unstable);
        Assert.True(merged.RayTracingErrors);
        // Les FPS restent ceux du banc Ultra
        Assert.Equal(104.8, merged.Pass!.FpsMoyen);
    }

    [Fact]
    public void CleanRayTracingCheckKeepsTheUltraMeasurement()
    {
        var merged = BenchResult.Merge(Pass("Ultra", 104.8, 0), Pass(BenchRunner.RayTracedLabel, 67.5, 0));

        Assert.False(merged.Unstable);
        Assert.False(merged.RayTracingErrors);
        Assert.Equal("Ultra", merged.Pass!.Qualite);
    }

    [Fact]
    public void UltraErrorsAreNotBlamedOnRayTracing()
    {
        var merged = BenchResult.Merge(Pass("Ultra", 104.8, 2), Pass(BenchRunner.RayTracedLabel, 67.5, 0));

        Assert.True(merged.Unstable);
        Assert.False(merged.RayTracingErrors);
    }
}
