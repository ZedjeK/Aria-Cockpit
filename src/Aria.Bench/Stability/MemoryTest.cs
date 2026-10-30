using System.Diagnostics;
using Vortice.Direct3D12;

namespace Aria.Bench.Stability;

/// <summary>Une passe complète sur toute la mémoire testée : écriture, puis relecture vérifiée.</summary>
public sealed record MemoryPass(int Numero, bool Melange, double EcritureGoS, double LectureGoS, long MotsFaux, long BitsFaux, double ASecondes);

/// <summary>Une adresse ou un fil fautif : numéro, valeur attendue, valeur relue.</summary>
public sealed record WrongWord(ulong Numero, string Attendu, string Relu);

/// <summary>
/// Test mémoire mené par des shaders de calcul : chaque mot reçoit une valeur calculée à partir de son adresse et de la
/// passe, puis un shader relit tout, recalcule et compte les écarts sur la carte elle-même. Une passe sur deux accède à la
/// mémoire dans un ordre mélangé (sauts de ligne, comme l'inférence d'un LLM), l'autre dans l'ordre (bande passante maximale).
/// </summary>
internal sealed class MemoryTest(ComputeKit kit, int blocks)
{
    public const ulong BlockBytes = 256UL * 1024 * 1024;
    private const uint Chunks = (uint)(BlockBytes / 16);        // groupes de 16 octets par bloc : 2^24
    private const uint Groups = 16384;                           // 4 millions de fils : 4 groupes de 16 octets chacun
    private const uint Stride = Groups * 256;
    private const uint Mul = 0x9E3779B1u;                        // impair : la multiplication est une permutation
    private const int ReportBytes = 256;
    public const int InjectedWords = ReportBytes / 4;

    /// <summary>Auto-contrôle : à la première passe, 64 mots sont écrasés par des zéros après l'écriture.</summary>
    public bool InjectFault { get; init; }
    public List<MemoryPass> Passes { get; } = [];
    public List<WrongWord> FirstErrors { get; } = [];
    public long WrongWords { get; private set; }
    public long FlippedBits { get; private set; }
    public int Blocks => blocks;
    public ulong TestedBytes => (ulong)blocks * BlockBytes;

    public void Run(Func<bool> keepGoing, Stopwatch clock)
    {
        var data = new List<ID3D12Resource>();
        ID3D12Resource? report = null, zero = null, readback = null;
        try
        {
            for (var i = 0; i < blocks; i++) data.Add(kit.Buffer(BlockBytes, $"Mémoire testée {i}"));
            report = kit.Buffer(ReportBytes, "Compteurs mémoire");
            zero = kit.Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(ReportBytes), ResourceStates.GenericRead);
            zero.SetData(new byte[ReportBytes]);
            readback = kit.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(ReportBytes), ResourceStates.CopyDest);

            using var lane = new Lane(kit, CommandListType.Direct, "Test mémoire");
            for (var pass = 0; keepGoing(); pass++)
            {
                var scattered = pass % 2 == 1;
                var seed = (uint)(pass * 0x632BE5AB + 0x1234567);

                // Écriture : toutes les valeurs de la passe
                var list = lane.Begin();
                ToUav(list, data, report);
                list.SetPipelineState(kit.Fill);
                Dispatch(list, data, report, seed, scattered);
                if (InjectFault && pass == 0)
                {
                    list.ResourceBarrierTransition(data[0], ResourceStates.UnorderedAccess, ResourceStates.CopyDest);
                    list.CopyBufferRegion(data[0], 0, zero, 0, ReportBytes);
                }
                var watch = Stopwatch.StartNew();
                lane.Run();
                var writeSeconds = watch.Elapsed.TotalSeconds;

                // Relecture vérifiée sur la carte, puis rapatriement des compteurs et remise à zéro
                list = lane.Begin();
                ToUav(list, data, report);
                list.SetPipelineState(kit.Verify);
                Dispatch(list, data, report, seed, scattered);
                list.ResourceBarrierTransition(report, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
                list.CopyBufferRegion(readback, 0, report, 0, ReportBytes);
                list.ResourceBarrierTransition(report, ResourceStates.CopySource, ResourceStates.CopyDest);
                list.CopyBufferRegion(report, 0, zero, 0, ReportBytes);
                watch.Restart();
                lane.Run();
                var readSeconds = watch.Elapsed.TotalSeconds;

                var (wrong, bits) = ReadReport(readback);
                WrongWords += wrong;
                FlippedBits += bits;
                var gigabytes = TestedBytes / 1e9;
                lock (Passes)
                    Passes.Add(new MemoryPass(pass, scattered, Math.Round(gigabytes / writeSeconds, 1), Math.Round(gigabytes / readSeconds, 1),
                        wrong, bits, Math.Round(clock.Elapsed.TotalSeconds, 1)));
            }
        }
        finally
        {
            readback?.Dispose();
            zero?.Dispose();
            report?.Dispose();
            foreach (var d in data) d.Dispose();
        }
    }

    // Les tampons repassent seuls à l'état COMMON à la fin de chaque exécution : on les remet en accès UAV à chaque liste
    private static void ToUav(ID3D12GraphicsCommandList list, List<ID3D12Resource> data, ID3D12Resource report)
    {
        foreach (var d in data) list.ResourceBarrierTransition(d, ResourceStates.Common, ResourceStates.UnorderedAccess);
        list.ResourceBarrierTransition(report, ResourceStates.Common, ResourceStates.UnorderedAccess);
    }

    private static unsafe void Dispatch(ID3D12GraphicsCommandList list, List<ID3D12Resource> data, ID3D12Resource report, uint seed, bool scattered)
    {
        list.SetComputeRootUnorderedAccessView(1, report.GPUVirtualAddress);
        var constants = stackalloc uint[8] { 0, Chunks, seed, scattered ? 1u : 0u, Stride, Mul, 0, 0 };
        for (var i = 0; i < data.Count; i++)
        {
            constants[0] = (uint)i * Chunks;
            list.SetComputeRootUnorderedAccessView(0, data[i].GPUVirtualAddress);
            list.SetComputeRoot32BitConstants(2, 8, constants, 0);
            list.Dispatch(Groups, 1, 1);
        }
    }

    private unsafe (long Wrong, long Bits) ReadReport(ID3D12Resource readback)
    {
        var p = readback.Map<uint>(0);
        try
        {
            long wrong = p[0], bits = p[1];
            if (wrong > 0)
                lock (FirstErrors)
                    for (var slot = 0; slot < Math.Min(8, (int)p[2]) && FirstErrors.Count < 16; slot++)
                    {
                        var e = p + 4 + slot * 3;
                        FirstErrors.Add(new WrongWord(e[0], $"0x{e[1]:X8}", $"0x{e[2]:X8}"));
                    }
            return (wrong, bits);
        }
        finally
        {
            readback.Unmap(0);
        }
    }
}
