using Aria.Core.Diagnostics;
using Aria.Core.Diagnostics.Checks;
using Aria.Core.Models;

namespace Aria.Tests;

/// <summary>Précision du diagnostic (audit du 26/10/2026) : vitesses de RAM, disque plein, PCIe, température des SSD.</summary>
public class DiagnosticLot2Tests
{
    // ---- Vitesse de la RAM : MHz → MT/s selon la génération ----

    [Theory]
    [InlineData(1600, "DDR4", 3200)]   // DDR4-3200 rapportée en MHz : auparavant laissée à 1600
    [InlineData(1800, "DDR4", 3600)]
    [InlineData(1066, "DDR4", 2133)]   // 2132 arrondi à la vitesse commerciale
    [InlineData(3200, "DDR4", 3200)]
    [InlineData(2400, "DDR5", 4800)]
    [InlineData(4000, "DDR5", 4000)]   // la tour : déjà en MT/s
    [InlineData(5600, "DDR5", 5600)]
    [InlineData(0, "DDR5", 0)]
    public void ConfiguredSpeedIsConvertedPerGeneration(int raw, string generation, int expected)
    {
        Assert.Equal(expected, MemoryParser.NormalizeSpeed(raw, generation));
    }

    [Theory]
    [InlineData("KVR32N22S8/16", true)]             // Kingston ValueRAM
    [InlineData("CT16G56C46U5", true)]              // Crucial standard DDR5-5600
    [InlineData("M323R2GA3BB0-CQKOL", true)]        // Samsung
    [InlineData("HMCG78MEBUA081N", true)]           // SK hynix
    [InlineData("MTC8C1084S1UC48BA1", true)]        // Micron
    [InlineData("CMT32GX5M2B5200C40", false)]       // Corsair de la tour (profil XMP)
    [InlineData("F5-6000J3038F16G", false)]         // G.Skill
    [InlineData("KF560C36BBE-16", false)]           // Kingston Fury
    [InlineData("CP16G60C36U5B", false)]            // Crucial Pro (XMP/EXPO)
    public void JedecOnlyModulesAreRecognised(string partNumber, bool jedecOnly)
    {
        Assert.Equal(jedecOnly, MemoryParser.IsJedecOnly(partNumber));
    }

    // ---- Disque système presque plein ----

    [Theory]
    [InlineData(15, 0.30, true)]    // moins de 20 Go
    [InlineData(40, 0.08, true)]    // moins de 10 % et moins de 50 Go
    [InlineData(177, 0.095, false)] // disque de 2 To avec 190 Go (au sens décimal) : pas « presque plein »
    [InlineData(492, 0.53, false)]  // la tour
    public void SystemDriveNearlyFull(double freeGb, double ratio, bool nearlyFull)
    {
        Assert.Equal(nearlyFull, StorageCheck.NearlyFull(freeGb, ratio));
    }

    // ---- PCIe ----

    [Theory]
    [InlineData("NVIDIA GeForce GT 1030", 4)]
    [InlineData("NVIDIA GeForce RTX 5060 Ti", 8)]
    [InlineData("NVIDIA GeForce RTX 4060", 8)]
    [InlineData("NVIDIA GeForce RTX 4080 SUPER", 16)]
    public void NativeLaneCount(string gpu, int lanes)
    {
        Assert.Equal(lanes, PcieLinkCheck.NativeWidth(gpu));
    }

    [Theory]
    [InlineData(8, 4, Severity.Info)]      // RTX 5060 (Gen5 x8) sur carte mère Gen4 : 1 à 2 %
    [InlineData(8, 3, Severity.Warning)]   // RTX 4060 (Gen4 x8) en Gen3 : perte nette
    [InlineData(4, 3, Severity.Warning)]
    [InlineData(16, 3, Severity.Info)]     // x16 : impact faible
    public void PcieGenerationSeverity(int lanes, int linkGen, Severity expected)
    {
        Assert.Equal(expected, PcieLinkCheck.GenSeverity(lanes, linkGen));
    }

    // ---- Température des SSD ----

    private static DiskInfo Ssd(int? temperatureMax) =>
        new(0, "SSD", StorageMedia.Ssd, StorageBus.NVMe, 1_000_000_000_000, ['C'], 3, 72, 1000, temperatureMax);

    [Fact]
    public void DiskOwnMaximumIsTheThreshold()
    {
        Assert.Equal(84, SsdHealthCheck.HotThreshold(Ssd(84)));
    }

    [Fact]
    public void WithoutMaximumTheThresholdIs75()
    {
        // 72 °C pendant un téléchargement sur un NVMe Gen4 : plus d'alerte
        Assert.Equal(75, SsdHealthCheck.HotThreshold(Ssd(null)));
        Assert.True(Ssd(null).TemperatureC < SsdHealthCheck.HotThreshold(Ssd(null)));
    }
}
