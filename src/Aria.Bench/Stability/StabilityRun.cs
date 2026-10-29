using System.Diagnostics;
using System.Text.Json;
using Aria.Bench.Native;
using Aria.Bench.Rendering;
using SharpGen.Runtime;

namespace Aria.Bench.Stability;

/// <summary>
/// Contrôles de stabilité d'Aria-Bench, repris du « Test VRAM discriminant » (session Aria Pilote, 26/10/2026) :
/// --calcul (calcul vérifié seul, à pleine puissance) et --vram (calcul seul pendant un tiers du temps, puis mémoire vidéo
/// seule). Le calcul s'arrête quand la mémoire commence : dans l'outil d'origine, il était de toute façon presque à l'arrêt
/// pendant le test mémoire ; s'il continue, il partage la carte et la bande passante mesurée chute (180 Go/s au lieu de 570),
/// ce qui fausserait la comparaison entre paliers mémoire. Le rendu 3D ne sollicite pas le cœur ni la mémoire comme une IA locale : un undervolt validé au banc
/// a donné 17 130 calculs faux à pleine puissance, et des « illegal memory access » au chargement d'un LLM.
/// </summary>
internal static class StabilityRun
{
    /// <summary>Part de la mémoire vidéo libre testée (le reste pour Windows et les autres programmes).</summary>
    public const double MemoryShare = 0.7;
    /// <summary>Durée de l'auto-contrôle : le temps de retrouver les erreurs injectées exprès.</summary>
    public static readonly TimeSpan SelfCheckDuration = TimeSpan.FromSeconds(20);

    /// <summary>Renvoie faux si l'utilisateur a arrêté avant la fin (Échap).</summary>
    public static bool Run(GpuDevice gpu, Win32Window window, BenchOptions options, BenchLog log)
    {
        var memoryTested = options.Stability == StabilityMode.Memory;
        var seconds = options.SelfCheck ? SelfCheckDuration.TotalSeconds : options.Duration.TotalSeconds;
        var previous = ReadPrevious(options.PreviousPath, log);

        using var kit = new ComputeKit(gpu);
        var (budget, usage) = gpu.LocalMemory();
        var free = budget > usage ? budget - usage : 0;
        var blocks = (int)Math.Clamp((long)(free * MemoryShare / MemoryTest.BlockBytes), 2, 64);
        var memory = memoryTested ? new MemoryTest(kit, blocks) { InjectFault = options.SelfCheck } : null;
        var burn = new BurnTest(kit) { InjectFault = options.SelfCheck };

        var clock = Stopwatch.StartNew();
        var stop = false;
        var failures = new List<string>();
        bool KeepGoing() => !Volatile.Read(ref stop) && clock.Elapsed.TotalSeconds < seconds;

        void Guard(string what, Action work)
        {
            try { work(); }
            catch (Exception e) when (e is BenchException or SharpGenException)
            {
                var reason = e is SharpGenException s ? GpuDevice.Explain(s.HResult) : e.Message;
                var removed = kit.RemovedReason();
                lock (failures) failures.Add($"{what} : {reason}{(removed is not null && !reason.Contains(removed) ? $" ({removed})" : "")}");
                Volatile.Write(ref stop, true);
            }
        }

        // Avec la mémoire, le calcul tourne d'abord seul (un tiers du temps), à pleine puissance, là où le cœur faute ;
        // puis la mémoire seule, pour une bande passante propre. L'auto-contrôle fait tourner les deux ensemble
        var burnAlone = memory is not null && !options.SelfCheck ? seconds / 3.0 : 0;
        bool BurnGoing() => KeepGoing() && (burnAlone == 0 || clock.Elapsed.TotalSeconds < burnAlone);
        var threads = new List<Thread>();
        if (memory is not null)
            threads.Add(new Thread(() => Guard("test mémoire", () =>
            {
                while (clock.Elapsed.TotalSeconds < burnAlone && !Volatile.Read(ref stop)) Thread.Sleep(200);
                if (KeepGoing()) memory.Run(KeepGoing, clock);
            })));
        threads.Add(new Thread(() => Guard("calcul vérifié", () => burn.Run(BurnGoing))));
        foreach (var t in threads) t.Start();

        var interrupted = false;
        while (threads.Any(t => t.IsAlive))
        {
            if (!window.PumpMessages())
            {
                interrupted = true;
                Volatile.Write(ref stop, true);
            }
            Thread.Sleep(50);
        }
        foreach (var t in threads) t.Join();

        List<MemoryPass> passes;
        if (memory is null) passes = [];
        else lock (memory.Passes) passes = [.. memory.Passes];
        var report = StabilityReport.Conclude(memoryTested ? "memoire" : "calcul", options.SelfCheck, memoryTested, burnTested: true,
            passes, memory?.WrongWords ?? 0, memory?.FlippedBits ?? 0, burn.Dispatches, burn.Mismatches, failures, previous,
            seconds, clock.Elapsed.TotalSeconds);
        report.PhaseCalculSeulS = Math.Round(burnAlone, 1);
        report.MemoireTesteeGio = memory is null ? 0 : Math.Round(memory.TestedBytes / 1073741824.0, 1);
        report.IterationsCalcul = burn.Iterations;
        lock (burn.FaultyThreads) report.FilsFautifsDistincts = burn.FaultyThreads.Count;
        report.PremieresErreurs = memory?.FirstErrors ?? [];
        report.PremiersEcartsCalcul = burn.FirstErrors;
        log.Stabilite = report;

        // Une ligne de résultats, pour qu'Aria Cockpit compte les fautes comme les erreurs de calcul du rendu
        var faults = options.SelfCheck ? (report.Statut == StabilityReport.Passed ? 0 : 1) : burn.Mismatches + (memory?.WrongWords ?? 0);
        log.Resultats.Add(new PassResult(memoryTested ? "Mémoire vidéo et calcul" : "Calcul vérifié",
            memoryTested ? $"{report.MemoireTesteeGio:0.0} Gio".Replace('.', ',') : "-", 0, 0, (int)Math.Min(int.MaxValue, burn.Dispatches),
            report.DureeS, 0, 0, 0, 0, 0, 0, 0, (int)Math.Min(int.MaxValue, burn.Dispatches + passes.Count),
            (int)Math.Min(int.MaxValue, faults), memory?.WrongWords ?? 0, null, null));
        log.Remarques.Add(Summary(report));
        if (failures.Count > 0) log.Erreurs.AddRange(failures);
        return !interrupted;
    }

