using System.Runtime.InteropServices;
using Aria.Core.Models;

namespace Aria.Core.Inventory;

/// <summary>
/// API « DisplayConfig » de Windows : type de connexion (HDMI, DisplayPort…) et état HDR de chaque écran actif.
/// </summary>
internal static class DisplayConfig
{
    private const uint QdcOnlyActivePaths = 0x2;
    private const int GetSourceName = 1;
    private const int GetTargetName = 2;
    private const int GetAdvancedColorInfo = 9;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public int OutputTechnology;
        public int Rotation;
        public int Scaling;
        public uint RefreshNumerator;
        public uint RefreshDenominator;
        public int ScanLineOrdering;
        public int TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PathInfo
    {
        public PathSourceInfo SourceInfo;
        public PathTargetInfo TargetInfo;
        public uint Flags;
    }

    /// <summary>Contenu non utilisé ici : seule la taille (64 octets) compte.</summary>
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    internal struct ModeInfo
    {
        public int InfoType;
        public uint Id;
        public Luid AdapterId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DeviceInfoHeader
    {
        public int Type;
        public int Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public int OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdvancedColorInfo
    {
        public DeviceInfoHeader Header;
        public uint Value;
        public int ColorEncoding;
        public uint BitsPerColorChannel;
    }

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] PathInfo[] paths,
        ref uint modeCount, [Out] ModeInfo[] modes, IntPtr topologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref AdvancedColorInfo request);

    /// <summary>Par nom système (\\.\DISPLAY1) : connexion, HDR pris en charge, HDR activé.</summary>
    public static Dictionary<string, (DisplayConnection Connection, bool? HdrSupported, bool? HdrEnabled)> Read()
    {
        var result = new Dictionary<string, (DisplayConnection, bool?, bool?)>(StringComparer.OrdinalIgnoreCase);
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0) return result;

        var paths = new PathInfo[pathCount];
        var modes = new ModeInfo[modeCount];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return result;

        foreach (var path in paths.Take((int)pathCount))
        {
            var source = new SourceDeviceName
            {
                Header = Header<SourceDeviceName>(GetSourceName, path.SourceInfo.AdapterId, path.SourceInfo.Id)
            };
            if (DisplayConfigGetDeviceInfo(ref source) != 0 || string.IsNullOrEmpty(source.ViewGdiDeviceName)) continue;

            var target = new TargetDeviceName
            {
                Header = Header<TargetDeviceName>(GetTargetName, path.TargetInfo.AdapterId, path.TargetInfo.Id)
            };
            var connection = DisplayConfigGetDeviceInfo(ref target) == 0
                ? Classify(target.OutputTechnology)
                : Classify(path.TargetInfo.OutputTechnology);

            var color = new AdvancedColorInfo
            {
                Header = Header<AdvancedColorInfo>(GetAdvancedColorInfo, path.TargetInfo.AdapterId, path.TargetInfo.Id)
            };
            bool? supported = null, enabled = null;
            if (DisplayConfigGetDeviceInfo(ref color) == 0)
            {
                supported = (color.Value & 0x1) != 0; // advancedColorSupported
                enabled = (color.Value & 0x2) != 0;   // advancedColorEnabled
            }

            result.TryAdd(source.ViewGdiDeviceName, (connection, supported, enabled));
        }
        return result;
    }

    private static DeviceInfoHeader Header<T>(int type, Luid adapter, uint id) => new()
    {
        Type = type,
        Size = Marshal.SizeOf<T>(),
        AdapterId = adapter,
        Id = id
    };

    /// <summary>Valeurs DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.</summary>
    internal static DisplayConnection Classify(int technology) => technology switch
    {
        0 => DisplayConnection.Vga,                        // HD15
        4 => DisplayConnection.Dvi,
        5 => DisplayConnection.Hdmi,
        6 => DisplayConnection.Internal,                   // LVDS
        10 or 11 or 18 => DisplayConnection.DisplayPort,   // externe, intégré, USB-C (tunnel)
        15 => DisplayConnection.Wireless,                  // Miracast
        unchecked((int)0x80000000) => DisplayConnection.Internal,
        -1 => DisplayConnection.Other,
        _ => DisplayConnection.Other
    };
}
