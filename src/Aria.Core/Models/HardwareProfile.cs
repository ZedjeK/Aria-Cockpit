namespace Aria.Core.Models;

public enum MachineType { Unknown, Desktop, Laptop }
public enum GpuVendor { Other, Nvidia, Amd, Intel }
public enum StorageMedia { Unknown, Ssd, Hdd }
public enum StorageBus { Other, NVMe, Sata, Usb, Raid }
public enum BootMode { Unknown, Legacy, Uefi }
public enum DisplayConnection { Unknown, DisplayPort, Hdmi, Dvi, Vga, Internal, Wireless, Other }
public enum NetworkKind { Ethernet, WiFi, Other }

public sealed record OsInfo(string Name, string DisplayVersion, string Version, int Build)
{
    public static readonly OsInfo Unknown = new("Inconnu", "", "", 0);
}

public sealed record MachineInfo(string Manufacturer, string Model, MachineType Type, bool HasBattery)
{
    public static readonly MachineInfo Unknown = new("Inconnu", "Inconnu", MachineType.Unknown, false);
}

/// <param name="PerformanceCores">Cœurs performants (P-cores). 0 si le processeur n'est pas hybride.</param>
/// <param name="EfficientCores">Cœurs économes (E-cores). 0 si le processeur n'est pas hybride.</param>
/// <param name="Microcode">Révision du microcode chargé (ex. 0x12F), si lisible.</param>
/// <param name="Model">Modèle CPUID affiché par Windows (« Family 6 Model 183 ») : distingue les puces d'un même nom commercial.</param>
public sealed record CpuInfo(
    string Name,
    string Manufacturer,
    int Cores,
    int Threads,
    int MaxClockMHz,
    int PerformanceCores = 0,
    int EfficientCores = 0,
    uint? Microcode = null,
    int? Model = null)
{
    public bool IsHybrid => PerformanceCores > 0 && EfficientCores > 0;

    public static readonly CpuInfo Unknown = new("Inconnu", "", 0, 0, 0);

    public bool IsAmd => Manufacturer.Contains("AMD", StringComparison.OrdinalIgnoreCase);
    public bool IsIntel => Manufacturer.Contains("Intel", StringComparison.OrdinalIgnoreCase);
}

public sealed record BoardInfo(string Manufacturer, string Product, string BiosVersion, DateTime? BiosDate)
{
    public static readonly BoardInfo Unknown = new("Inconnu", "Inconnu", "", null);
}

/// <summary>État du firmware vu depuis Windows. null = information indisponible.</summary>
public sealed record FirmwareInfo(
    BootMode BootMode,
    bool? SecureBoot,
    bool? TpmReady,
    string? TpmVersion,
    bool? Virtualization,
    bool? MemoryIntegrity)
{
    public static readonly FirmwareInfo Unknown = new(BootMode.Unknown, null, null, null, null, null);
}

/// <summary>Lien PCIe et Resizable BAR de la carte NVIDIA (via nvidia-smi). 0 = inconnu.</summary>
/// <param name="PcieGenMax">Génération max possible avec cette carte ET cette carte mère.</param>
/// <param name="PcieGenGpuMax">Génération max supportée par la carte graphique seule.</param>
/// <param name="PcieWidthMax">Nombre de lignes max possible dans ce port.</param>
/// <param name="Bar1TotalMiB">Taille de la fenêtre BAR1 : 256 Mio = Resizable BAR inactif.</param>
public sealed record NvidiaLinkInfo(
    int PcieGenCurrent,
    int PcieGenMax,
    int PcieGenGpuMax,
    int PcieWidthCurrent,
    int PcieWidthMax,
    int Bar1TotalMiB);

/// <param name="DriverVersion">Version Windows du pilote (ex. 32.0.15.6094).</param>
/// <param name="VendorDriverVersion">Version "commerciale" du constructeur (ex. 560.94 chez NVIDIA), si calculable.</param>
public sealed record GpuInfo(
    string Name,
    GpuVendor Vendor,
    ulong VramBytes,
    string DriverVersion,
    string? VendorDriverVersion,
    DateTime? DriverDate,
    string PnpDeviceId)
{
    /// <summary>
    /// Puce graphique intégrée au processeur (mémoire partagée avec la RAM).
    /// Intel : tout sauf les Arc dédiées (A770, B580…). AMD : tout sauf les Radeon RX / Pro W.
    /// </summary>
    public bool IsIntegrated => Vendor switch
    {
        GpuVendor.Intel => !System.Text.RegularExpressions.Regex.IsMatch(Name, @"Arc\(TM\)\s+[AB]\d{3}|Arc\s+[AB]\d{3}"),
        GpuVendor.Amd => !System.Text.RegularExpressions.Regex.IsMatch(Name, @"\bRX\b|Pro W|Radeon VII|Vega (56|64)"),
        _ => false
    };
}

/// <param name="RatedSpeedMTs">Vitesse annoncée par le module (souvent la vitesse JEDEC, pas le profil XMP/EXPO).</param>
/// <param name="ConfiguredSpeedMTs">Vitesse réellement configurée par le BIOS.</param>
/// <param name="SmbiosType">Type SMBIOS : 24 = DDR3, 26 = DDR4, 34 = DDR5.</param>
/// <param name="DeviceLocator">Emplacement sur la carte mère (ex. DIMM_A2, Controller0-ChannelA-DIMM1).</param>
public sealed record MemoryModule(
    string Manufacturer,
    string PartNumber,
    ulong CapacityBytes,
    int RatedSpeedMTs,
    int ConfiguredSpeedMTs,
    int SmbiosType,
    string DeviceLocator,
    string BankLabel);

