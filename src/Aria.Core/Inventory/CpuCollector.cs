using Aria.Core.Models;

namespace Aria.Core.Inventory;

internal static class CpuCollector
{
    public static CpuInfo Collect()
    {
        var cpus = Wmi.Query(
            "SELECT Name, Manufacturer, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed FROM Win32_Processor");
        if (cpus.Count == 0) return CpuInfo.Unknown;

        var first = cpus[0];
        var (performance, efficient) = CpuTopology.ReadCoreTypes();
        return new CpuInfo(
            Name: Wmi.Str(first, "Name"),
            Manufacturer: Wmi.Str(first, "Manufacturer"),
            Cores: cpus.Sum(c => Wmi.Int(c, "NumberOfCores")),            // somme si plusieurs sockets
            Threads: cpus.Sum(c => Wmi.Int(c, "NumberOfLogicalProcessors")),
            MaxClockMHz: Wmi.Int(first, "MaxClockSpeed"),
            PerformanceCores: performance,
            EfficientCores: efficient,
            Microcode: CpuTopology.ReadMicrocode(),
            Model: CpuTopology.ReadModel());
    }
}
