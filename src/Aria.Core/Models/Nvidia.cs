using Aria.Core.Nvidia;

namespace Aria.Core.Models;

/// <summary>État d'un écran vu par le pilote NVIDIA.</summary>
/// <param name="Vrr">État G-SYNC, ou null si illisible (pilote antérieur à 525, écran non piloté par NVIDIA…).</param>
/// <param name="Color">Couleurs envoyées à l'écran, ou null si illisibles.</param>
public sealed record NvidiaDisplayInfo(string DeviceName, VrrState? Vrr, ColorState? Color);

/// <summary>Ce qu'Aria peut lire du pilote NVIDIA (NVAPI).</summary>
/// <param name="Error">Raison si NVAPI est indisponible.</param>
/// <param name="DriverSettingsError">Raison si les réglages du pilote (profil global) sont illisibles.</param>
public sealed record NvidiaDriverInfo(string? Error, string? DriverSettingsError, IReadOnlyList<NvidiaDisplayInfo> Displays)
{
    public bool IsAvailable => Error is null;
    public bool CanReadSettings => Error is null && DriverSettingsError is null;

    public NvidiaDisplayInfo? Display(string deviceName) =>
        Displays.FirstOrDefault(d => string.Equals(d.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Génération de carte NVIDIA, pour les conseils DLSS.</summary>
public enum NvidiaGeneration { NoDlss, Rtx20, Rtx30, Rtx40, Rtx50, Unknown }
