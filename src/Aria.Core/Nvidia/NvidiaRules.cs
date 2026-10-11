using System.Text.RegularExpressions;
using Aria.Core.Models;

namespace Aria.Core.Nvidia;

/// <summary>Règles de décision NVIDIA, séparées des appels au pilote pour être testables.</summary>
public static partial class NvidiaRules
{
    /// <summary>Marge sous la fréquence maximale avec G-SYNC (Blur Busters, « G-SYNC 101 » : au moins 3 images/s).</summary>
    public const int GsyncMargin = 3;

    /// <summary>Écran principal, s'il est piloté par la carte NVIDIA.</summary>
    public static DisplayInfo? PrimaryNvidiaDisplay(HardwareProfile hw) =>
        hw.Displays.FirstOrDefault(d => d.IsPrimary && IsNvidiaDriven(d));

    public static bool IsNvidiaDriven(DisplayInfo display) =>
        display.AdapterName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

    /// <summary>État G-SYNC de l'écran principal : vrai, faux, ou null si illisible.</summary>
    public static bool? PrimaryGsyncOn(HardwareProfile hw)
    {
        if (PrimaryNvidiaDisplay(hw) is not { } primary) return false;
        return hw.Nvidia?.Display(primary.DeviceName)?.Vrr?.IsOn;
    }

    /// <summary>Limite d'images par seconde avec G-SYNC : fréquence maximale moins la marge (ex. 144 Hz → 141).</summary>
    public static int FrameCap(int maxRefreshHz) => maxRefreshHz - GsyncMargin;

    /// <summary>Raison pour laquelle les réglages G-SYNC ne s'appliquent pas, ou null s'ils s'appliquent.</summary>
    public static string? WhyNoGsync(HardwareProfile hw)
    {
        if (PrimaryNvidiaDisplay(hw) is not { } primary) return "Écran principal non relié à la carte NVIDIA";
        return PrimaryGsyncOn(hw) switch
        {
            null => "État G-SYNC illisible (pilote antérieur à la version 525 ?) : laissé au choix du jeu",
            false => $"G-SYNC inactif sur {primary.MonitorName} : laissé au choix du jeu",
            true when primary.MaxRefreshHz <= GsyncMargin + 30 => "Fréquence de l'écran illisible",
            _ => null
        };
    }

    /// <summary>Portable à deux puces graphiques (Optimus) : puce intégrée + carte NVIDIA.</summary>
    public static bool IsOptimusLaptop(HardwareProfile hw) =>
        hw.IsLaptop && hw.HasNvidiaGpu && hw.Gpus.Any(g => g.IsIntegrated);

    [GeneratedRegex(@"RTX\s*(?:PRO\s*)?(\d{2})\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex RtxNumber();

    /// <summary>Génération d'après le nom (« NVIDIA GeForce RTX 4080 SUPER » → RTX 40).</summary>
    public static NvidiaGeneration Generation(string gpuName)
    {
        var match = RtxNumber().Match(gpuName);
        if (match.Success)
            return match.Groups[1].Value switch
            {
                "20" => NvidiaGeneration.Rtx20,
                "30" => NvidiaGeneration.Rtx30,
                "40" => NvidiaGeneration.Rtx40,
                "50" => NvidiaGeneration.Rtx50,
                _ => NvidiaGeneration.Unknown
            };
        return gpuName.Contains("GTX", StringComparison.OrdinalIgnoreCase) || gpuName.Contains(" GT ", StringComparison.OrdinalIgnoreCase)
               || gpuName.Contains("MX", StringComparison.Ordinal)
            ? NvidiaGeneration.NoDlss
            : NvidiaGeneration.Unknown;
    }

    /// <summary>Taille en Mo du cache des shaders, lisible.</summary>
    public static string DescribeCacheSize(uint megabytes) => megabytes switch
    {
        0 => "Désactivé",
        DrsIds.ShaderCacheUnlimited => "Illimité",
        >= 1024 when megabytes % 1024 == 0 => $"{megabytes / 1024} Go",
        >= 1024 => $"{megabytes / 1024.0:0.#} Go",
        _ => $"{megabytes} Mo"
    };
}
