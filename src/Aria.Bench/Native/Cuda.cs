using System.Runtime.InteropServices;
using System.Text;

namespace Aria.Bench.Native;

/// <summary>
/// Le test des cœurs Tensor ne peut pas se faire sur cette machine (pilote sans CUDA, carte trop ancienne, noyau refusé
/// par le compilateur du pilote…). Ce n'est pas une erreur de la carte : le contrôle est signalé « non testé », jamais « instable ».
/// </summary>
internal sealed class TensorUnavailableException(string message) : Exception(message);

/// <summary>
/// Accès minimal à l'API CUDA du pilote NVIDIA : nvcuda.dll est installée avec le pilote, comme nvapi64.dll et nvml.dll,
/// sans le kit de développement CUDA. Le noyau est du PTX en texte (Shaders/Tensor.ptx), compilé par le pilote au
/// chargement : il tourne sur toutes les générations sans être recompilé. Un contexte CUDA appartient au fil qui l'a créé :
/// tous les appels se font depuis ce fil.
/// </summary>
internal sealed unsafe class CudaContext : IDisposable
{
    /// <summary>Première capacité de calcul avec des cœurs Tensor INT8 (Turing, RTX 20).</summary>
    public const int MinimumCapability = 75;

    [DllImport("nvcuda.dll")] private static extern int cuInit(uint flags);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetCount(out int count);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGet(out int device, int ordinal);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetName(byte[] name, int length, int device);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetAttribute(out int value, int attribute, int device);
    [DllImport("nvcuda.dll")] private static extern int cuDeviceGetLuid(byte[] luid, out uint nodeMask, int device);
    [DllImport("nvcuda.dll")] private static extern int cuCtxCreate_v2(out IntPtr context, uint flags, int device);
    [DllImport("nvcuda.dll")] private static extern int cuCtxDestroy_v2(IntPtr context);
    [DllImport("nvcuda.dll")] private static extern int cuCtxSynchronize();
    [DllImport("nvcuda.dll")]
    private static extern int cuModuleLoadDataEx(out IntPtr module, byte[] image, uint optionCount, int[] options, IntPtr[] optionValues);
    [DllImport("nvcuda.dll")] private static extern int cuModuleGetFunction(out IntPtr function, IntPtr module, byte[] name);
    [DllImport("nvcuda.dll")] private static extern int cuModuleUnload(IntPtr module);
    [DllImport("nvcuda.dll")] private static extern int cuMemAlloc_v2(out IntPtr pointer, UIntPtr bytes);
    [DllImport("nvcuda.dll")] private static extern int cuMemFree_v2(IntPtr pointer);
    [DllImport("nvcuda.dll")] private static extern int cuMemcpyHtoD_v2(IntPtr device, IntPtr host, UIntPtr bytes);
    [DllImport("nvcuda.dll")] private static extern int cuMemcpyDtoH_v2(IntPtr host, IntPtr device, UIntPtr bytes);
    [DllImport("nvcuda.dll")]
    private static extern int cuLaunchKernel(IntPtr function, uint gridX, uint gridY, uint gridZ, uint blockX, uint blockY, uint blockZ,
        uint sharedBytes, IntPtr stream, IntPtr[] parameters, IntPtr[]? extra);
    [DllImport("nvcuda.dll")] private static extern int cuGetErrorString(int error, out IntPtr text);

    private const int AttributeMultiprocessors = 16, AttributeCapabilityMajor = 75, AttributeCapabilityMinor = 76;
    private const int JitInfoLog = 3, JitInfoLogSize = 4, JitErrorLog = 5, JitErrorLogSize = 6;
    private const int NoDevice = 100;

    private readonly IntPtr _context;
    private readonly List<IntPtr> _allocations = [];
    private readonly List<IntPtr> _modules = [];

    public string DeviceName { get; }
    public int Capability { get; }
    public int Multiprocessors { get; }

    private CudaContext(IntPtr context, string name, int capability, int multiprocessors)
    {
        _context = context;
        DeviceName = name;
        Capability = capability;
        Multiprocessors = multiprocessors;
    }

    /// <summary>
    /// Ouvre un contexte sur la carte testée par le banc (même LUID que l'adaptateur Direct3D 12), ou sur la seule carte
    /// CUDA de la machine. Lève <see cref="TensorUnavailableException"/> si le test est impossible ici.
    /// </summary>
    public static CudaContext Open(long adapterLuid)
    {
        try
        {
            var init = cuInit(0);
            if (init == NoDevice) throw new TensorUnavailableException("aucune carte NVIDIA compatible CUDA n'est visible par le pilote.");
            if (init != 0) throw new TensorUnavailableException($"le pilote refuse d'initialiser CUDA ({Describe(init)}).");
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new TensorUnavailableException("nvcuda.dll est absente : le pilote NVIDIA est incomplet.");
        }

        if (cuDeviceGetCount(out var count) != 0 || count == 0)
            throw new TensorUnavailableException("aucune carte NVIDIA compatible CUDA n'est visible par le pilote.");

        var device = -1;
        for (var i = 0; i < count && device < 0; i++)
        {
            if (cuDeviceGet(out var candidate, i) != 0) continue;
            var luid = new byte[8];
            if (cuDeviceGetLuid(luid, out _, candidate) == 0 && BitConverter.ToInt64(luid) == adapterLuid) device = candidate;
        }
        // Sans correspondance de LUID (pilote qui ne le donne pas), une seule carte CUDA ne laisse pas de doute
        if (device < 0 && count == 1 && cuDeviceGet(out var only, 0) == 0) device = only;
        if (device < 0) throw new TensorUnavailableException("aucune carte CUDA ne correspond à la carte testée par le banc.");

        var name = new byte[256];
        cuDeviceGetName(name, name.Length, device);
        var label = Encoding.ASCII.GetString(name, 0, Math.Max(0, Array.IndexOf(name, (byte)0)));
        cuDeviceGetAttribute(out var major, AttributeCapabilityMajor, device);
        cuDeviceGetAttribute(out var minor, AttributeCapabilityMinor, device);
        cuDeviceGetAttribute(out var processors, AttributeMultiprocessors, device);
        var capability = major * 10 + minor;
        if (capability < MinimumCapability)
            throw new TensorUnavailableException($"capacité de calcul {major}.{minor} : les cœurs Tensor INT8 commencent avec Turing (RTX 20, capacité 7.5).");

        Check(cuCtxCreate_v2(out var context, 0, device), "création du contexte CUDA");
        return new CudaContext(context, label, capability, processors);
    }

