using Aria.Core.Models;

namespace Aria.Core.Inventory;

/// <summary>
/// Point d'entrée de l'inventaire. Chaque partie est isolée :
/// si une détection échoue, on note un avertissement et on continue.
/// </summary>
public static class HardwareInventory
{
    public static HardwareProfile Collect()
    {
        var hw = new HardwareProfile();

        hw.Os = Safe(hw, "système d'exploitation", OsCollector.Collect, OsInfo.Unknown);
        hw.Machine = Safe(hw, "type de machine", MachineCollector.Collect, MachineInfo.Unknown);
        hw.Board = Safe(hw, "carte mère", BoardCollector.Collect, BoardInfo.Unknown);
        hw.Firmware = Safe(hw, "firmware", () => FirmwareCollector.Collect(hw.Warnings), FirmwareInfo.Unknown);
        hw.Cpu = Safe(hw, "processeur", CpuCollector.Collect, CpuInfo.Unknown);
        hw.Gpus = Safe(hw, "cartes graphiques", GpuCollector.Collect, []);
        if (hw.HasNvidiaGpu)
            hw.NvidiaLink = Safe(hw, "lien PCIe NVIDIA", () => NvidiaSmiCollector.Collect(hw.Warnings), null);
        hw.Memory = Safe(hw, "mémoire", MemoryCollector.Collect, MemoryInfo.Unknown);
        hw.Disks = Safe(hw, "stockage", StorageCollector.Collect, []);
        hw.Displays = Safe(hw, "écrans", () => DisplayCollector.Collect(hw.Warnings), []);
        if (hw.HasNvidiaGpu)
            hw.Nvidia = Safe(hw, "pilote NVIDIA (NVAPI)", () => NvidiaCollector.Collect(hw.Displays, hw.Warnings), null);
        hw.Peripherals = Safe(hw, "périphériques", PeripheralCollector.Collect, PeripheralInfo.Unknown);
        hw.Network = Safe(hw, "réseau", NetworkCollector.Collect, []);
        hw.SystemState = Safe(hw, "état de Windows", SystemStateCollector.Collect, SystemStateInfo.Unknown);
        if (hw.Machine.HasBattery)
            hw.Battery = Safe(hw, "batterie", BatteryCollector.Collect, null);

        return hw;
    }

    private static T Safe<T>(HardwareProfile hw, string part, Func<T> collect, T fallback)
    {
        try
        {
            return collect();
        }
        catch (Exception e)
        {
            hw.Warnings.Add($"Détection {part} impossible : {e.Message}");
            return fallback;
        }
    }
}
