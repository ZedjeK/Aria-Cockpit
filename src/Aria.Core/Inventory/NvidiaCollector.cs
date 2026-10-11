using Aria.Core.Models;
using Aria.Core.Nvidia;

namespace Aria.Core.Inventory;

/// <summary>Lecture seule via NVAPI : G-SYNC et couleurs de chaque écran NVIDIA, accès aux réglages du pilote.</summary>
internal static class NvidiaCollector
{
    public static NvidiaDriverInfo Collect(IReadOnlyList<DisplayInfo> displays, List<string> warnings)
    {
        if (NvApi.EnsureReady() is { } error)
        {
            warnings.Add($"NVAPI indisponible : {error}. Réglages NVIDIA non proposés.");
            return new NvidiaDriverInfo(error, null, []);
        }

        // Accès aux réglages du pilote : une lecture d'essai dans le profil global
        string? settingsError = null;
        try
        {
            NvApiDriverSettings.Instance.Read(DrsIds.VSync);
        }
        catch (Exception e)
        {
            settingsError = e.Message;
            warnings.Add($"Réglages du pilote NVIDIA illisibles : {e.Message}");
        }

        var api = NvApiDisplays.Instance;
        var states = new List<NvidiaDisplayInfo>();
        foreach (var display in displays.Where(NvidiaRules.IsNvidiaDriven))
        {
            ColorState? color = null;
            try
            {
                color = api.ReadColor(display.DeviceName);
            }
            catch (Exception e)
            {
                warnings.Add($"Couleurs de {display.MonitorName} illisibles via NVAPI : {e.Message}");
            }
            states.Add(new NvidiaDisplayInfo(display.DeviceName, api.ReadVrr(display.DeviceName), color));
        }
        return new NvidiaDriverInfo(null, settingsError, states);
    }
}