    /// <summary>
    /// Charge un module PTX (texte) et renvoie son point d'entrée. Un refus du compilateur du pilote est un problème de
    /// compatibilité, pas une instabilité : <see cref="TensorUnavailableException"/>.
    /// </summary>
    public IntPtr LoadKernel(string ptx, string entry)
    {
        var image = Encoding.ASCII.GetBytes(ptx + "\0");
        var errors = Marshal.AllocHGlobal(4096);
        var info = Marshal.AllocHGlobal(4096);
        try
        {
            Marshal.WriteByte(errors, 0);
            Marshal.WriteByte(info, 0);
            int[] options = [JitErrorLog, JitErrorLogSize, JitInfoLog, JitInfoLogSize];
            IntPtr[] values = [errors, 4096, info, 4096];
            var result = cuModuleLoadDataEx(out var module, image, (uint)options.Length, options, values);
            if (result != 0)
            {
                var detail = Marshal.PtrToStringAnsi(errors)?.Trim();
                throw new TensorUnavailableException(
                    $"le pilote n'a pas pu compiler le noyau des cœurs Tensor ({Describe(result)}{(string.IsNullOrEmpty(detail) ? "" : " : " + detail)}).");
            }
            _modules.Add(module);
            Check(cuModuleGetFunction(out var function, module, Encoding.ASCII.GetBytes(entry + "\0")), "recherche du noyau");
            return function;
        }
        finally
        {
            Marshal.FreeHGlobal(errors);
            Marshal.FreeHGlobal(info);
        }
    }

    public IntPtr Alloc(long bytes)
    {
        Check(cuMemAlloc_v2(out var pointer, (UIntPtr)(ulong)bytes), "réservation de mémoire sur la carte");
        _allocations.Add(pointer);
        return pointer;
    }

    public void Upload<T>(IntPtr device, T[] data) where T : unmanaged
    {
        fixed (T* p = data) Check(cuMemcpyHtoD_v2(device, (IntPtr)p, (UIntPtr)(ulong)(data.Length * sizeof(T))), "envoi des données à la carte");
    }

    public void Download<T>(IntPtr device, T[] destination) where T : unmanaged
    {
        fixed (T* p = destination)
            Check(cuMemcpyDtoH_v2((IntPtr)p, device, (UIntPtr)(ulong)(destination.Length * sizeof(T))), "relecture des résultats");
    }

    /// <summary>Lance le noyau (grille d'une dimension). Arguments : <see cref="IntPtr"/> pour une adresse, <see cref="uint"/> pour un entier.</summary>
    public void Launch(IntPtr function, uint blocks, uint threads, params object[] arguments)
    {
        var storage = Marshal.AllocHGlobal(Math.Max(1, arguments.Length) * 8);
        var pointers = new IntPtr[arguments.Length];
        try
        {
            for (var i = 0; i < arguments.Length; i++)
            {
                var slot = storage + i * 8;
                switch (arguments[i])
                {
                    case IntPtr address: Marshal.WriteIntPtr(slot, address); break;
                    case uint number: Marshal.WriteInt32(slot, unchecked((int)number)); break;
                    default: throw new ArgumentException($"Argument de noyau non pris en charge : {arguments[i].GetType().Name}.");
                }
                pointers[i] = slot;
            }
            Check(cuLaunchKernel(function, blocks, 1, 1, threads, 1, 1, 0, IntPtr.Zero, pointers, null), "lancement du noyau");
        }
        finally
        {
            Marshal.FreeHGlobal(storage);
        }
    }

    /// <summary>Attend la fin de tout ce qui a été lancé ; une erreur ici veut presque toujours dire que la carte a fauté ou que le pilote a lâché.</summary>
    public void Synchronize() => Check(cuCtxSynchronize(), "exécution sur la carte");

    private static void Check(int result, string what)
    {
        if (result != 0) throw new BenchException($"Échec pendant {what} : {Describe(result)}.");
    }

    private static string Describe(int result)
    {
        var text = cuGetErrorString(result, out var pointer) == 0 && pointer != IntPtr.Zero ? Marshal.PtrToStringAnsi(pointer) : null;
        return $"{text ?? "erreur inconnue"}, code {result}";
    }

    public void Dispose()
    {
        foreach (var pointer in _allocations) cuMemFree_v2(pointer);
        foreach (var module in _modules) cuModuleUnload(module);
        cuCtxDestroy_v2(_context);
    }
}
