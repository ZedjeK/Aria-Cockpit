using System.Runtime.InteropServices;
using System.Text;

namespace Aria.Core.Nvidia;

/// <summary>Résultat d'un test : <see cref="Supported"/> null quand on n'a pas pu conclure.</summary>
public sealed record ProbeCheck(string Name, bool? Supported, string Detail);

/// <summary>Ce que le pilote accepte comme réglages de la carte, et les plages permises.</summary>
public sealed record GpuTuningReport(string GpuName, string DriverVersion, IReadOnlyList<ProbeCheck> Checks, IReadOnlyList<string> Facts);

/// <summary>
/// Vérifie si le pilote NVIDIA accepte, via NVML (API documentée), les trois réglages d'efficacité :
/// limite de puissance, plafond de fréquence, décalage de fréquence.
/// Aucun réglage n'est modifié : chaque test réécrit la valeur déjà en place (ou remet la valeur par défaut
/// du plafond, qu'Aria n'a jamais posé). Requiert les droits administrateur.
/// </summary>
public static class GpuTuningProbe
{
    private const int Success = 0, InvalidArgument = 2, NotSupported = 3, NoPermission = 4, FunctionNotFound = 13, VersionMismatch = 25;
    /// <summary>Code interne : la fonction n'existe pas dans ce nvml.dll.</summary>
    private const int Missing = -1;
    private const int ClockGraphics = 0, ClockMemory = 2, PState0 = 0;
    /// <summary>NVML_STRUCT_VERSION(ClockOffset, 1) : taille de la structure (24 octets) | (1 &lt;&lt; 24).</summary>
    private const uint ClockOffsetV1 = 0x01000018;

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
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NameFn(IntPtr device, byte[] name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DriverFn(byte[] version, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UIntFn(IntPtr device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TwoUIntFn(IntPtr device, out uint a, out uint b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TypedUIntFn(IntPtr device, int type, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetUIntFn(IntPtr device, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DeviceFn(IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OffsetFn(IntPtr device, ref ClockOffset info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int IntFn(IntPtr device, out int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TwoIntFn(IntPtr device, out int a, out int b);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetIntFn(IntPtr device, int value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr ErrorStringFn(int code);

    /// <summary>null si aucune carte NVIDIA n'est accessible par NVML.</summary>
    public static GpuTuningReport? Run()
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return null;
        var lib = GpuTelemetry.LoadLibrary();
        if (lib == IntPtr.Zero) return null;
        if (Fn<InitFn>(lib, "nvmlInit_v2")?.Invoke() != Success) return null;
        if (Fn<HandleByIndexFn>(lib, "nvmlDeviceGetHandleByIndex_v2") is not { } byIndex || byIndex(0, out var dev) != Success) return null;

        var errors = Fn<ErrorStringFn>(lib, "nvmlErrorString");
        string Code(int code) => code switch
        {
            Success => "accepté",
            NotSupported => "non pris en charge par ce pilote ou cette carte",
            NoPermission => "refusé (droits administrateur requis)",
            InvalidArgument => "valeur refusée",
            FunctionNotFound or Missing => "fonction absente de ce pilote",
            VersionMismatch => "version de structure non reconnue (pilote trop ancien ?)",
            _ => $"erreur {code}" + (errors?.Invoke(code) is { } p && p != IntPtr.Zero ? $" ({Marshal.PtrToStringAnsi(p)})" : "")
        };

        var name = Text(Fn<NameFn>(lib, "nvmlDeviceGetName"), dev) ?? "Carte NVIDIA";
        var driver = Fn<DriverFn>(lib, "nvmlSystemGetDriverVersion") is { } drv && Buffer(b => drv(b, (uint)b.Length)) is { } d ? d : "?";
        var checks = new List<ProbeCheck>();
        var facts = new List<string>();

        // ---- 1. Limite de puissance : on réécrit la limite actuelle (aucun changement) ----
        var get = Fn<UIntFn>(lib, "nvmlDeviceGetPowerManagementLimit");
        var current = get is not null && get(dev, out var cur) == Success ? cur : (uint?)null;
        if (Fn<UIntFn>(lib, "nvmlDeviceGetPowerManagementDefaultLimit") is { } def && def(dev, out var dflt) == Success)
            facts.Add($"Limite de puissance par défaut : {dflt / 1000.0:0} W");
        if (current is { } c1) facts.Add($"Limite de puissance actuelle : {c1 / 1000.0:0} W");
        if (Fn<TwoUIntFn>(lib, "nvmlDeviceGetPowerManagementLimitConstraints") is { } cons && cons(dev, out var min, out var max) == Success)
            facts.Add($"Plage autorisée par la carte : {min / 1000.0:0} à {max / 1000.0:0} W");
        if (current is { } same && Fn<SetUIntFn>(lib, "nvmlDeviceSetPowerManagementLimit") is { } setPower)
        {
            var r = setPower(dev, same);
            checks.Add(new("Limite de puissance", r == Success, Code(r)));
        }
        else checks.Add(new("Limite de puissance", null, current is null ? "limite actuelle illisible" : Code(Missing)));

        // ---- 2. Plafond de fréquence : remise à la valeur par défaut (Aria n'en a jamais posé) ----
        if (Fn<TypedUIntFn>(lib, "nvmlDeviceGetMaxClockInfo") is { } maxClock && maxClock(dev, ClockGraphics, out var mhz) == Success)
            facts.Add($"Fréquence graphique maximale : {mhz} MHz");
        if (Fn<DeviceFn>(lib, "nvmlDeviceResetGpuLockedClocks") is { } reset)
        {
            var r = reset(dev);
            checks.Add(new("Plafond de fréquence", r == Success,
                r == Success ? "accepté (remise à la valeur par défaut, comme après un redémarrage)" : Code(r)));
        }
        else checks.Add(new("Plafond de fréquence", false, Code(Missing)));

        // ---- 3. Décalage de fréquence (P0) : on réécrit le décalage actuel ----
        checks.Add(Offset(lib, dev, ClockGraphics, "Décalage fréquence graphique", facts, Code));
        checks.Add(Offset(lib, dev, ClockMemory, "Décalage fréquence mémoire", facts, Code));

        return new GpuTuningReport(name, driver, checks, facts);
    }

    private static ProbeCheck Offset(IntPtr lib, IntPtr dev, int type, string label, List<string> facts, Func<int, string> code)
    {
        // API actuelle (pilotes R555+)
        if (Fn<OffsetFn>(lib, "nvmlDeviceGetClockOffsets") is { } getOffsets && Fn<OffsetFn>(lib, "nvmlDeviceSetClockOffsets") is { } setOffsets)
        {
            var info = new ClockOffset { Version = ClockOffsetV1, Type = type, PState = PState0 };
            var r = getOffsets(dev, ref info);
            if (r != Success) return new(label, r == NotSupported ? false : null, $"lecture : {code(r)}");
            facts.Add($"{label} : actuel {info.OffsetMHz:+0;-0;0} MHz, plage {info.MinOffsetMHz:+0;-0;0} à {info.MaxOffsetMHz:+0;-0;0} MHz");
            var write = new ClockOffset { Version = ClockOffsetV1, Type = type, PState = PState0, OffsetMHz = info.OffsetMHz };
            r = setOffsets(dev, ref write);
            return new(label, r == Success, code(r));
        }

        // Anciennes fonctions (dépréciées par NVIDIA, encore présentes)
        var (get, range, set) = type == ClockGraphics
            ? ("nvmlDeviceGetGpcClkVfOffset", "nvmlDeviceGetGpcClkMinMaxVfOffset", "nvmlDeviceSetGpcClkVfOffset")
            : ("nvmlDeviceGetMemClkVfOffset", "nvmlDeviceGetMemClkMinMaxVfOffset", "nvmlDeviceSetMemClkVfOffset");
        if (Fn<IntFn>(lib, get) is not { } oldGet || Fn<SetIntFn>(lib, set) is not { } oldSet) return new(label, false, code(Missing));
        var g = oldGet(dev, out var offset);
        if (g != Success) return new(label, g == NotSupported ? false : null, $"lecture : {code(g)}");
        if (Fn<TwoIntFn>(lib, range) is { } oldRange && oldRange(dev, out var lo, out var hi) == Success)
            facts.Add($"{label} : actuel {offset:+0;-0;0} MHz, plage {lo:+0;-0;0} à {hi:+0;-0;0} MHz (ancienne API)");
        var s = oldSet(dev, offset);
        return new(label, s == Success, code(s) + " (ancienne API)");
    }

    private static T? Fn<T>(IntPtr lib, string name) where T : Delegate =>
        NativeLibrary.TryGetExport(lib, name, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

    private static string? Text(NameFn? fn, IntPtr dev) => fn is null ? null : Buffer(b => fn(dev, b, (uint)b.Length));

    private static string? Buffer(Func<byte[], int> call)
    {
        var buffer = new byte[256];
        if (call(buffer) != Success) return null;
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end < 0 ? buffer.Length : end).Trim();
    }
}
