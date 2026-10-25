using System.Runtime.InteropServices;

namespace Aria.Bench.Native;

/// <summary>Alimentation (secteur ou batterie, économiseur d'énergie) et fréquence de l'écran principal.</summary>
internal static unsafe class SystemInfo
{
    public static SystemContext Read()
    {
        string power = "inconnue";
        var saver = false;
        if (GetSystemPowerStatus(out var status) != 0)
        {
            power = status.ACLineStatus switch { 0 => "batterie", 1 => "secteur", _ => "inconnue" };
            saver = status.SystemStatusFlag == 1;
        }
        return new SystemContext(power, saver, RefreshRate());
    }

    /// <summary>Fréquence actuelle de l'écran principal en Hz (0 si illisible).</summary>
    private static int RefreshRate()
    {
        // DEVMODEW : 220 octets ; dmSize à l'octet 68, dmDisplayFrequency à l'octet 184
        const int size = 220;
        var devmode = stackalloc byte[size];
        new Span<byte>(devmode, size).Clear();
        *(ushort*)(devmode + 68) = size;
        if (EnumDisplaySettingsW(null, ENUM_CURRENT_SETTINGS, devmode) == 0) return 0;
        var hz = *(uint*)(devmode + 184);
        return hz is > 1 and < 1000 ? (int)hz : 0;   // 0 et 1 veulent dire « valeur matérielle par défaut »
    }

    private const int ENUM_CURRENT_SETTINGS = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern int GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int EnumDisplaySettingsW(char* deviceName, int mode, byte* devmode);
}
