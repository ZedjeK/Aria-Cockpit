using Aria.Core.Models;

namespace Aria.Core.Inventory;

internal static class MemoryCollector
{
    public static MemoryInfo Collect()
    {
        var modules = Wmi.Query(
                "SELECT Manufacturer, PartNumber, Capacity, Speed, ConfiguredClockSpeed, SMBIOSMemoryType, DeviceLocator, BankLabel FROM Win32_PhysicalMemory")
            .Select(m => new MemoryModule(
                Manufacturer: Wmi.Str(m, "Manufacturer"),
                PartNumber: Wmi.Str(m, "PartNumber"),
                CapacityBytes: Wmi.ULong(m, "Capacity"),
                RatedSpeedMTs: Wmi.Int(m, "Speed"),
                ConfiguredSpeedMTs: Wmi.Int(m, "ConfiguredClockSpeed"),
                SmbiosType: Wmi.Int(m, "SMBIOSMemoryType"),
                DeviceLocator: Wmi.Str(m, "DeviceLocator"),
                BankLabel: Wmi.Str(m, "BankLabel")))
            .ToList();

        return new MemoryInfo(modules);
    }
}
