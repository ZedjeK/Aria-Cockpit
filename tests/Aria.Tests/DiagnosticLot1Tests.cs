using Aria.Core.Diagnostics.Checks;
using Aria.Core.Inventory;
using Aria.Core.Models;

namespace Aria.Tests;

/// <summary>Fausses alertes graves du diagnostic (audit du 26/10/2026) : microcode, écrans, Resizable BAR, usure SSD, eDP.</summary>
public class DiagnosticLot1Tests
{
    // ---- Microcode Intel : seules les puces Raptor Lake (CPUID modèle 183) sont concernées ----

    [Theory]
    [InlineData("Intel64 Family 6 Model 183 Stepping 1", 183)]   // i9-13900K, i7-14700K
    [InlineData("Intel64 Family 6 Model 191 Stepping 2", 191)]   // i5-13400F sur puce Alder Lake
    [InlineData("Intel64 Family 6 Model 151 Stepping 2", 151)]   // i7-12700K de la tour
    [InlineData("AMD64 Family 25 Model 33 Stepping 2", 33)]
    public void CpuModelIsReadFromTheWindowsIdentifier(string identifier, int model)
    {
        Assert.Equal(model, CpuTopology.ParseModel(identifier));
    }

    [Fact]
    public void UnreadableIdentifierGivesNoModel()
    {
        Assert.Null(CpuTopology.ParseModel(null));
        Assert.Null(CpuTopology.ParseModel("Processeur inconnu"));
    }

    [Theory]
    [InlineData(183, true)]
    [InlineData(191, false)]   // i5 sans K sur puce Alder Lake : microcode 0x3x, jamais 0x12B
    [InlineData(151, false)]
    [InlineData(null, true)]   // modèle illisible : on reste prudent
    public void MicrocodeCheckOnlyAppliesToRaptorLakeDies(int? model, bool applies)
    {
        Assert.Equal(applies, MicrocodeCheck.IsRaptorLakeDie(model));
    }

    // ---- Écran « branché sur la carte mère » : seulement la vraie puce graphique intégrée ----

    private static readonly GpuInfo[] TowerGpus =
    [
        new("Intel(R) UHD Graphics 770", GpuVendor.Intel, 0, "32.0.101.7088", null, null, "PCI\\VEN_8086"),
        new("NVIDIA GeForce RTX 4080 SUPER", GpuVendor.Nvidia, 16UL << 30, "32.0.16.2101", "621.01", null, "PCI\\VEN_10DE")
    ];

    private static DisplayInfo Screen(string adapter) =>
        new(@"\\.\DISPLAY1", "Écran", adapter, true, 2560, 1440, 144, 144, 2560, 1440);

    [Fact]
    public void ScreenOnTheIntegratedGpuIsFlagged()
    {
        Assert.True(DisplayConnectionCheck.OnIntegratedGpu(Screen("Intel(R) UHD Graphics 770"), TowerGpus));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4080 SUPER")]
    [InlineData("Microsoft Remote Display Adapter")]      // Bureau à distance
    [InlineData("Parsec Virtual Display Adapter")]        // écran virtuel de streaming
    [InlineData("DisplayLink USB Device")]                // dock USB
    [InlineData("Microsoft Basic Display Adapter")]
    public void OtherAdaptersAreNotTheMotherboard(string adapter)
    {
        Assert.False(DisplayConnectionCheck.OnIntegratedGpu(Screen(adapter), TowerGpus));
    }

    // ---- Resizable BAR : plateforme prise en charge par NVIDIA ----

    [Theory]
    [InlineData("12th Gen Intel(R) Core(TM) i7-12700K", true)]
    [InlineData("Intel(R) Core(TM) i5-10400F CPU @ 2.90GHz", true)]
    [InlineData("Intel(R) Core(TM) Ultra 7 265K", true)]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", true)]
    [InlineData("AMD Ryzen 9 7950X 16-Core Processor", true)]
    [InlineData("Intel(R) Core(TM) i7-8700K CPU @ 3.70GHz", false)]
    [InlineData("AMD Ryzen 5 2600 Six-Core Processor", false)]
    [InlineData("AMD Ryzen 7 3700X 8-Core Processor", null)]   // selon la carte mère
    [InlineData("Intel(R) Xeon(R) W-2245 CPU", null)]
    public void RebarPlatformSupport(string cpu, bool? supported)
    {
        Assert.Equal(supported, ResizableBarCheck.PlatformSupportsRebar(cpu));
    }

    // ---- Usure des SSD : au-delà de 100 %, c'est le cas le plus grave ----

    [Theory]
    [InlineData(3, 3)]
    [InlineData(100, 100)]
    [InlineData(110, 110)]   // NVMe au-delà de sa durée prévue
    [InlineData(254, 254)]
    public void WearIsKeptUpTo254(int raw, int expected)
    {
        Assert.Equal(expected, StorageCollector.UsableWear(raw));
    }

    [Theory]
    [InlineData(255)]
    [InlineData(-1)]
    [InlineData(null)]
    public void WearNotProvidedIsDropped(int? raw)
    {
        Assert.Null(StorageCollector.UsableWear(raw));
    }

    // ---- Écran intégré des portables (eDP) ----

    [Theory]
    [InlineData(11, DisplayConnection.Internal)]      // DISPLAYPORT_EMBEDDED
    [InlineData(13, DisplayConnection.Internal)]      // UDI_EMBEDDED
    [InlineData(10, DisplayConnection.DisplayPort)]   // DisplayPort externe
    [InlineData(18, DisplayConnection.DisplayPort)]   // USB-C (tunnel DisplayPort)
    [InlineData(5, DisplayConnection.Hdmi)]
    public void OutputTechnologyIsClassified(int technology, DisplayConnection expected)
    {
        Assert.Equal(expected, DisplayConfig.Classify(technology));
    }
}