public sealed record MemoryInfo(IReadOnlyList<MemoryModule> Modules)
{
    public static readonly MemoryInfo Unknown = new(Array.Empty<MemoryModule>());

    public ulong TotalBytes => (ulong)Modules.Sum(m => (decimal)m.CapacityBytes);

    /// <summary>Débit configuré en MT/s, corrigé quand le BIOS le rapporte en MHz (même valeur partout : Mon PC, diagnostic, fiche).</summary>
    public int ConfiguredSpeedMTs => Modules.Count == 0 ? 0 : Diagnostics.MemoryParser.NormalizeSpeed(Modules.Min(m => m.ConfiguredSpeedMTs), Generation);

    public string Generation => Modules.FirstOrDefault()?.SmbiosType switch
    {
        24 => "DDR3",
        26 => "DDR4",
        34 => "DDR5",
        _ => "Inconnue"
    };
}

public sealed record DiskInfo(
    int Number,
    string Model,
    StorageMedia Media,
    StorageBus Bus,
    ulong SizeBytes,
    IReadOnlyList<char> DriveLetters,
    int? WearPercent = null,
    int? TemperatureC = null,
    long? PowerOnHours = null,
    int? TemperatureMaxC = null);

/// <param name="DeviceName">Nom système de la sortie (ex. \\.\DISPLAY1).</param>
/// <param name="AdapterName">Carte graphique qui pilote cet écran.</param>
/// <param name="MaxRefreshHz">Fréquence maximale disponible à la résolution actuelle.</param>
public sealed record DisplayInfo(
    string DeviceName,
    string MonitorName,
    string AdapterName,
    bool IsPrimary,
    int Width,
    int Height,
    int CurrentRefreshHz,
    int MaxRefreshHz,
    int NativeWidth,
    int NativeHeight,
    DisplayConnection Connection = DisplayConnection.Unknown,
    bool? HdrSupported = null,
    bool? HdrEnabled = null);

/// <param name="SpeedBps">Vitesse de liaison négociée, en bits par seconde.</param>
/// <param name="WifiStandard">Norme Wi-Fi (ex. « Wi-Fi 6 (802.11ax) »), si connue.</param>
/// <param name="WifiSignalPercent">Qualité du signal Wi-Fi, si connue.</param>
public sealed record NetworkAdapterInfo(
    string Name,
    string Description,
    NetworkKind Kind,
    long SpeedBps,
    string? WifiStandard = null,
    int? WifiSignalPercent = null);

/// <param name="PendingReboot">Un redémarrage est attendu pour terminer des mises à jour.</param>
/// <param name="SystemProtection">Protection du système (points de restauration) active sur le disque de Windows.</param>
public sealed record SystemStateInfo(bool? PendingReboot, bool? SystemProtection)
{
    public static readonly SystemStateInfo Unknown = new(null, null);
}

/// <param name="DesignCapacity">Capacité d'origine (mWh).</param>
/// <param name="FullChargeCapacity">Capacité actuelle à pleine charge (mWh).</param>
public sealed record BatteryInfo(long DesignCapacity, long FullChargeCapacity)
{
    /// <summary>Capacité restante par rapport à l'origine, en %.</summary>
    public int HealthPercent => DesignCapacity <= 0 ? 0 : (int)Math.Round(100.0 * FullChargeCapacity / DesignCapacity);
}

/// <param name="Printers">Imprimantes réelles (locales ou réseau), sans les imprimantes virtuelles (PDF, XPS, OneNote, Fax). null = illisible.</param>
public sealed record PeripheralInfo(
    IReadOnlyList<string> Mice,
    IReadOnlyList<string> Keyboards,
    IReadOnlyList<string> GameControllers,
    IReadOnlyList<string> AudioDevices,
    IReadOnlyList<string>? Printers = null)
{
    public static readonly PeripheralInfo Unknown = new([], [], [], []);
}

/// <summary>
/// Photographie complète du matériel. Remplie par HardwareInventory ;
/// chaque partie garde une valeur "Inconnu" si sa détection échoue.
/// </summary>
public sealed class HardwareProfile
{
    public OsInfo Os { get; set; } = OsInfo.Unknown;
    public MachineInfo Machine { get; set; } = MachineInfo.Unknown;
    public BoardInfo Board { get; set; } = BoardInfo.Unknown;
    public FirmwareInfo Firmware { get; set; } = FirmwareInfo.Unknown;
    public CpuInfo Cpu { get; set; } = CpuInfo.Unknown;
    public IReadOnlyList<GpuInfo> Gpus { get; set; } = [];
    public NvidiaLinkInfo? NvidiaLink { get; set; }
    /// <summary>État lu par NVAPI (G-SYNC, couleurs, accès aux réglages du pilote). null sans carte NVIDIA.</summary>
    public NvidiaDriverInfo? Nvidia { get; set; }
    public MemoryInfo Memory { get; set; } = MemoryInfo.Unknown;
    public IReadOnlyList<DiskInfo> Disks { get; set; } = [];
    public IReadOnlyList<DisplayInfo> Displays { get; set; } = [];
    public PeripheralInfo Peripherals { get; set; } = PeripheralInfo.Unknown;
    public IReadOnlyList<NetworkAdapterInfo> Network { get; set; } = [];
    public SystemStateInfo SystemState { get; set; } = SystemStateInfo.Unknown;
    public BatteryInfo? Battery { get; set; }

    /// <summary>Problèmes rencontrés pendant la détection (non bloquants).</summary>
    public List<string> Warnings { get; } = [];

    public bool IsLaptop => Machine.Type == MachineType.Laptop;

    public GpuInfo? NvidiaGpu => Gpus.FirstOrDefault(g => g.Vendor == GpuVendor.Nvidia);

    public bool HasNvidiaGpu => NvidiaGpu is not null;
}
