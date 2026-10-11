using Microsoft.Win32;
using Aria.Core.Models;

namespace Aria.Core.Inventory;

internal static class GpuCollector
{
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public static IReadOnlyList<GpuInfo> Collect()
    {
        var result = new List<GpuInfo>();

        foreach (var gpu in Wmi.Query("SELECT Name, PNPDeviceID, DriverVersion, DriverDate, AdapterRAM FROM Win32_VideoController"))
        {
            var pnpId = Wmi.Str(gpu, "PNPDeviceID");

            // On ignore les adaptateurs virtuels (bureau à distance, écrans virtuels…) : seules les vraies cartes sont en PCI.
            if (!pnpId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)) continue;

            var vendor = VendorFromPnpId(pnpId);
            var driver = Wmi.Str(gpu, "DriverVersion");

            // AdapterRAM est plafonné à 4 Go (entier 32 bits) : on lit la vraie valeur dans le registre.
            var vram = ReadVramFromRegistry(pnpId);
            if (vram == 0) vram = Wmi.ULong(gpu, "AdapterRAM");

            result.Add(new GpuInfo(
                Name: Wmi.Str(gpu, "Name"),
                Vendor: vendor,
                VramBytes: vram,
                DriverVersion: driver,
                VendorDriverVersion: vendor == GpuVendor.Nvidia ? ToNvidiaVersion(driver) : null,
                DriverDate: Wmi.Date(gpu, "DriverDate"),
                PnpDeviceId: pnpId));
        }

        return result;
    }

    private static GpuVendor VendorFromPnpId(string pnpId)
    {
        var id = pnpId.ToUpperInvariant();
        if (id.Contains("VEN_10DE")) return GpuVendor.Nvidia;
        if (id.Contains("VEN_1002")) return GpuVendor.Amd;
        if (id.Contains("VEN_8086")) return GpuVendor.Intel;
        return GpuVendor.Other;
    }

    /// <summary>
    /// Convertit la version Windows du pilote NVIDIA en version commerciale.
    /// Exemple : 32.0.15.6094 → 560.94 (dernier chiffre du 3e bloc + 4e bloc).
    /// </summary>
    internal static string? ToNvidiaVersion(string windowsVersion)
    {
        var parts = windowsVersion.Split('.');
        if (parts.Length != 4 || parts[2].Length == 0 || parts[3].Length != 4) return null;

        var digits = parts[2][^1] + parts[3];          // ex. "5" + "6094" = "56094"
        return $"{digits[..3]}.{digits[3..]}";          // "560.94"
    }

    private static ulong ReadVramFromRegistry(string pnpId)
    {
        try
        {
            using var classKey = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (classKey is null) return 0;

            foreach (var name in classKey.GetSubKeyNames().Where(n => n.All(char.IsDigit)))
            {
                using var key = classKey.OpenSubKey(name);
                if (key?.GetValue("MatchingDeviceId") is not string matchingId) continue;
                if (!pnpId.StartsWith(matchingId, StringComparison.OrdinalIgnoreCase)) continue;

                var size = ToULong(key.GetValue("HardwareInformation.qwMemorySize"));
                if (size == 0) size = ToULong(key.GetValue("HardwareInformation.MemorySize"));
                if (size > 0) return size;
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Lecture refusée : on retombera sur la valeur WMI.
        }

        return 0;
    }

    private static ulong ToULong(object? value) => value switch
    {
        long l => (ulong)l,
        int i => (uint)i,
        byte[] { Length: >= 8 } b => BitConverter.ToUInt64(b, 0),
        byte[] { Length: >= 4 } b => BitConverter.ToUInt32(b, 0),
        _ => 0
    };
}
