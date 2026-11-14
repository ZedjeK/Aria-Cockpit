using System.Diagnostics;
using Vortice.Direct3D12;

namespace Aria.Bench.Stability;

/// <summary>Ce qu'une phase des cycles a donné, tous cycles confondus : calculs vérifiés, écarts, mots de mémoire relus faux.</summary>
public sealed record TransitionPhase(string Phase, long Executions, long Ecarts, long MotsFaux);

/// <summary>
/// Stabilité quand la carte change d'état. Un contrôle soutenu à pleine charge ne traverse jamais les états bas : la
/// fréquence mémoire et la tension du cœur y sont ailleurs, et c'est là qu'un décalage mémoire ou un undervolt (qui décale
/// toute la courbe, y compris ses basses tensions) peut faire fauter une carte qui tient à pleine charge. Chaque cycle :
/// la mémoire est écrite, la carte se repose (le pilote redescend), la mémoire est relue, puis des rafales de calcul vérifié
/// à 5, 20, 50 et 100 % de charge font traverser les états, la mémoire étant relue après chaque phase.
/// Le calcul est celui de <see cref="BurnTest"/> (même shader, même référence) ; la mémoire est celle de <see cref="MemoryTest"/>.
/// </summary>
internal sealed class TransitionTest(ComputeKit kit, int blocks)
{
    public const string IdlePhase = "repos";
    public static readonly (string Name, double Duty)[] Phases =
        [("charge 5 %", 0.05), ("charge 20 %", 0.20), ("charge 50 %", 0.50), ("pleine charge", 1.0)];

    private const uint Groups = 4096;
    private const int ReportBytes = 256;
    private const int FullLoadDispatches = 8;
    private static readonly TimeSpan Period = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TargetDispatch = TimeSpan.FromMilliseconds(5);

