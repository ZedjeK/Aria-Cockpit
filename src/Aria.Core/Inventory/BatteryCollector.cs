using Aria.Core.Models;

namespace Aria.Core.Inventory;

/// <summary>Usure de la batterie d'un portable : capacité actuelle comparée à la capacité d'origine.</summary>
internal static class BatteryCollector
{
    public static BatteryInfo? Collect()
    {
        var design = Wmi.Query("SELECT DesignedCapacity FROM BatteryStaticData", Wmi.RootWmi)
            .Sum(b => Wmi.Long(b, "DesignedCapacity"));
        var full = Wmi.Query("SELECT FullChargedCapacity FROM BatteryFullChargedCapacity", Wmi.RootWmi)
            .Sum(b => Wmi.Long(b, "FullChargedCapacity"));

        // Valeurs absentes ou incohérentes (certains fabricants) : on n'affiche rien
        if (design <= 0 || full <= 0 || full > design * 1.3) return null;
        return new BatteryInfo(design, full);
    }
}
