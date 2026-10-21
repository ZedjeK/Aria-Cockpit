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
        using var cpu = await Task.Run(() => new CpuLoadMonitor()).ConfigureAwait(false);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var started = DateTime.UtcNow;
        try
        {
            while (DateTime.UtcNow - started < duration && await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                samples.Add(new PerfSample(gpu.Read(), cpu.Read()));
                progress?.Report(DateTime.UtcNow - started);
            }
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé : on analyse ce qui a été enregistré
        }
        return PerformanceAnalyzer.Analyze(samples, DateTime.Now);
    }
}
