using System.Runtime.InteropServices;

namespace Aria.Core.Nvidia;

/// <summary>Ce que la courbe de ventilation attend de la carte (remplacé par un faux dans les tests).</summary>
public interface IFanDevice
{
    /// <summary>Température du GPU en °C ; null si la lecture échoue.</summary>
    uint? ReadTemperature();

    /// <summary>Vitesse imposée à tous les ventilateurs, en % : ils passent en manuel.</summary>
    void SetSpeed(uint percent);

    /// <summary>Rend la main au pilote, qui suit de nouveau la température (arrêt au repos compris).</summary>
    void RestoreAuto();
}

/// <summary>
/// Ventilateurs de la carte NVIDIA via NVML (fonctions officielles, cartes Maxwell et plus récentes, droits
/// administrateur). Imposer une vitesse passe les ventilateurs en manuel : le pilote ne suit plus la température.
/// C'est alors au programme de le faire (<see cref="FanCurveRunner"/>), puis de rendre la main (<see cref="RestoreAuto"/>).
/// Un redémarrage ou un rechargement du pilote rend aussi la main au pilote.
/// </summary>
public sealed class GpuFans : IFanDevice
{
    private const int Success = 0, NotSupported = 3, NoPermission = 4, InvalidArgument = 2;
    private const int TemperatureGpu = 0;   // NVML_TEMPERATURE_GPU
    private const uint PolicyManual = 1;    // NVML_FAN_POLICY_MANUAL

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HandleByIndexFn(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UIntFn(IntPtr device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TwoUIntFn(IntPtr device, out uint a, out uint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TypedUIntFn(IntPtr device, int type, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FanUIntFn(IntPtr device, uint fan, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetFanFn(IntPtr device, uint fan, uint speed);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int FanFn(IntPtr device, uint fan);

    private readonly IntPtr _device;
    private readonly TypedUIntFn _temperature;
    private readonly FanUIntFn _speed;
    private readonly FanUIntFn? _policy;
    private readonly SetFanFn _set;
    private readonly FanFn _default;
    private readonly object _gate = new();

    /// <summary>Nombre de ventilateurs pilotables.</summary>
    public uint FanCount { get; }

    /// <summary>Plage que le pilote accepte, en % (souvent 30 à 100 : en dessous, seul le pilote peut les arrêter).</summary>
    public uint MinPercent { get; }
    public uint MaxPercent { get; }

    private GpuFans(IntPtr lib, IntPtr device, uint count, uint min, uint max)
    {
        _device = device;
        FanCount = count;
        MinPercent = min;
        MaxPercent = max;
        _temperature = Fn<TypedUIntFn>(lib, "nvmlDeviceGetTemperature")!;
        _speed = Fn<FanUIntFn>(lib, "nvmlDeviceGetFanSpeed_v2")!;
        _policy = Fn<FanUIntFn>(lib, "nvmlDeviceGetFanControlPolicy_v2");
        _set = Fn<SetFanFn>(lib, "nvmlDeviceSetFanSpeed_v2")!;
        _default = Fn<FanFn>(lib, "nvmlDeviceSetDefaultFanSpeed_v2")!;
    }

    /// <summary>Ventilateurs de la première carte NVIDIA ; null sans NVML, sans ventilateur ou sur une carte trop ancienne.</summary>
    public static GpuFans? TryOpen()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return null;
        try
        {
            var lib = GpuTelemetry.LoadLibrary();
            if (lib == IntPtr.Zero) return null;
            if (Fn<InitFn>(lib, "nvmlInit_v2")?.Invoke() != Success) return null;
            if (Fn<HandleByIndexFn>(lib, "nvmlDeviceGetHandleByIndex_v2") is not { } byIndex || byIndex(0, out var dev) != Success) return null;
            // Toutes les fonctions de pilotage sont indispensables : sans l'une d'elles, pas de courbe
            string[] required = ["nvmlDeviceGetTemperature", "nvmlDeviceGetFanSpeed_v2", "nvmlDeviceSetFanSpeed_v2", "nvmlDeviceSetDefaultFanSpeed_v2"];
            if (required.Any(name => !NativeLibrary.TryGetExport(lib, name, out _))) return null;
            if (Fn<UIntFn>(lib, "nvmlDeviceGetNumFans") is not { } count || count(dev, out var fans) != Success || fans == 0) return null;
            var (min, max) = Fn<TwoUIntFn>(lib, "nvmlDeviceGetMinMaxFanSpeed") is { } range && range(dev, out var lo, out var hi) == Success
                                                                                && hi > lo && hi <= 100
                ? (lo, hi)
                : (0u, 100u);
            return new GpuFans(lib, dev, fans, min, max);
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException or BadImageFormatException)
        {
            return null;
        }
    }

    public uint? ReadTemperature()
    {
        lock (_gate) return _temperature(_device, TemperatureGpu, out var t) == Success ? t : null;
    }

    /// <summary>Vitesse réelle de chaque ventilateur, en % (null si illisible).</summary>
    public IReadOnlyList<uint?> ReadSpeeds()
    {
        lock (_gate)
            return Enumerable.Range(0, (int)FanCount)
                .Select(i => _speed(_device, (uint)i, out var s) == Success ? s : (uint?)null)
                .ToList();
    }

    /// <summary>Vrai si un ventilateur est resté en manuel (laissé par Aria ou par un autre outil).</summary>
    public bool AnyManual()
    {
        if (_policy is null) return false;
        lock (_gate)
            return Enumerable.Range(0, (int)FanCount).Any(i => _policy(_device, (uint)i, out var p) == Success && p == PolicyManual);
    }

    public void SetSpeed(uint percent)
    {
        var speed = Math.Clamp(percent, MinPercent, MaxPercent);
        lock (_gate)
            for (uint fan = 0; fan < FanCount; fan++)
                Call(_set(_device, fan, speed), $"Ventilateur {fan + 1}");
    }

    /// <summary>Rend la main au pilote sur tous les ventilateurs, même si l'un d'eux refuse.</summary>
    public void RestoreAuto()
    {
        string? error = null;
        lock (_gate)
            for (uint fan = 0; fan < FanCount; fan++)
            {
                try { Call(_default(_device, fan), $"Ventilateur {fan + 1}"); }
                catch (GpuTuningException e) { error ??= e.Message; }
            }
        if (error is not null) throw new GpuTuningException($"Retour au mode automatique impossible. {error}");
    }

    private static void Call(int result, string what)
    {
        switch (result)
        {
            case Success: return;
            case NotSupported: throw new GpuTuningException($"{what} : non pris en charge par ce pilote ou cette carte.");
            case NoPermission: throw new GpuTuningException($"{what} : droits administrateur requis.");
            case InvalidArgument: throw new GpuTuningException($"{what} : valeur refusée par le pilote.");
            default: throw new GpuTuningException($"{what} : erreur NVML {result}.");
        }
    }

    private static T? Fn<T>(IntPtr lib, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(lib, name, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;
}
