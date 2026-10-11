using Microsoft.Win32;
using Aria.Core.Models;

namespace Aria.Core.Inventory;

internal static class OsCollector
{
    public static OsInfo Collect()
    {
        var os = Wmi.Query("SELECT Caption, Version, BuildNumber FROM Win32_OperatingSystem").FirstOrDefault();
        if (os is null) return OsInfo.Unknown;

        // "24H2", "25H2"… n'est disponible que dans le registre
        var displayVersion = Registry.GetValue(
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "DisplayVersion", "") as string ?? "";

        _ = int.TryParse(Wmi.Str(os, "BuildNumber"), out var build);

        return new OsInfo(
            Name: Wmi.Str(os, "Caption").Replace("Microsoft ", ""),
            DisplayVersion: displayVersion,
            Version: Wmi.Str(os, "Version"),
            Build: build);
    }
}
