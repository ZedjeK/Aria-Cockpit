using Aria.Core.Diagnostics;
using Aria.Core.Diagnostics.Checks;
using Aria.Core.Models;

namespace Aria.Tests;

/// <summary>Conseil détaillé pour 4 barrettes DDR5 (cas de la tour : 4 × 16 Go Corsair 5200 à 4000 MT/s sur i7-12700K).</summary>
public class MemoryFourDimmTests
{
    private static DiagnosticContext Tower()
    {
        var modules = new[] { "Controller0-DIMMA1", "Controller0-DIMMA2", "Controller1-DIMMB1", "Controller1-DIMMB2" }
            .Select(slot => new MemoryModule("Corsair", "CMT32GX5M2B5200C40", 16UL << 30, 4000, 4000, 34, slot, "BANK"))
            .ToArray();
        var hw = new HardwareProfile
        {
            Memory = new MemoryInfo(modules),
            Cpu = new CpuInfo("12th Gen Intel(R) Core(TM) i7-12700K", "GenuineIntel", 12, 20, 3600, 8, 4, null, 151)
        };
        return new DiagnosticContext(hw, BiosGuide.Load(hw.Board, hw.Warnings));
    }

    [Fact]
    public void TowerGetsADetailedInfoNotAWarning()
    {
        var finding = Assert.Single(new MemorySpeedCheck().Run(Tower()));

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("kit vendu pour 5200", finding.Title);
        Assert.Contains("3600 à 4000 MT/s", finding.Detail);
        Assert.Contains("12e génération", finding.Detail);
        Assert.Contains("plusieurs kits de 2", finding.Detail);
        Assert.Contains("ne garder que 2 barrettes", finding.Advice);
    }

    [Fact]
    public void IntelStepsIncludeCautiousVoltages()
    {
        var steps = MemorySpeedCheck.FourDimmSteps(intel: true, "XMP");

        Assert.Equal(5, steps.Count);
        Assert.StartsWith("1. Dans le BIOS, laisser le profil XMP désactivé", steps[0]);
        Assert.Contains("1,35 V", steps[4]);
        Assert.Contains("System Agent", steps[4]);
    }

    [Fact]
    public void AmdStepsLeaveIntelVoltagesOut()
    {
        var steps = MemorySpeedCheck.FourDimmSteps(intel: false, "EXPO");

        Assert.Equal(4, steps.Count);
        Assert.DoesNotContain(steps, s => s.Contains("System Agent"));
    }
}
