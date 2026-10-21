using System.Runtime.InteropServices;

namespace Aria.Core.Nvidia;

/// <summary>
/// Une lecture instantanée de la carte NVIDIA. Chaque mesure vaut null quand la carte ne la fournit pas
/// (ventilateur sur un portable, consommation sur certains modèles…).
/// </summary>
public sealed record GpuSample(
    uint? TemperatureC,
    uint? LoadPercent,
    ulong? VramUsedBytes,
    ulong? VramTotalBytes,
    uint? GraphicsClockMHz,
    uint? MaxGraphicsClockMHz,
    double? PowerWatts,
    double? PowerLimitWatts,
    uint? FanPercent,
    ulong? LimitReasons = null);

/// <summary>Raisons pour lesquelles la carte ne monte pas plus haut en fréquence (masque NVML « clocks event reasons »).</summary>
[Flags]
public enum GpuLimitReasons : ulong
{
    None = 0,
    Idle = 0x1,
    ApplicationClocks = 0x2,
    PowerCap = 0x4,
    HardwareSlowdown = 0x8,
    SyncBoost = 0x10,
    SoftwareThermal = 0x20,
    HardwareThermal = 0x40,
    PowerBrake = 0x80,
    DisplayClock = 0x100,

    Thermal = SoftwareThermal | HardwareThermal,
    /// <summary>Ralentissements d'urgence : fréquence divisée par 2 ou plus.</summary>
    Emergency = HardwareSlowdown | PowerBrake
}

/// <summary>Une mesure prête à afficher. <see cref="Ratio"/> (0 à 1) remplit la barre ; <see cref="Hot"/> la passe en alerte.</summary>
public sealed record GpuReadout(string Key, string Label, string Value, string Unit, string Detail, double Ratio, bool Hot);

/// <summary>
/// Télémétrie en direct via NVML (nvml.dll, installé avec le pilote NVIDIA, documenté par NVIDIA :
/// docs.nvidia.com/deploy/nvml-api). Lecture seule : aucun réglage de la carte n'est modifié.
/// </summary>
public sealed class GpuTelemetry
{
    private const int Success = 0;
    private const int TemperatureGpu = 0;   // NVML_TEMPERATURE_GPU
    private const int ClockGraphics = 0;    // NVML_CLOCK_GRAPHICS

    /// <summary>Seuil d'alerte de la température : au-delà, la plupart des cartes commencent à baisser leur fréquence.</summary>
    public const uint HotTemperatureC = 83;

