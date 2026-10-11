using Aria.Core.Models;

namespace Aria.Core.Inventory;

internal static class MachineCollector
{
    // Codes SMBIOS "ChassisTypes"
    private static readonly HashSet<int> LaptopChassis = [8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32];
    private static readonly HashSet<int> DesktopChassis = [3, 4, 5, 6, 7, 13, 15, 16, 24, 35, 36];

    public static MachineInfo Collect()
    {
        var system = Wmi.Query("SELECT Manufacturer, Model FROM Win32_ComputerSystem").FirstOrDefault();
        var manufacturer = system is null ? "Inconnu" : Wmi.Str(system, "Manufacturer");
        var model = system is null ? "Inconnu" : Wmi.Str(system, "Model");

        var chassis = Wmi.Query("SELECT ChassisTypes FROM Win32_SystemEnclosure")
            .SelectMany(e => Wmi.Raw(e, "ChassisTypes") is ushort[] types ? types.Select(t => (int)t) : Enumerable.Empty<int>())
            .ToList();

        var hasBattery = Wmi.Query("SELECT DeviceID FROM Win32_Battery").Count > 0;

        // Le type de boîtier passe avant la batterie : un onduleur USB apparaît aussi comme "batterie".
        var type = chassis.Any(LaptopChassis.Contains) ? MachineType.Laptop
                 : chassis.Any(DesktopChassis.Contains) ? MachineType.Desktop
                 : hasBattery ? MachineType.Laptop
                 : MachineType.Desktop;

        return new MachineInfo(manufacturer, model, type, hasBattery);
    }
}
