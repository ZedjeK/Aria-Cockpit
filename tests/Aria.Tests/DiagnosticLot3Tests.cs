using Aria.Core.Diagnostics.Checks;
using Aria.Core.Models;
using Aria.Core.Nvidia;

namespace Aria.Tests;

/// <summary>Finitions du diagnostic (audit du 26/10/2026) : mémoire soudée, G-SYNC.</summary>
public class DiagnosticLot3Tests
{
    private static MemoryModule Module(int smbiosType) =>
        new("Samsung", "K3KL8L80CM-MGCT", 16UL << 30, 7500, 7500, smbiosType, "Controller0-ChannelA", "BANK 0");

    [Theory]
    [InlineData(35, true)]    // LPDDR5
    [InlineData(30, true)]    // LPDDR4
    [InlineData(34, false)]   // DDR5 en barrette
    [InlineData(26, false)]   // DDR4
    public void SolderedMemoryIsRecognised(int smbiosType, bool soldered)
    {
        Assert.Equal(soldered, MemoryChannelCheck.IsSoldered(Module(smbiosType)));
    }

    [Fact]
    public void SingleSolderedModuleIsNotSingleChannelWarning()
    {
        var findings = new MemoryChannelCheck().Run(Context([Module(35)])).ToList();

        var finding = Assert.Single(findings);
        Assert.Equal(Severity.Ok, finding.Severity);
        Assert.Contains("soudée", finding.Title);
    }

    [Fact]
    public void SingleRemovableModuleStillWarns()
    {
        var finding = Assert.Single(new MemoryChannelCheck().Run(Context([Module(34)])));

        Assert.Equal(Severity.Warning, finding.Severity);
    }

    private static Aria.Core.Diagnostics.DiagnosticContext Context(MemoryModule[] modules)
    {
        var hw = new HardwareProfile { Memory = new MemoryInfo(modules) };
        return new(hw, Aria.Core.Diagnostics.BiosGuide.Load(hw.Board, hw.Warnings));
    }

    // ---- G-SYNC : actif seulement si l'écran le permet ----

    [Theory]
    [InlineData(false, false, true, false, false)]  // G-SYNC demandé globalement, écran sans VRR : pas actif
    [InlineData(false, true, true, false, true)]    // demandé sur un écran qui le permet
    [InlineData(false, true, false, true, true)]    // la tour : possible, en cours de VRR
    [InlineData(true, true, false, false, true)]
    [InlineData(false, false, false, false, false)]
    public void GsyncIsOnlyOnWhenPossible(bool enabled, bool possible, bool requested, bool inVrr, bool on)
    {
        Assert.Equal(on, new VrrState(enabled, possible, requested, inVrr).IsOn);
    }
}
