using System.Diagnostics;
using Vortice.Direct3D12;

namespace Aria.Bench.Stability;

/// <summary>
/// Calcul vérifié, sur une file à part : la carte chauffe et tire sur sa limite de puissance, comme pendant l'inférence
/// d'un LLM. Le calcul est déterministe : la première exécution sert de référence, chaque exécution suivante doit donner
/// exactement le même résultat, sinon le cœur a fauté (décalage de fréquence ou undervolt trop poussés).
/// </summary>
internal sealed class BurnTest(ComputeKit kit)
{
    private const uint Groups = 4096;                  // un million de fils
    public const int Threads = (int)Groups * 256;
    private const int ReportBytes = 256;
    private static readonly TimeSpan TargetDispatch = TimeSpan.FromMilliseconds(60);

    public long Dispatches { get; private set; }
    public long Mismatches { get; private set; }

    /// <summary>Fils fautifs relevés (8 au plus par lot) et nombre de fois : toujours les mêmes = référence ou code en cause, au hasard = la carte.</summary>
    public Dictionary<uint, int> FaultyThreads { get; } = [];
    public List<WrongWord> FirstErrors { get; } = [];
    public uint Iterations { get; private set; } = 2048;

    /// <summary>Auto-contrôle : une exécution est lancée exprès avec une itération de plus, tous ses résultats doivent différer.</summary>
    public bool InjectFault { get; init; }

    public void Run(Func<bool> keepGoing)
    {
        using var results = kit.Buffer((ulong)Threads * 4, "Résultats du calcul");
        using var report = kit.Buffer(ReportBytes, "Compteurs du calcul");
        using var readback = kit.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(ReportBytes), ResourceStates.CopyDest);
        using var zero = kit.Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(ReportBytes), ResourceStates.GenericRead);
        zero.SetData(new byte[ReportBytes]);
        using var lane = new Lane(kit, CommandListType.Compute, "Calcul vérifié");

        // Étalonnage : des exécutions d'environ 60 ms, pour garder la carte occupée sans bloquer le pilote
        var watch = Stopwatch.StartNew();
        Record(lane, results, report, readback, zero, reference: true, count: 1);
        var seconds = watch.Elapsed.TotalSeconds;
        Iterations = (uint)Math.Clamp(Iterations * TargetDispatch.TotalSeconds / Math.Max(seconds, 1e-3), 256, 1 << 20);
        Record(lane, results, report, readback, zero, reference: true, count: 1);  // référence avec la longueur retenue

        if (InjectFault)
        {
            Mismatches += Record(lane, results, report, readback, zero, reference: false, count: 1, extra: 1);
            Dispatches += 1;
        }
        while (keepGoing())
        {
            Mismatches += Record(lane, results, report, readback, zero, reference: false, count: 8);
            Dispatches += 8;
        }
    }

    private unsafe long Record(Lane lane, ID3D12Resource results, ID3D12Resource report, ID3D12Resource readback, ID3D12Resource zero,
        bool reference, int count, uint extra = 0)
    {
        var list = lane.Begin();
        list.ResourceBarrierTransition(results, ResourceStates.Common, ResourceStates.UnorderedAccess);
        list.ResourceBarrierTransition(report, ResourceStates.Common, ResourceStates.UnorderedAccess);
        list.SetPipelineState(kit.Burn);
        list.SetComputeRootUnorderedAccessView(0, results.GPUVirtualAddress);
        list.SetComputeRootUnorderedAccessView(1, report.GPUVirtualAddress);
        var constants = stackalloc uint[8] { 0, 0, 0, reference ? 0u : 1u, 0, 0, Iterations + extra, 0 };
        list.SetComputeRoot32BitConstants(2, 8, constants, 0);
        for (var i = 0; i < count; i++)
        {
            list.Dispatch(Groups, 1, 1);
            list.ResourceBarrierUnorderedAccessView(results);
        }
        list.ResourceBarrierTransition(report, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        list.CopyBufferRegion(readback, 0, report, 0, ReportBytes);
        list.ResourceBarrierTransition(report, ResourceStates.CopySource, ResourceStates.CopyDest);
        list.CopyBufferRegion(report, 0, zero, 0, ReportBytes);
        lane.Run();

        var p = readback.Map<uint>(0);
        try
        {
            if (extra == 0 && !reference)
                lock (FaultyThreads)
                    for (var slot = 0; slot < Math.Min(8, (int)p[0]); slot++)
                    {
                        var e = p + 4 + slot * 3;
                        FaultyThreads[e[0]] = FaultyThreads.GetValueOrDefault(e[0]) + 1;
                        if (FirstErrors.Count < 16) FirstErrors.Add(new WrongWord(e[0], $"0x{e[1]:X8}", $"0x{e[2]:X8}"));
                    }
            return p[0];
        }
        finally { readback.Unmap(0); }
    }
}
