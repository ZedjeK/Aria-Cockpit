using System.Runtime.InteropServices;

namespace Aria.Core.Nvidia;

/// <summary>Carte (nom, identifiant unique NVML) et version du pilote.</summary>
public sealed record GpuIdentity(string Name, string Uuid, string DriverVersion);

/// <summary>Erreur renvoyée par NVML lors d'un réglage, avec un message lisible.</summary>
public sealed class GpuTuningException(string message) : Exception(message);

/// <summary>
/// Réglages de la carte NVIDIA via NVML (API documentée par NVIDIA, docs.nvidia.com/deploy/nvml-api) :
/// limite de puissance, plafond de fréquence, décalages de fréquence GPU et mémoire.
/// Le pilote garde la main sur la tension : ces réglages ne peuvent pas dépasser les limites de sécurité de la carte.
/// Tous s'effacent au redémarrage (ou au rechargement du pilote). Requiert les droits administrateur.
/// </summary>
public sealed class GpuTuner
{
    private const int Success = 0, NotSupported = 3, NoPermission = 4, InvalidArgument = 2;
    private const int ClockGraphics = 0, ClockMemory = 2, PState0 = 0, PStateUnknown = 32, MaxPStates = 16;
    /// <summary>NVML_STRUCT_VERSION(ClockOffset, 1) : taille (24 octets) | (1 &lt;&lt; 24), d'après nvml.h.</summary>
    private const uint ClockOffsetV1 = 0x01000018;
    /// <summary>Fréquence de repos des cartes récentes, si le pilote ne donne pas la plus basse.</summary>
    private const uint FallbackMinClockMHz = 210;

