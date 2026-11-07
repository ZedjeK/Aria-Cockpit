using Aria.Core.Models;
using Aria.Core.Windows;

namespace Aria.Core.Inventory;

/// <summary>Batterie d'un portable : usure (capacité actuelle comparée à l'origine), charge, secteur, cycles.</summary>
internal static class BatteryCollector
{
    public static BatteryInfo? Collect()
    {
        var design = Wmi.Query("SELECT DesignedCapacity FROM BatteryStaticData", Wmi.RootWmi)
            .Sum(b => Wmi.Long(b, "DesignedCapacity"));
        var full = Wmi.Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", Wmi.RootWmi)
            .Sum(b => Wmi.Long(b, "FullChargedCapacity"));

        // Valeurs absentes ou incohérentes (certains fabricants) : pas de santé affichée, le reste l'est quand même
        if (design <= 0 || full <= 0 || full > design * 1.3) design = full = 0;

        var (charge, onAc) = NativePower.BatteryStatus();
        int? cycles = null;
        try
        {
            var count = Wmi.Query("SELECT CycleCount FROM BatteryCycleCount", Wmi.RootWmi).Select(b => Wmi.Int(b, "CycleCount")).DefaultIfEmpty(0).Max();
            if (count > 0) cycles = count;
        }
        catch (System.Management.ManagementException) { }   // classe absente : cycles non rapportés

        if (design == 0 && charge is null) return null;
        return new BatteryInfo(design, full, charge, onAc, cycles);
    }
}
