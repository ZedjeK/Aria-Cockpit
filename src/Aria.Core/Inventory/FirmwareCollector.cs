using System.Runtime.InteropServices;
using Microsoft.Win32;
using Aria.Core.Models;

namespace Aria.Core.Inventory;

/// <summary>Traces visibles depuis Windows des principaux réglages du firmware (BIOS/UEFI).</summary>
internal static class FirmwareCollector
{
    public static FirmwareInfo Collect(List<string> warnings)
    {
        var (tpmReady, tpmVersion) = ReadTpm(warnings);

        return new FirmwareInfo(
            BootMode: ReadBootMode(),
            SecureBoot: ReadSecureBoot(),
            TpmReady: tpmReady,
            TpmVersion: tpmVersion,
            Virtualization: ReadVirtualization(),
            MemoryIntegrity: ReadMemoryIntegrity());
    }

    // ---- Mode de démarrage (UEFI ou ancien mode Legacy/CSM) ----

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out int firmwareType);

    private static BootMode ReadBootMode()
    {
        if (!GetFirmwareType(out var type)) return BootMode.Unknown;
        return type switch { 1 => BootMode.Legacy, 2 => BootMode.Uefi, _ => BootMode.Unknown };
    }

    // ---- Secure Boot ----

    private static bool? ReadSecureBoot()
    {
        var value = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null);
        return value is int i ? i == 1 : null;
    }

    // ---- TPM (nécessite les droits administrateur) ----

    private static (bool? Ready, string? Version) ReadTpm(List<string> warnings)
    {
        try
        {
            var tpm = Wmi.Query(
                "SELECT IsEnabled_InitialValue, IsActivated_InitialValue, SpecVersion FROM Win32_Tpm",
                @"root\cimv2\Security\MicrosoftTpm").FirstOrDefault();

            if (tpm is null) return (false, null); // aucun TPM visible : absent ou désactivé dans le BIOS

            var ready = Wmi.Raw(tpm, "IsEnabled_InitialValue") is true && Wmi.Raw(tpm, "IsActivated_InitialValue") is true;
            var version = Wmi.Str(tpm, "SpecVersion").Split(',')[0].Trim(); // "2.0, 0, 1.59" → "2.0"
            return (ready, version.Length > 0 ? version : null);
        }
        catch (Exception e)
        {
            warnings.Add($"État du TPM illisible : {e.Message}");
            return (null, null);
        }
    }

    // ---- Virtualisation (VT-x / SVM) ----

    private static bool? ReadVirtualization()
    {
        // Si un hyperviseur tourne déjà (Hyper-V, WSL2, VBS…), Windows indique "désactivé" à tort :
        // dans ce cas la virtualisation est forcément active.
        var system = Wmi.Query("SELECT HypervisorPresent FROM Win32_ComputerSystem").FirstOrDefault();
        if (system is not null && Wmi.Raw(system, "HypervisorPresent") is true) return true;

        var cpu = Wmi.Query("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor").FirstOrDefault();
        return cpu is null ? null : Wmi.Raw(cpu, "VirtualizationFirmwareEnabled") as bool?;
    }

    // ---- Intégrité de la mémoire (HVCI) : lue pour information, jamais modifiée ----

    private static bool? ReadMemoryIntegrity()
    {
        try
        {
            var guard = Wmi.Query("SELECT SecurityServicesRunning FROM Win32_DeviceGuard",
                @"root\Microsoft\Windows\DeviceGuard").FirstOrDefault();
            if (guard is not null && Wmi.Raw(guard, "SecurityServicesRunning") is uint[] running)
                return running.Contains(2u); // 2 = intégrité de la mémoire en cours d'exécution
        }
        catch (Exception)
        {
            // Repli sur le registre ci-dessous
        }

        var value = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity",
            "Enabled", null);
        return value is int i ? i == 1 : null;
    }
}
