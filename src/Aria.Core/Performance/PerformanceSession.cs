using Aria.Core.Nvidia;

namespace Aria.Core.Performance;

/// <summary>
/// Enregistre une mesure par seconde pendant que l'utilisateur joue, puis rend le verdict.
/// Lecture seule : rien n'est modifié sur la carte ni sur le processeur.
/// </summary>
public static class PerformanceSession
{
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(5);

    /// <param name="stop">Arrêt anticipé : le verdict porte alors sur les secondes déjà enregistrées.</param>
    /// <param name="progress">Appelé à chaque seconde avec le temps écoulé.</param>
    public static async Task<PerformanceReport> RunAsync(GpuTelemetry gpu, TimeSpan duration,
        IProgress<TimeSpan>? progress = null, CancellationToken stop = default)
    {
        var samples = new List<PerfSample>();
        var game = new GameTracker();
        using var cpu = await Task.Run(() => new CpuLoadMonitor()).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;
        var startedLocal = DateTime.Now;
        try
        {
            while (DateTime.UtcNow - started < duration && await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                var sample = new PerfSample(gpu.Read(), cpu.Read());
                samples.Add(sample);
                // Le jeu : le programme qui affiche avec la carte pendant qu'elle travaille
                game.Observe(gpu.GraphicsProcesses(), sample.Gpu.LoadPercent >= PerformanceAnalyzer.ActiveLoadPercent);
                progress?.Report(DateTime.UtcNow - started);
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé : on analyse ce qui a été enregistré
        }
        // Plantages notés par Windows pendant l'analyse (avec 5 s de marge avant le début)
        var incidents = await Task.Run(() => SessionIncidents.Collect(startedLocal.AddSeconds(-5), game.Game)).ConfigureAwait(false);
        return PerformanceAnalyzer.Analyze(samples, DateTime.Now, incidents);
    }
}
