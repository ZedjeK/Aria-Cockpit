using Aria.Core.Models;
using static Aria.Core.Inventory.NativeDisplay;

namespace Aria.Core.Inventory;

internal static class DisplayCollector
{
    public static IReadOnlyList<DisplayInfo> Collect(List<string> warnings)
    {
        var friendlyNames = ReadMonitorFriendlyNames(warnings);
        var links = ReadLinks(warnings);
        var result = new List<DisplayInfo>();

        var adapter = DISPLAY_DEVICE.Create();
        for (uint i = 0; EnumDisplayDevices(null, i, ref adapter, 0); i++, adapter = DISPLAY_DEVICE.Create())
        {
            if (!adapter.StateFlags.HasFlag(DisplayDeviceFlags.AttachedToDesktop)) continue;
            if (adapter.StateFlags.HasFlag(DisplayDeviceFlags.MirroringDriver)) continue;

            var current = DEVMODE.Create();
            if (!EnumDisplaySettings(adapter.DeviceName, EnumCurrentSettings, ref current)) continue;

            // Parcours de tous les modes : fréquence max à la résolution actuelle + résolution native
            int maxRefresh = current.dmDisplayFrequency, nativeW = current.dmPelsWidth, nativeH = current.dmPelsHeight;
            var mode = DEVMODE.Create();
            for (int m = 0; EnumDisplaySettings(adapter.DeviceName, m, ref mode); m++)
            {
                if (mode.dmPelsWidth == current.dmPelsWidth && mode.dmPelsHeight == current.dmPelsHeight)
                    maxRefresh = Math.Max(maxRefresh, mode.dmDisplayFrequency);

                if ((long)mode.dmPelsWidth * mode.dmPelsHeight > (long)nativeW * nativeH)
                    (nativeW, nativeH) = (mode.dmPelsWidth, mode.dmPelsHeight);
            }

            result.Add(new DisplayInfo(
                DeviceName: adapter.DeviceName,
                MonitorName: ResolveMonitorName(adapter.DeviceName, friendlyNames),
                AdapterName: adapter.DeviceString,
                IsPrimary: adapter.StateFlags.HasFlag(DisplayDeviceFlags.Primary),
                Width: current.dmPelsWidth,
                Height: current.dmPelsHeight,
                CurrentRefreshHz: current.dmDisplayFrequency,
                MaxRefreshHz: maxRefresh,
                NativeWidth: nativeW,
                NativeHeight: nativeH,
                Connection: links.TryGetValue(adapter.DeviceName, out var link) ? link.Connection : DisplayConnection.Unknown,
                HdrSupported: link.HdrSupported,
                HdrEnabled: link.HdrEnabled));
        }

        return result;
    }

    private static Dictionary<string, (DisplayConnection Connection, bool? HdrSupported, bool? HdrEnabled)> ReadLinks(List<string> warnings)
    {
        try
        {
            return DisplayConfig.Read();
        }
        catch (Exception e)
        {
            warnings.Add($"Connexion et HDR des écrans illisibles : {e.Message}");
            return [];
        }
    }

    /// <summary>Nom de l'écran : nom commercial (EDID) si trouvé, sinon nom générique de Windows.</summary>
    private static string ResolveMonitorName(string adapterDeviceName, Dictionary<string, string> friendlyNames)
    {
        var monitor = DISPLAY_DEVICE.Create();
        if (!EnumDisplayDevices(adapterDeviceName, 0, ref monitor, 0)) return "Écran inconnu";

        // DeviceID du type MONITOR\GSM5B7F\{...}\0003 → identifiant matériel "GSM5B7F"
        var parts = monitor.DeviceID.Split('\\');
        if (parts.Length > 1 && friendlyNames.TryGetValue(parts[1], out var name)) return name;

        return string.IsNullOrWhiteSpace(monitor.DeviceString) ? "Écran inconnu" : monitor.DeviceString;
    }

    /// <summary>Lit les noms commerciaux des écrans (ex. "LG ULTRAGEAR") depuis leurs données EDID.</summary>
    private static Dictionary<string, string> ReadMonitorFriendlyNames(List<string> warnings)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var mon in Wmi.Query("SELECT InstanceName, UserFriendlyName FROM WmiMonitorID", Wmi.RootWmi))
            {
                // InstanceName du type DISPLAY\GSM5B7F\5&...&UID4353_0
                var parts = Wmi.Str(mon, "InstanceName").Split('\\');
                if (parts.Length < 2 || Wmi.Raw(mon, "UserFriendlyName") is not ushort[] chars) continue;

                var name = new string(chars.TakeWhile(c => c != 0).Select(c => (char)c).ToArray()).Trim();
                if (name.Length > 0) names.TryAdd(parts[1], name);
            }
        }
        catch (Exception e)
        {
            warnings.Add($"Noms commerciaux des écrans indisponibles : {e.Message}");
        }
        return names;
    }
}
