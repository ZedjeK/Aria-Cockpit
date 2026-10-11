using Aria.Core.Models;

namespace Aria.Core.Inventory;

/// <summary>
/// Inventaire informatif des périphériques. Les réglages propres aux marques
/// (fréquence d'interrogation de la souris, etc.) passent par leurs logiciels et restent hors périmètre.
/// </summary>
internal static class PeripheralCollector
{
    private static readonly string[] ControllerKeywords = ["game controller", "gamepad", "manette", "contrôleur de jeu", "controller"];

    public static PeripheralInfo Collect()
    {
        var devices = Wmi.Query(
                "SELECT Name, PNPClass FROM Win32_PnPEntity " +
                "WHERE Status = 'OK' AND (PNPClass = 'Mouse' OR PNPClass = 'Keyboard' OR PNPClass = 'XnaComposite' OR PNPClass = 'HIDClass')")
            .Select(d => (Name: Wmi.Str(d, "Name"), Class: Wmi.Str(d, "PNPClass")))
            .Where(d => d.Name.Length > 0)
            .ToList();

        var audio = Wmi.Query("SELECT Name FROM Win32_SoundDevice WHERE Status = 'OK'")
            .Select(d => Wmi.Str(d, "Name"));

        return new PeripheralInfo(
            Mice: Distinct(devices.Where(d => d.Class == "Mouse").Select(d => d.Name)),
            Keyboards: Distinct(devices.Where(d => d.Class == "Keyboard").Select(d => d.Name)),
            GameControllers: Distinct(devices
                .Where(d => d.Class == "XnaComposite"
                         || (d.Class == "HIDClass" && ControllerKeywords.Any(k => d.Name.Contains(k, StringComparison.OrdinalIgnoreCase))))
                .Select(d => d.Name)),
            AudioDevices: Distinct(audio),
            Printers: Printers());
    }

    private static readonly string[] VirtualPrinterWords = ["PDF", "XPS", "OneNote", "Fax", "Send To", "Envoyer vers"];
    private static readonly string[] VirtualPorts = ["PORTPROMPT:", "NUL:", "SHRFAX:", "XPSPORT:", "FILE:"];

    /// <summary>Imprimantes réelles : les imprimantes virtuelles (Imprimer en PDF, XPS, OneNote, Fax) sont ignorées.</summary>
    private static IReadOnlyList<string>? Printers()
    {
        try
        {
            return Distinct(Wmi.Query("SELECT Name, PortName FROM Win32_Printer")
                .Select(p => (Name: Wmi.Str(p, "Name"), Port: Wmi.Str(p, "PortName").ToUpperInvariant()))
                .Where(p => !VirtualPrinterWords.Any(w => p.Name.Contains(w, StringComparison.OrdinalIgnoreCase)))
                .Where(p => !VirtualPorts.Contains(p.Port) && !p.Port.StartsWith("ONENOTE", StringComparison.Ordinal))
                .Select(p => p.Name));
        }
        catch (Exception)
        {
            return null; // spouleur arrêté ou WMI indisponible : inconnu, le spouleur ne sera pas proposé
        }
    }

    private static List<string> Distinct(IEnumerable<string> names) =>
        names.Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
}