    [StructLayout(LayoutKind.Sequential)]
    private struct ClockOffset
    {
        public uint Version;
        public int Type;
        public int PState;
        public int OffsetMHz;
        public int MinOffsetMHz;
        public int MaxOffsetMHz;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HandleByIndexFn(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UIntFn(IntPtr device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TwoUIntFn(IntPtr device, out uint a, out uint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TypedUIntFn(IntPtr device, int type, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int MinMaxOfPStateFn(IntPtr device, int type, int pstate, out uint min, out uint max);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PStatesFn(IntPtr device, [Out] int[] pstates, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetUIntFn(IntPtr device, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetTwoUIntFn(IntPtr device, uint a, uint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceFn(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OffsetFn(IntPtr device, ref ClockOffset info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntFn(IntPtr device, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TwoIntFn(IntPtr device, out int a, out int b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetIntFn(IntPtr device, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceTextFn(IntPtr device, byte[] text, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SystemTextFn(byte[] text, uint length);

    private readonly IntPtr _lib, _device;
    private readonly object _gate = new();

    public GpuTuningLimits Limits { get; }

    /// <summary>Identité de la carte et du pilote : le résultat d'Équilibre ne vaut que pour cette carte.</summary>
    public GpuIdentity Identity { get; }

    private GpuTuner(IntPtr lib, IntPtr device, GpuTuningLimits limits)
    {
        _lib = lib;
        _device = device;
        Limits = limits;
        Identity = new GpuIdentity(
            Text(Fn<DeviceTextFn>(lib, "nvmlDeviceGetName") is { } name ? b => name(device, b, (uint)b.Length) : null) ?? "Carte NVIDIA",
            Text(Fn<DeviceTextFn>(lib, "nvmlDeviceGetUUID") is { } uuid ? b => uuid(device, b, (uint)b.Length) : null) ?? "",
            Text(Fn<SystemTextFn>(lib, "nvmlSystemGetDriverVersion") is { } drv ? b => drv(b, (uint)b.Length) : null) ?? "");
    }

    private static string? Text(Func<byte[], int>? call)
    {
        if (call is null) return null;
        var buffer = new byte[256];
        if (call(buffer) != Success) return null;
        var end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end).Trim();
    }

    /// <summary>Première carte NVIDIA ; null si NVML est indisponible ou si les plages ne sont pas lisibles.</summary>
    public static GpuTuner? TryOpen()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return null;
        try
        {
            var lib = GpuTelemetry.LoadLibrary();
            if (lib == IntPtr.Zero) return null;
            if (Fn<InitFn>(lib, "nvmlInit_v2")?.Invoke() != Success) return null;
            if (Fn<HandleByIndexFn>(lib, "nvmlDeviceGetHandleByIndex_v2") is not { } byIndex || byIndex(0, out var dev) != Success) return null;
            return ReadLimits(lib, dev) is { } limits ? new GpuTuner(lib, dev, limits) : null;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// État actuel de la carte. NVML ne permet pas de relire un plafond de fréquence :
    /// <paramref name="knownMaxClock"/> est celui qu'Aria a posé (0 si aucun).
    /// </summary>
    public GpuTuningState Read(uint knownMaxClock, bool atStartup)
    {
        lock (_gate)
        {
            var power = Fn<UIntFn>(_lib, "nvmlDeviceGetPowerManagementLimit") is { } get && get(_device, out var p) == Success
                ? p
                : throw new GpuTuningException("Limite de puissance illisible.");
            return new GpuTuningState(power, knownMaxClock, ReadOffset(ClockGraphics), ReadOffset(ClockMemory), atStartup);
        }
    }

    /// <summary>
    /// Applique un réglage complet. Ordre : plafond retiré, décalages, puis plafond et limite de puissance
    /// (le plafond se calcule sur la courbe déjà décalée). Lève <see cref="GpuTuningException"/> au premier refus.
    /// </summary>
    public void Apply(GpuTuningState target)
    {
        if (Limits.Check(target) is { } error) throw new GpuTuningException(error);
        lock (_gate)
        {
            Call(Fn<DeviceFn>(_lib, "nvmlDeviceResetGpuLockedClocks")?.Invoke(_device), "Retrait du plafond de fréquence");
            WriteOffset(ClockGraphics, target.CoreOffsetMHz, "Décalage GPU");
            WriteOffset(ClockMemory, target.MemoryOffsetMHz, "Décalage mémoire");
            if (target.MaxClockMHz != 0)
                Call(Fn<SetTwoUIntFn>(_lib, "nvmlDeviceSetGpuLockedClocks")?.Invoke(_device, Limits.MinClockMHz, target.MaxClockMHz),
                    "Plafond de fréquence");
            Call(Fn<SetUIntFn>(_lib, "nvmlDeviceSetPowerManagementLimit")?.Invoke(_device, target.PowerLimitMilliwatts),
                "Limite de puissance");
        }
    }

    private int ReadOffset(int type)
    {
        if (Fn<OffsetFn>(_lib, "nvmlDeviceGetClockOffsets") is { } get)
        {
            var info = new ClockOffset { Version = ClockOffsetV1, Type = type, PState = PState0 };
            if (get(_device, ref info) == Success) return info.OffsetMHz;
        }
        var old = Fn<IntFn>(_lib, type == ClockGraphics ? "nvmlDeviceGetGpcClkVfOffset" : "nvmlDeviceGetMemClkVfOffset");
        return old is not null && old(_device, out var offset) == Success ? offset : throw new GpuTuningException("Décalage de fréquence illisible.");
    }

    private void WriteOffset(int type, int offset, string label)
    {
        if (Fn<OffsetFn>(_lib, "nvmlDeviceSetClockOffsets") is { } set)
        {
            var info = new ClockOffset { Version = ClockOffsetV1, Type = type, PState = PState0, OffsetMHz = offset };
            Call(set(_device, ref info), label);
            return;
        }
        var old = Fn<SetIntFn>(_lib, type == ClockGraphics ? "nvmlDeviceSetGpcClkVfOffset" : "nvmlDeviceSetMemClkVfOffset");
        Call(old?.Invoke(_device, offset), label);
    }

    private static GpuTuningLimits? ReadLimits(IntPtr lib, IntPtr dev)
    {
        if (Fn<TwoUIntFn>(lib, "nvmlDeviceGetPowerManagementLimitConstraints") is not { } cons || cons(dev, out var minP, out var maxP) != Success) return null;
        if (Fn<UIntFn>(lib, "nvmlDeviceGetPowerManagementDefaultLimit") is not { } def || def(dev, out var defP) != Success) return null;
        if (Fn<TypedUIntFn>(lib, "nvmlDeviceGetMaxClockInfo") is not { } maxClock || maxClock(dev, ClockGraphics, out var maxMHz) != Success) return null;
        var (coreMin, coreMax) = OffsetRange(lib, dev, ClockGraphics);
        var (memMin, memMax) = OffsetRange(lib, dev, ClockMemory);
        return new GpuTuningLimits(minP, maxP, defP, LowestClock(lib, dev), maxMHz, coreMin, coreMax, memMin, memMax);
    }

    private static (int Min, int Max) OffsetRange(IntPtr lib, IntPtr dev, int type)
    {
        if (Fn<OffsetFn>(lib, "nvmlDeviceGetClockOffsets") is { } get)
        {
            var info = new ClockOffset { Version = ClockOffsetV1, Type = type, PState = PState0 };
            if (get(dev, ref info) == Success) return (info.MinOffsetMHz, info.MaxOffsetMHz);
        }
        var old = Fn<TwoIntFn>(lib, type == ClockGraphics ? "nvmlDeviceGetGpcClkMinMaxVfOffset" : "nvmlDeviceGetMemClkMinMaxVfOffset");
        return old is not null && old(dev, out var min, out var max) == Success ? (min, max) : (0, 0);
    }

    /// <summary>
    /// Fréquence la plus basse de la carte (état de repos) : borne basse du plafond, pour que la carte
    /// redescende normalement au repos au lieu de rester haute.
    /// </summary>
    private static uint LowestClock(IntPtr lib, IntPtr dev)
    {
        if (Fn<PStatesFn>(lib, "nvmlDeviceGetSupportedPerformanceStates") is { } states
            && Fn<MinMaxOfPStateFn>(lib, "nvmlDeviceGetMinMaxClockOfPState") is { } minMax)
        {
            var buffer = new int[MaxPStates];
            Array.Fill(buffer, PStateUnknown);
            if (states(dev, buffer, (uint)(sizeof(int) * MaxPStates)) == Success)
            {
                // Le numéro le plus grand est l'état le plus économe (P8, P12…)
                var lowest = buffer.Where(s => s is >= 0 and < MaxPStates).DefaultIfEmpty(-1).Max();
                if (lowest >= 0 && minMax(dev, ClockGraphics, lowest, out var min, out _) == Success && min > 0) return min;
            }
        }
        return FallbackMinClockMHz;
    }

    private static void Call(int? result, string what)
    {
        switch (result)
        {
            case Success: return;
            case null: throw new GpuTuningException($"{what} : fonction absente de ce pilote.");
            case NotSupported: throw new GpuTuningException($"{what} : non pris en charge par ce pilote ou cette carte.");
            case NoPermission: throw new GpuTuningException($"{what} : droits administrateur requis.");
            case InvalidArgument: throw new GpuTuningException($"{what} : valeur refusée par le pilote.");
            default: throw new GpuTuningException($"{what} : erreur NVML {result}.");
        }
    }

    private static T? Fn<T>(IntPtr lib, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(lib, name, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;
}
