using Aria.Core.Models;

namespace Aria.Core.Reporting;

/// <summary>Destination de l'inventaire : texte (console, rapport) ou fiche HTML.</summary>
public interface IInventorySink
{
    void Section(string title);
    void Line(string label, string value);
    void Warning(string text);
}

/// <summary>Inventaire matériel, écrit dans un rapport texte, la console ou la fiche HTML.</summary>
public static class InventoryPrinter
{
    public static void Print(HardwareProfile hw, TextWriter output)
    {
        Write(hw, new TextSink(output));
        output.WriteLine();
    }

    public static void Write(HardwareProfile hw, IInventorySink output)
    {
        output.Section("SYSTÈME");
        output.Line("Windows", $"{hw.Os.Name} {hw.Os.DisplayVersion} (build {hw.Os.Build})");
        output.Line("Machine", $"{hw.Machine.Manufacturer} {hw.Machine.Model}");
        output.Line("Type", hw.Machine.Type switch
        {
            MachineType.Laptop => "Portable",
            MachineType.Desktop => "PC fixe",
            _ => "Inconnu"
        });

        output.Section("CARTE MÈRE ET FIRMWARE");
        output.Line("Modèle", $"{hw.Board.Manufacturer} {hw.Board.Product}");
        output.Line("BIOS", hw.Board.BiosDate is { } date ? $"{hw.Board.BiosVersion} du {date:dd/MM/yyyy}" : hw.Board.BiosVersion);
        output.Line("Démarrage", hw.Firmware.BootMode switch
        {
            BootMode.Uefi => "UEFI",
            BootMode.Legacy => "Ancien mode (Legacy/CSM)",
            _ => "?"
        });
        output.Line("Secure Boot", State(hw.Firmware.SecureBoot));
        output.Line("TPM", hw.Firmware.TpmReady == true ? $"actif ({hw.Firmware.TpmVersion})" : State(hw.Firmware.TpmReady));

        output.Section("PROCESSEUR");
        output.Line("Modèle", hw.Cpu.Name);
        output.Line("Cœurs", hw.Cpu.IsHybrid
            ? $"{hw.Cpu.PerformanceCores} P-cores + {hw.Cpu.EfficientCores} E-cores / {hw.Cpu.Threads} threads"
            : $"{hw.Cpu.Cores} cœurs / {hw.Cpu.Threads} threads");
        if (hw.Cpu.Microcode is { } microcode) output.Line("Microcode", $"0x{microcode:X}");

        output.Section("CARTES GRAPHIQUES");
        if (hw.Gpus.Count == 0) output.Line("-", "Aucune détectée");
        foreach (var gpu in hw.Gpus)
        {
            output.Line("Modèle", gpu.Name);
            output.Line(gpu.IsIntegrated ? "  Mémoire" : "  VRAM", gpu.IsIntegrated ? "partagée avec la RAM (puce intégrée)" : Format.Gb(gpu.VramBytes));
            output.Line("  Pilote", gpu.VendorDriverVersion is null
                ? gpu.DriverVersion
                : $"{gpu.VendorDriverVersion} ({gpu.DriverVersion})");
        }
        if (hw.NvidiaLink is { } link)
        {
            output.Line("  PCIe", $"Gen{link.PcieGenMax} x{link.PcieWidthMax} (max possible)");
            output.Line("  BAR1", link.Bar1TotalMiB > 0 ? $"{link.Bar1TotalMiB} Mio" : "?");
        }
        if (hw.Nvidia is { } nvidia)
            output.Line("  NVAPI", nvidia.Error is not null ? $"indisponible ({nvidia.Error})"
                : nvidia.DriverSettingsError is not null ? $"réglages du pilote illisibles ({nvidia.DriverSettingsError})"
                : "disponible");

        output.Section("MÉMOIRE");
        output.Line("Total", $"{Format.Gb(hw.Memory.TotalBytes)} {hw.Memory.Generation}");
        output.Line("Barrettes", hw.Memory.Modules.Count.ToString());
        output.Line("Vitesse", hw.Memory.ConfiguredSpeedMTs > 0 ? $"{hw.Memory.ConfiguredSpeedMTs} MT/s (configurée)" : "?");
        foreach (var m in hw.Memory.Modules)
            output.Line("  Module", $"{m.DeviceLocator} · {m.Manufacturer} {m.PartNumber} · {Format.Gb(m.CapacityBytes)}");

        output.Section("STOCKAGE");
        foreach (var d in hw.Disks)
        {
            var letters = d.DriveLetters.Count > 0 ? string.Join(" ", d.DriveLetters.Select(l => $"{l}:")) : "sans lettre";
            output.Line($"Disque {d.Number}", $"{d.Model} · {Media(d)} · {Format.DiskSize(d.SizeBytes)} · {letters}");
            var health = Diagnostics.Checks.SsdHealthCheck.Describe(d);
            if (health.Length > 0) output.Line("  Santé", health);
        }

        output.Section("ÉCRANS");
        foreach (var s in hw.Displays)
        {
            output.Line(s.IsPrimary ? "Principal" : "Secondaire", s.MonitorName);
            output.Line("  Mode", $"{s.Width}x{s.Height} @ {s.CurrentRefreshHz} Hz (max {s.MaxRefreshHz} Hz)");
            if (s.NativeWidth != s.Width || s.NativeHeight != s.Height)
                output.Line("  Native", $"{s.NativeWidth}x{s.NativeHeight}");
            output.Line("  Branché sur", s.AdapterName);
            if (Diagnostics.Checks.DisplayLinkCheck.ConnectionName(s.Connection) is { } connection)
                output.Line("  Connexion", connection);
            if (s.HdrSupported is { } hdr)
                output.Line("  HDR", s.HdrEnabled == true ? "actif" : hdr ? "disponible, inactif" : "non pris en charge");
            if (hw.Nvidia?.Display(s.DeviceName) is { } nv)
            {
                // Détail des indicateurs du pilote : utile pour vérifier la lecture G-SYNC sur matériel réel
                output.Line("  G-SYNC", nv.Vrr is not { } vrr ? "illisible"
                    : $"{(vrr.IsOn ? "actif" : vrr.Possible ? "possible, inactif" : "non disponible")} " +
                      $"(possible {Flag(vrr.Possible)}, demandé {Flag(vrr.Requested)}, activé {Flag(vrr.Enabled)}, en cours {Flag(vrr.InVrrMode)})");
                if (nv.Color is { } color)
                    output.Line("  Couleurs", $"{(color.IsRgb ? "RGB" : "YCbCr")} · plage " +
                        $"{color.Range switch { Nvidia.DynamicRange.Full => "complète", Nvidia.DynamicRange.Limited => "limitée", _ => "automatique" }}" +
                        (color.Policy == Nvidia.ColorPolicy.Driver ? " (choix du pilote)" : " (réglage manuel)"));
            }
        }

        output.Section("PÉRIPHÉRIQUES");
        List(output, "Souris", hw.Peripherals.Mice);
        List(output, "Claviers", hw.Peripherals.Keyboards);
        List(output, "Manettes", hw.Peripherals.GameControllers);
        List(output, "Audio", hw.Peripherals.AudioDevices);
        if (hw.Peripherals.Printers is { } printers) List(output, "Imprimantes", printers);

        output.Section("RÉSEAU");
        if (hw.Network.Count == 0) output.Line("-", "Aucune connexion");
        foreach (var a in hw.Network)
        {
            output.Line(a.Kind == NetworkKind.Ethernet ? "Ethernet" : "Wi-Fi",
                $"{a.Description} · {Diagnostics.Checks.NetworkCheck.Speed(a.SpeedBps)}");
            if (a.WifiStandard is not null || a.WifiSignalPercent is not null)
                output.Line("", string.Join(" · ", new[] { a.WifiStandard, a.WifiSignalPercent is { } p ? $"signal {p} %" : null }.Where(x => x is not null)));
        }

        output.Section("ÉTAT DE WINDOWS");
        output.Line("Protection", hw.SystemState.SystemProtection switch { true => "points de restauration actifs", false => "désactivée", null => "?" });
        output.Line("Redémarrage", hw.SystemState.PendingReboot switch { true => "en attente", false => "aucun en attente", null => "?" });
        if (hw.Battery is { } battery) output.Line("Batterie", $"{battery.HealthPercent} % de la capacité d'origine");

        if (hw.Warnings.Count > 0)
        {
            output.Section("AVERTISSEMENTS DE DÉTECTION");
            foreach (var w in hw.Warnings) output.Warning(w);
        }
    }

    private static string State(bool? value) => value switch { true => "actif", false => "inactif", null => "?" };

    private static string Media(DiskInfo d) => (d.Media, d.Bus) switch
    {
        (_, StorageBus.NVMe) => "SSD NVMe",
        (StorageMedia.Ssd, _) => "SSD SATA",
        (StorageMedia.Hdd, _) => "Disque dur",
        (_, StorageBus.Usb) => "USB",
        _ => "Type inconnu"
    };

    private static string Flag(bool value) => value ? "oui" : "non";

    private static void List(IInventorySink output, string label, IReadOnlyList<string> items)
    {
        if (items.Count == 0) { output.Line(label, "aucun"); return; }
        output.Line(label, items[0]);
        foreach (var item in items.Skip(1)) output.Line("", item);
    }

    /// <summary>Mise en forme texte historique : « == TITRE == » puis « libellé  valeur ».</summary>
    private sealed class TextSink(TextWriter output) : IInventorySink
    {
        public void Section(string title)
        {
            output.WriteLine();
            output.WriteLine($"== {title} ==");
        }

        public void Line(string label, string value) => output.WriteLine($"  {label,-14}{value}");

        public void Warning(string text) => output.WriteLine($"  [!] {text}");
    }
}
