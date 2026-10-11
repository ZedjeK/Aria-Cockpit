using System.Management;
using Microsoft.Win32;
using Aria.Core.Inventory;

namespace Aria.Core.Backup;

/// <summary>Point de restauration Windows : filet de sécurité en plus de la sauvegarde JSON.</summary>
public static class RestorePoint
{
    private const string FrequencyKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";
    private const string FrequencyValue = "SystemRestorePointCreationFrequency";

    /// <returns>Message lisible pour le rapport.</returns>
    public static (bool Created, string Message) TryCreate(string description)
    {
        if (SystemStateCollector.IsSystemProtectionEnabled() == false)
            return (false, "Point de restauration impossible : la protection du système est désactivée sur le disque de Windows.");

        // Nom unique : on retrouve ensuite CE point précis, au lieu de compter les points
        // (Windows peut supprimer un ancien point en créant le nouveau, et le total ne bouge pas).
        var name = $"{description} ({DateTime.Now:HH:mm:ss})";

        // Windows refuse un 2e point de restauration en moins de 24 h : on lève cette limite
        // le temps de l'appel, puis on remet exactement la valeur d'origine.
        object? previous = null;
        RegistryKey? key = null;
        try
        {
            key = Registry.LocalMachine.CreateSubKey(FrequencyKey, writable: true);
            previous = key.GetValue(FrequencyValue);
            key.SetValue(FrequencyValue, 0, RegistryValueKind.DWord);

            using var restore = new ManagementClass(@"\\.\root\default", "SystemRestore", null);
            var result = Convert.ToUInt32(restore.InvokeMethod("CreateRestorePoint", [name, 12u, 100u]));
            // 12 = modification de paramètres, 100 = début de modification système

            if (result != 0)
                return (false, result == 1058
                    ? "Point de restauration impossible : le service de protection du système est désactivé."
                    : $"Point de restauration refusé par Windows (code {result}).");

            // Windows peut mettre quelques secondes à faire apparaître le point dans la liste
            for (var attempt = 0; attempt < 15; attempt++)
            {
                if (Exists(name)) return (true, $"Point de restauration Windows créé : « {name} ».");
                Thread.Sleep(1000);
            }
            return (false, $"Windows a accepté la demande mais le point « {name} » n'est pas encore visible. " +
                           "Vérifiez dans « Créer un point de restauration » → « Restauration du système ».");
        }
        catch (Exception e)
        {
            return (false, $"Point de restauration impossible : {e.Message}");
        }
        finally
        {
            if (key is not null)
            {
                if (previous is null) key.DeleteValue(FrequencyValue, throwOnMissingValue: false);
                else key.SetValue(FrequencyValue, previous, RegistryValueKind.DWord);
                key.Dispose();
            }
        }
    }

    private static bool Exists(string description)
    {
        using var searcher = new ManagementObjectSearcher(@"root\default", "SELECT Description FROM SystemRestore");
        return searcher.Get().Cast<ManagementBaseObject>()
            .Any(p => string.Equals(p["Description"]?.ToString(), description, StringComparison.Ordinal));
    }
}