    /// <summary>Repos entre l'écriture et la relecture : le temps que le pilote redescende (il reste en P2 plusieurs secondes après une charge).</summary>
    public TimeSpan IdleGap { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan PhaseLength { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Auto-contrôle : 64 mots de mémoire écrasés après la première écriture, et une exécution de calcul faussée exprès.</summary>
    public bool InjectFault { get; init; }

    /// <summary>Cycles menés jusqu'au bout.</summary>
    public long Cycles { get; private set; }
    public long ComputeRuns { get; private set; }
    public long Mismatches { get; private set; }
    public long WrongWords { get; private set; }
    public long FlippedBits { get; private set; }
    public uint Iterations { get; private set; } = 2048;
    public int Blocks => blocks;
    public ulong TestedBytes => (ulong)blocks * MemoryTest.BlockBytes;
    public List<WrongWord> FirstMemoryErrors { get; } = [];
    public List<WrongWord> FirstComputeErrors { get; } = [];

    private readonly Dictionary<string, (long Runs, long Mismatches, long Words)> _phases = [];

    /// <summary>Les phases dans l'ordre d'un cycle, avec ce que chacune a donné.</summary>
    public IReadOnlyList<TransitionPhase> PhaseResults => [.. new[] { IdlePhase }.Concat(Phases.Select(p => p.Name))
        .Select(n => _phases.TryGetValue(n, out var v) ? new TransitionPhase(n, v.Runs, v.Mismatches, v.Words) : new TransitionPhase(n, 0, 0, 0))];

    public void Run(Func<bool> keepGoing)
    {
        var data = new List<ID3D12Resource>();
        ID3D12Resource? memoryReport = null, results = null, computeReport = null, zero = null, memoryReadback = null, computeReadback = null;
        try
        {
            for (var i = 0; i < blocks; i++) data.Add(kit.Buffer(MemoryTest.BlockBytes, $"Mémoire des transitions {i}"));
            memoryReport = kit.Buffer(ReportBytes, "Compteurs mémoire des transitions");
            results = kit.Buffer((ulong)BurnTest.Threads * 4, "Résultats du calcul des transitions");
            computeReport = kit.Buffer(ReportBytes, "Compteurs du calcul des transitions");
            zero = kit.Device.CreateCommittedResource(HeapType.Upload, ResourceDescription.Buffer(ReportBytes), ResourceStates.GenericRead);
            zero.SetData(new byte[ReportBytes]);
            memoryReadback = kit.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(ReportBytes), ResourceStates.CopyDest);
            computeReadback = kit.Device.CreateCommittedResource(HeapType.Readback, ResourceDescription.Buffer(ReportBytes), ResourceStates.CopyDest);
            using var lane = new Lane(kit, CommandListType.Direct, "Transitions");

            // Étalonnage à pleine vitesse : des exécutions d'environ 5 ms, référence prise avec la longueur retenue
            Compute(lane, results, computeReport, computeReadback, zero, reference: true, count: 1);
            var watch = Stopwatch.StartNew();
            Compute(lane, results, computeReport, computeReadback, zero, reference: true, count: 1);
            Iterations = (uint)Math.Clamp(Iterations * TargetDispatch.TotalSeconds / Math.Max(watch.Elapsed.TotalSeconds, 1e-3), 64, 1 << 20);
            Compute(lane, results, computeReport, computeReadback, zero, reference: true, count: 1);
            watch.Restart();
            Compute(lane, results, computeReport, computeReadback, zero, reference: true, count: 1);
            var dispatchSeconds = Math.Max(watch.Elapsed.TotalSeconds, 1e-3);

            for (var cycle = 0; keepGoing(); cycle++)
            {
                var seed = unchecked((uint)(cycle * 0x632BE5AB + 0x1234567));
                var inject = InjectFault && cycle == 0;
                Fill(lane, data, memoryReport, zero, seed, inject);
                if (inject)
                {
                    Mismatches += Compute(lane, results, computeReport, computeReadback, zero, reference: false, count: 1, extra: 1);
                    ComputeRuns++;
                }
                if (!Wait(IdleGap, keepGoing)) break;
                Check(IdlePhase, lane, data, memoryReport, zero, memoryReadback, seed);

                var complete = true;
                foreach (var (name, duty) in Phases)
                {
                    if (!keepGoing()) { complete = false; break; }
                    Burst(name, duty, dispatchSeconds, lane, results, computeReport, computeReadback, zero, keepGoing);
                    Check(name, lane, data, memoryReport, zero, memoryReadback, seed);
                }
                if (complete && keepGoing()) Cycles++;
            }
        }
        finally
        {
            computeReadback?.Dispose();
            memoryReadback?.Dispose();
            zero?.Dispose();
            computeReport?.Dispose();
            results?.Dispose();
            memoryReport?.Dispose();
            foreach (var d in data) d.Dispose();
        }
    }

    /// <summary>Une phase : des rafales de calcul vérifié, au rapport cyclique demandé (une période de 100 ms, le reste au repos).</summary>
    private void Burst(string name, double duty, double dispatchSeconds, Lane lane, ID3D12Resource results, ID3D12Resource report,
        ID3D12Resource readback, ID3D12Resource zero, Func<bool> keepGoing)
    {
        var perPeriod = duty >= 1 ? FullLoadDispatches : Math.Max(1, (int)Math.Round(duty * Period.TotalSeconds / dispatchSeconds));
        var clock = Stopwatch.StartNew();
        long runs = 0, bad = 0;
        while (clock.Elapsed < PhaseLength && keepGoing())
        {
            var start = clock.Elapsed;
            bad += Compute(lane, results, report, readback, zero, reference: false, count: perPeriod);
            runs += perPeriod;
            if (duty < 1 && Period - (clock.Elapsed - start) is { } rest && rest > TimeSpan.Zero) Thread.Sleep(rest);
        }
        ComputeRuns += runs;
        Mismatches += bad;
        Add(name, runs, bad, 0);
    }

    private static bool Wait(TimeSpan duration, Func<bool> keepGoing)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < duration)
        {
            if (!keepGoing()) return false;
            Thread.Sleep(50);
        }
        return true;
    }

    private void Add(string phase, long runs, long mismatches, long words)
    {
        var (r, m, w) = _phases.GetValueOrDefault(phase);
        _phases[phase] = (r + runs, m + mismatches, w + words);
    }

    /// <summary>Écrit toute la mémoire testée avec les valeurs de la graine (ordre linéaire, bande passante maximale).</summary>
    private static void Fill(Lane lane, List<ID3D12Resource> data, ID3D12Resource report, ID3D12Resource zero, uint seed, bool inject)
    {
        var list = lane.Begin();
        MemoryTest.ToUav(list, data, report);
        list.SetPipelineState(lane.Kit.Fill);
        MemoryTest.Dispatch(list, data, report, seed, scattered: false);
        if (inject)
        {
            list.ResourceBarrierTransition(data[0], ResourceStates.UnorderedAccess, ResourceStates.CopyDest);
            list.CopyBufferRegion(data[0], 0, zero, 0, ReportBytes);
        }
        lane.Run();
    }

    /// <summary>Relit toute la mémoire testée sur la carte et compte les mots faux, après la phase donnée.</summary>
    private unsafe void Check(string phase, Lane lane, List<ID3D12Resource> data, ID3D12Resource report, ID3D12Resource zero,
        ID3D12Resource readback, uint seed)
    {
        var list = lane.Begin();
        MemoryTest.ToUav(list, data, report);
        list.SetPipelineState(lane.Kit.Verify);
        MemoryTest.Dispatch(list, data, report, seed, scattered: false);
        list.ResourceBarrierTransition(report, ResourceStates.UnorderedAccess, ResourceStates.CopySource);
        list.CopyBufferRegion(readback, 0, report, 0, ReportBytes);
        list.ResourceBarrierTransition(report, ResourceStates.CopySource, ResourceStates.CopyDest);
        list.CopyBufferRegion(report, 0, zero, 0, ReportBytes);
        lane.Run();

        var p = readback.Map<uint>(0);
        try
        {
            long wrong = p[0], bits = p[1];
            WrongWords += wrong;
            FlippedBits += bits;
            if (wrong > 0)
                for (var slot = 0; slot < Math.Min(8, (int)p[2]) && FirstMemoryErrors.Count < 16; slot++)
                {
                    var e = p + 4 + slot * 3;
                    FirstMemoryErrors.Add(new WrongWord(e[0], $"0x{e[1]:X8}", $"0x{e[2]:X8}"));
                }
            Add(phase, 0, 0, wrong);
        }
        finally
        {
            readback.Unmap(0);
        }
    }

    /// <summary>Le calcul vérifié de <see cref="BurnTest"/> : la référence est la première exécution, chaque autre doit lui être identique.</summary>
    private unsafe long Compute(Lane lane, ID3D12Resource results, ID3D12Resource report, ID3D12Resource readback, ID3D12Resource zero,
        bool reference, int count, uint extra = 0)
    {
        var list = lane.Begin();
        list.ResourceBarrierTransition(results, ResourceStates.Common, ResourceStates.UnorderedAccess);
        list.ResourceBarrierTransition(report, ResourceStates.Common, ResourceStates.UnorderedAccess);
        list.SetPipelineState(lane.Kit.Burn);
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
                for (var slot = 0; slot < Math.Min(8, (int)p[0]) && FirstComputeErrors.Count < 16; slot++)
                {
                    var e = p + 4 + slot * 3;
                    FirstComputeErrors.Add(new WrongWord(e[0], $"0x{e[1]:X8}", $"0x{e[2]:X8}"));
                }
            return p[0];
        }
        finally
        {
            readback.Unmap(0);
        }
    }
}
