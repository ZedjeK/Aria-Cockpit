using System.Runtime.InteropServices;
using System.Text;
using Aria.Bench.Stability;

namespace Aria.Bench.Native;

/// <summary>
/// Relevé en lecture seule de l'état de la carte par NVML (nvml.dll, installé avec le pilote NVIDIA, comme nvcuda.dll) :
/// état de performance et fréquences du cœur et de la mémoire. Rien n'est réglé. Il sert au test des transitions à prouver
/// que la carte a vraiment changé d'état. Absent (pas de nvml.dll, plusieurs cartes sans nom qui corresponde) : le test
/// tourne quand même, et le dit.
/// </summary>
internal sealed class NvmlSampler : IDisposable
{
    private const int Success = 0, ClockGraphics = 0, ClockMemory = 2;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CountFn(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HandleFn(uint index, out IntPtr device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NameFn(IntPtr device, [Out] byte[] name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int StateFn(IntPtr device, out uint state);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ClockFn(IntPtr device, int type, out uint megahertz);

    private readonly IntPtr _library;
    private readonly IntPtr _device;
    private readonly InitFn _shutdown;
    private readonly StateFn _state;
    private readonly ClockFn _clock;

    private NvmlSampler(IntPtr library, IntPtr device, InitFn shutdown, StateFn state, ClockFn clock)
    {
        _library = library;
        _device = device;
        _shutdown = shutdown;
        _state = state;
        _clock = clock;
    }

    /// <summary>Ouvre NVML sur la carte du banc (reconnue par son nom, ou la seule carte NVIDIA) ; null si ce n'est pas possible.</summary>
    public static NvmlSampler? Open(string cardName)
    {
        try
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!NativeLibrary.TryLoad("nvml.dll", out var library)
                && !NativeLibrary.TryLoad(Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll"), out library))
                return null;

            T? Export<T>(string name) where T : Delegate =>
                NativeLibrary.TryGetExport(library, name, out var address) ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

            var init = Export<InitFn>("nvmlInit_v2");
            var shutdown = Export<InitFn>("nvmlShutdown");
            var count = Export<CountFn>("nvmlDeviceGetCount_v2");
            var handle = Export<HandleFn>("nvmlDeviceGetHandleByIndex_v2");
            var name = Export<NameFn>("nvmlDeviceGetName");
            var state = Export<StateFn>("nvmlDeviceGetPerformanceState");
            var clock = Export<ClockFn>("nvmlDeviceGetClockInfo");
            if (init is null || shutdown is null || count is null || handle is null || name is null || state is null || clock is null
                || init() != Success)
            {
                NativeLibrary.Free(library);
                return null;
            }

            var device = IntPtr.Zero;
            if (count(out var total) == Success)
                for (uint i = 0; i < total && device == IntPtr.Zero; i++)
                {
                    if (handle(i, out var candidate) != Success) continue;
                    var buffer = new byte[96];
                    if (name(candidate, buffer, (uint)buffer.Length) != Success) continue;
                    var label = Encoding.ASCII.GetString(buffer, 0, Math.Max(0, Array.IndexOf(buffer, (byte)0))).Trim();
                    if (string.Equals(label, cardName.Trim(), StringComparison.OrdinalIgnoreCase)) device = candidate;
                }
            // Aucun nom qui corresponde : une seule carte NVIDIA ne laisse pas de doute
            if (device == IntPtr.Zero && total == 1 && handle(0, out var only) == Success) device = only;
            if (device == IntPtr.Zero)
            {
                shutdown();
                NativeLibrary.Free(library);
                return null;
            }
            return new NvmlSampler(library, device, shutdown, state, clock);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or MarshalDirectiveException)
        {
            return null;
        }
    }

    /// <summary>Un relevé de l'état de performance et des fréquences, ajouté à la trace ; ignoré si NVML ne répond pas.</summary>
    public void Sample(TransitionTrace trace)
    {
        if (_state(_device, out var state) == Success && _clock(_device, ClockGraphics, out var core) == Success
            && _clock(_device, ClockMemory, out var memory) == Success)
            trace.Add(state, core, memory);
    }

    public void Dispose()
    {
        _shutdown();
        NativeLibrary.Free(_library);
    }
}
