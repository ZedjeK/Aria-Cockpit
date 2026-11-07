using System.Text.Json;
using Aria.Core.Backup;
using Aria.Core.Models;

namespace Aria.Core.Battery;

/// <summary>Limite de charge d'un fabricant : lecture, valeur conseillée et écriture.</summary>
internal interface IChargeLimitControl
{
    ChargeLimitVendor Vendor { get; }

    /// <summary>État actuel, ou null si ce portable n'expose pas sa limite de charge à Windows.</summary>
    ChargeLimitInfo? Read();

    /// <summary>Valeur conseillée, quand l'état actuel ne ménage pas la batterie.</summary>
    ChargeLimitInfo Recommended();

    /// <summary>État correspondant à une valeur sauvegardée (lève FormatException si elle est illisible).</summary>
    ChargeLimitInfo InfoFor(string raw);

    /// <summary>Valeur lisible d'une valeur sauvegardée.</summary>
    string Describe(string raw) => InfoFor(raw).Display;

    /// <summary>Ce qui empêche de régler la limite depuis Windows (mot de passe du BIOS…), ou null.</summary>
    string? Blocker();

    /// <summary>Écrit la valeur demandée. Lève une exception si le pilote ou le BIOS la refuse.</summary>
    void Write(ChargeLimitInfo current, string raw);

    /// <summary>La limite s'efface au redémarrage (ou peut être remise par le logiciel du fabricant) : Aria la réapplique à l'ouverture de session.</summary>
    bool ReappliesAtLogon { get; }

    /// <summary>Faux si l'interface ne sait pas relire la limite réellement en place (ASUS) : la réapplication écrit alors toujours.</summary>
    bool CanReadBack => true;
}

/// <summary>
/// Limite de charge de la batterie des portables : choix de l'interface selon le fabricant, et réapplication à
/// l'ouverture de session pour ASUS et Lenovo. Windows n'a pas d'interface commune : chaque fabricant a la sienne.
/// </summary>
public static class ChargeLimits
{
    /// <summary>Fabricant reconnu d'après Win32_ComputerSystem.Manufacturer, ou null.</summary>
    public static ChargeLimitVendor? VendorOf(string manufacturer)
    {
        var m = manufacturer.Trim();
        if (m.Contains("Dell", StringComparison.OrdinalIgnoreCase) || m.Contains("Alienware", StringComparison.OrdinalIgnoreCase))
            return ChargeLimitVendor.Dell;
        if (m.Equals("HP", StringComparison.OrdinalIgnoreCase) || m.StartsWith("HP ", StringComparison.OrdinalIgnoreCase)
            || m.Contains("Hewlett", StringComparison.OrdinalIgnoreCase))
            return ChargeLimitVendor.Hp;
        if (m.Contains("ASUS", StringComparison.OrdinalIgnoreCase)) return ChargeLimitVendor.Asus;
        if (m.Contains("LENOVO", StringComparison.OrdinalIgnoreCase)) return ChargeLimitVendor.Lenovo;
        return null;
    }

    internal static IChargeLimitControl Control(ChargeLimitVendor vendor) => vendor switch
    {
        ChargeLimitVendor.Dell => new DellChargeLimit(),
        ChargeLimitVendor.Hp => new HpChargeLimit(),
        ChargeLimitVendor.Asus => new AsusChargeLimit(),
        _ => new LenovoChargeLimit()
    };

    /// <summary>Lecture pour l'inventaire : portable d'un fabricant reconnu dont l'interface répond.</summary>
    public static ChargeLimitInfo? Read(HardwareProfile hw) =>
        hw.IsLaptop && VendorOf(hw.Machine.Manufacturer) is { } vendor ? Control(vendor).Read() : null;

    // ---------------------------------------------------------------- Réapplication (ASUS, Lenovo)

    public const string TaskName = @"Aria Cockpit\Limite de charge";
    public const string Argument = "--limite-charge";

    /// <summary>Valeur à réappliquer à chaque ouverture de session.</summary>
    internal sealed record Saved(ChargeLimitVendor Vendor, string Raw);

    private static string SavedPath => Path.Combine(BackupStore.DataRoot, "limite-charge.json");

    /// <summary>Valeur mémorisée pour ce fabricant, ou null.</summary>
    internal static string? SavedRaw(ChargeLimitVendor vendor)
    {
        try
        {
            return File.Exists(SavedPath) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavedPath)) is { } saved
                   && saved.Vendor == vendor ? saved.Raw : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    private static string LogPath => Path.Combine(BackupStore.DataRoot, "limite-charge.log");

    /// <summary>
    /// Après une écriture : si la limite doit être réappliquée et qu'elle ménage la batterie, elle est mémorisée et la
    /// tâche planifiée créée ; sinon (retour à la charge complète, restauration), mémoire et tâche sont supprimées.
    /// </summary>
    internal static void Remember(IChargeLimitControl control, ChargeLimitInfo applied, string exePath)
    {
        if (!control.ReappliesAtLogon) return;
        if (applied.Protects)
        {
            Directory.CreateDirectory(BackupStore.DataRoot);
            File.WriteAllText(SavedPath, JsonSerializer.Serialize(new Saved(applied.Vendor, applied.Raw)));
            // Lance la copie protégée de l'exe (Program Files), jamais l'exe dézippé : voir LogonExe
            Settings.LogonExe.CreateTask(TaskName, Argument, exePath);
        }
        else
        {
            File.Delete(SavedPath);
            if (Nvidia.StartupTask.TaskExists(TaskName)) Nvidia.StartupTask.Run($"/Delete /TN \"{TaskName}\" /F");
            Settings.LogonExe.RemoveIfUnused();
        }
    }

    /// <summary>Lancé par la tâche planifiée : réécrit la limite mémorisée si elle n'est plus en place. Sans fenêtre.</summary>
    public static void ReapplyAtLogon()
    {
        try
        {
            if (!File.Exists(SavedPath)) return;
            var saved = JsonSerializer.Deserialize<Saved>(File.ReadAllText(SavedPath))
                        ?? throw new InvalidDataException("limite-charge.json illisible.");
            var control = Control(saved.Vendor);
            var current = control.Read() ?? throw new InvalidOperationException("Interface de charge absente.");
            if (control.CanReadBack && string.Equals(current.Raw, saved.Raw, StringComparison.OrdinalIgnoreCase)) return;
            control.Write(current, saved.Raw);
            Log($"Limite de charge {VendorNames.Of(saved.Vendor)} réappliquée : {control.InfoFor(saved.Raw).Display}" +
                (control.CanReadBack ? $" (était : {current.Display})." : "."));
        }
        catch (Exception e)
        {
            Log($"Limite de charge non réappliquée : {e.Message}");
        }
    }

    private static void Log(string text)
    {
        try
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Journal facultatif
        }
    }
}