    private static string Summary(StabilityReport r)
    {
        var parts = new List<string>();
        if (r.ExecutionsCalcul > 0) parts.Add($"calcul : {r.ExecutionsCalcul} exécutions vérifiées, {r.EcartsCalcul} écart(s)");
        if (r.Passes > 0)
            parts.Add($"mémoire : {r.MemoireTesteeGio:0.0} Gio, {r.Passes} passes, {r.MotsFaux} mot(s) faux" +
                      (r.BandePassanteLectureMedianeGoS is { } read ? $", lecture médiane {read:0} Go/s" : ""));
        var verdict = r.Statut switch
        {
            StabilityReport.Passed => r.AutoControle ? "auto-contrôle réussi : le test voit bien les erreurs" : "aucun signe d'instabilité",
            StabilityReport.Warning => "à surveiller : " + string.Join(" ", r.Avertissements),
            _ => (r.AutoControle ? "auto-contrôle raté : " : "réglage instable : ") + string.Join(" ", r.Raisons)
        };
        return $"Contrôle de stabilité ({string.Join(" ; ", parts)}) : {verdict}";
    }

    /// <summary>Bande passante du palier précédent (journal d'un --vram), pour vérifier que la mémoire va plus vite.</summary>
    private static double? ReadPrevious(string? path, BenchLog log)
    {
        if (path is null) return null;
        try
        {
            var previous = BenchLog.FromJson(File.ReadAllText(path));
            return previous.Stabilite?.BandePassanteLectureMedianeGoS;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            log.Remarques.Add($"Résultat précédent illisible ({path}) : pas de comparaison de bande passante.");
            return null;
        }
    }
}
