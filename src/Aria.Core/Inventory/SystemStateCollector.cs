using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Aria.Core.Models;

namespace Aria.Core.Inventory;

/// <summary>Redémarrage en attente et protection du système (points de restauration).</summary>
public static class SystemStateCollector
{
    internal static SystemStateInfo Collect() => new(ReadPendingReboot(), IsSystemProtectionEnabled());

    /// <summary>Clés que Windows crée tant qu'un redémarrage est nécessaire pour finir une mise à jour.</summary>
    private static bool? ReadPendingReboot()
    {
        try
        {
            using var wu = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var cbs = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            return wu is not null || cbs is not null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- Protection du système ----

    private const string SppClientsKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SPP\Clients";
    private const string SystemRestoreClient = "{09F7EDC5-294E-4180-AF6A-FB0E6A0E9513}";

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(string mountPoint, StringBuilder volumeName, int length);

    /// <summary>
    /// Vrai si la protection du système est active sur le disque de Windows. Windows liste les volumes protégés
    /// dans la clé SPP\Clients, sous la forme « \\?\Volume{GUID}\:(C%3A) ».
    /// </summary>
    public static bool? IsSystemProtectionEnabled()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";
            var name = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPoint(root, name, name.Capacity)) return null;

            using var key = Registry.LocalMachine.OpenSubKey(SppClientsKey);
            var entries = key?.GetValue(SystemRestoreClient) as string[] ?? [];
            return IsVolumeListed(entries, name.ToString());
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool IsVolumeListed(IEnumerable<string> entries, string volumeName) =>
        entries.Any(e => e.StartsWith(volumeName, StringComparison.OrdinalIgnoreCase));
}