    [StructLayout(LayoutKind.Sequential)] private struct Utilization { public uint Gpu; public uint Memory; }
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total; public ulong Free; public ulong Used; }

    // ---- Signatures (nvml.h) ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CountFn(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HandleByIndexFn(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UIntFn(IntPtr device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TypedUIntFn(IntPtr device, int type, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UtilizationFn(IntPtr device, out Utilization value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MemoryFn(IntPtr device, out Memory value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ULongFn(IntPtr device, out ulong value);

    private static readonly Lazy<Task<GpuTelemetry?>> Shared = new(() => Task.Run(TryOpen));

    private readonly IntPtr _device;
    private readonly TypedUIntFn _temperature, _clock, _maxClock;
    private readonly UtilizationFn _utilization;
    private readonly MemoryFn _memory;
    private readonly UIntFn _power, _powerLimit, _fan;
    private readonly ULongFn? _reasons;
    private readonly object _gate = new();

    private GpuTelemetry(IntPtr library, IntPtr device)
    {
        _device = device;
        _temperature = Export<TypedUIntFn>(library, "nvmlDeviceGetTemperature");
        _clock = Export<TypedUIntFn>(library, "nvmlDeviceGetClockInfo");
        _maxClock = Export<TypedUIntFn>(library, "nvmlDeviceGetMaxClockInfo");
        _utilization = Export<UtilizationFn>(library, "nvmlDeviceGetUtilizationRates");
        _memory = Export<MemoryFn>(library, "nvmlDeviceGetMemoryInfo");
        _power = Export<UIntFn>(library, "nvmlDeviceGetPowerUsage");
        _powerLimit = Export<UIntFn>(library, "nvmlDeviceGetEnforcedPowerLimit");
        _fan = Export<UIntFn>(library, "nvmlDeviceGetFanSpeed");
        // Nom actuel (pilotes R535+), sinon l'ancien nom de la même fonction
        _reasons = OptionalExport<ULongFn>(library, "nvmlDeviceGetCurrentClocksEventReasons")
                   ?? OptionalExport<ULongFn>(library, "nvmlDeviceGetCurrentClocksThrottleReasons");
    }

    /// <summary>Instance unique pour toute l'application (bandeau et analyse), ouverte une seule fois.</summary>
    public static Task<GpuTelemetry?> OpenSharedAsync() => Shared.Value;

    /// <summary>Ouvre la première carte NVIDIA ; null s'il n'y en a pas ou si NVML est indisponible.</summary>
    public static GpuTelemetry? TryOpen()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return null;
        try
        {
            var library = LoadLibrary();
            if (library == IntPtr.Zero) return null;
            if (Export<InitFn>(library, "nvmlInit_v2")() != Success) return null;
            if (Export<CountFn>(library, "nvmlDeviceGetCount_v2")(out var count) != Success || count == 0) return null;
            if (Export<HandleByIndexFn>(library, "nvmlDeviceGetHandleByIndex_v2")(0, out var device) != Success) return null;
            return new GpuTelemetry(library, device);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>Lit toutes les mesures (quelques millisecondes). Une mesure non fournie reste à null.</summary>
    public GpuSample Read()
    {
        lock (_gate)
        {
            uint? temperature = _temperature(_device, TemperatureGpu, out var t) == Success ? t : null;
            uint? load = _utilization(_device, out var u) == Success ? u.Gpu : null;
            var memoryOk = _memory(_device, out var m) == Success;
            uint? clock = _clock(_device, ClockGraphics, out var c) == Success ? c : null;
            uint? maxClock = _maxClock(_device, ClockGraphics, out var mc) == Success && mc > 0 ? mc : null;
            double? power = _power(_device, out var p) == Success ? p / 1000.0 : null;
            double? limit = _powerLimit(_device, out var l) == Success && l > 0 ? l / 1000.0 : null;
            uint? fan = _fan(_device, out var f) == Success ? f : null;
            ulong? reasons = _reasons is not null && _reasons(_device, out var r) == Success ? r : null;
            return new GpuSample(temperature, load, memoryOk ? m.Used : null, memoryOk ? m.Total : null,
                clock, maxClock, power, limit, fan, reasons);
        }
    }

    /// <summary>Les mesures à afficher, dans l'ordre du bandeau. Les mesures absentes sont omises.</summary>
    public static IReadOnlyList<GpuReadout> Describe(GpuSample s)
    {
        var list = new List<GpuReadout>();
        if (s.TemperatureC is { } temp)
            list.Add(new("temp", "TEMPÉRATURE", $"{temp}", "°C",
                temp >= HotTemperatureC ? "Chaude : baisse de fréquence possible" : "Cœur du GPU",
                Math.Clamp(temp / 100.0, 0, 1), temp >= HotTemperatureC));
        if (s.LoadPercent is { } load)
            list.Add(new("load", "CHARGE GPU", $"{load}", "%", "Utilisation du processeur graphique", Math.Clamp(load / 100.0, 0, 1), false));
        if (s.VramUsedBytes is { } used && s.VramTotalBytes is { } total && total > 0)
            list.Add(new("vram", "MÉMOIRE VIDÉO", Gib(used), "Go", $"sur {Gib(total)} Go", Math.Clamp((double)used / total, 0, 1), false));
        if (s.GraphicsClockMHz is { } clock)
            list.Add(new("clock", "FRÉQUENCE", $"{clock}", "MHz",
                s.MaxGraphicsClockMHz is { } max ? $"max {max} MHz" : "Cœur graphique",
                s.MaxGraphicsClockMHz is { } m ? Math.Clamp((double)clock / m, 0, 1) : 0, false));
        if (s.PowerWatts is { } watts)
            list.Add(new("power", "CONSOMMATION", $"{watts:0}", "W",
                s.PowerLimitWatts is { } limit ? $"limite {limit:0} W" : "Carte graphique",
                s.PowerLimitWatts is { } lim ? Math.Clamp(watts / lim, 0, 1) : 0, false));
        if (s.FanPercent is { } fan)
            list.Add(new("fan", "VENTILATEUR", $"{fan}", "%", fan == 0 ? "À l'arrêt (mode silencieux)" : "Vitesse",
                Math.Clamp(fan / 100.0, 0, 1), false));
        return list;
    }

    private static string Gib(ulong bytes) => (bytes / 1073741824.0).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));

    private static T Export<T>(IntPtr library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private static T? OptionalExport<T>(IntPtr library, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(library, name, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

    /// <summary>
    /// nvml.dll : System32 sur la plupart des pilotes, sinon le dossier NVSMI ou le magasin de pilotes
    /// (certaines versions ne le copient plus dans System32).
    /// </summary>
    internal static IntPtr LoadLibrary()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new List<string>
        {
            Path.Combine(Environment.SystemDirectory, "nvml.dll"),
            Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll"),
            Path.Combine(programFiles, "NVIDIA", "NVSMI", "nvml.dll")
        };
        try
        {
            var store = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository");
            if (Directory.Exists(store))
                candidates.AddRange(Directory.GetDirectories(store, "nv_dispi.inf_amd64_*")
                    .OrderByDescending(Directory.GetLastWriteTimeUtc)
                    .Select(d => Path.Combine(d, "nvml.dll")));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

        foreach (var path in candidates.Where(File.Exists))
            if (NativeLibrary.TryLoad(path, out var handle)) return handle;
        return IntPtr.Zero;
    }
}
