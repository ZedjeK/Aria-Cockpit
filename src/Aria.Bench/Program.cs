using System.Diagnostics;
using System.Reflection;
using Aria.Bench;
using Aria.Bench.Native;
using Aria.Bench.Rendering;

// Aria-Bench : banc d'essai 3D d'Aria Cockpit.
// Codes de sortie : 0 terminé, 1 erreur, 2 interrompu par l'utilisateur (Échap).

var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
var log = new BenchLog { Version = version, Options = string.Join(' ', args) };
string outputPath = Path.Combine(AppContext.BaseDirectory, $"bench-{DateTime.Now:yyyyMMdd-HHmmss}.json");

try
{
    var options = BenchOptions.Parse(args);
    if (options.OutputPath is not null) outputPath = Path.GetFullPath(options.OutputPath);
    log.Statut = BenchRun.Execute(options, log) ? BenchLog.StatusDone : BenchLog.StatusInterrupted;
}
catch (Exception e) when (e is BenchException or ArgumentException)
{
    log.Statut = BenchLog.StatusError;
    log.Erreurs.Add(e.Message);
}
catch (Exception e)
{
    log.Statut = BenchLog.StatusError;
    log.Erreurs.Add($"{e.GetType().Name} : {e.Message}");
    log.Erreurs.Add(e.StackTrace ?? "");
}

try { log.Save(outputPath); }
catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* rien d'autre à faire sans fenêtre */ }

return log.Statut switch
{
    BenchLog.StatusDone => 0,
    BenchLog.StatusInterrupted => 2,
    _ => 1
};

namespace Aria.Bench
{
    internal static class BenchRun
    {
        /// <summary>Enchaîne les définitions demandées. Renvoie faux si l'utilisateur a arrêté avant la fin.</summary>
        public static bool Execute(BenchOptions options, BenchLog log)
        {
            Win32Window.EnablePerMonitorDpi();
            using var window = new Win32Window("Aria Bench", fullscreen: !options.Windowed);
            using var gpu = new GpuDevice(window.Handle, window.ClientWidth, window.ClientHeight, options.Debug, log.Remarques);
            log.Carte = gpu.Info;
            log.Remarques.Add($"Écran : {window.ClientWidth} × {window.ClientHeight}{(window.Fullscreen ? ", plein écran" : ", fenêtre")}.");

            try
            {
                using var renderer = new Renderer(gpu);
                var passLength = options.Warmup + options.Duration;
                var passes = options.Qualities.SelectMany(q => options.Definitions.Select(d => (Quality: q, Definition: d))).ToList();
                var total = passLength.TotalSeconds * passes.Count;
                var done = 0.0;

                foreach (var (quality, definition) in passes)
                {
                    var (width, height) = BenchOptions.SizeOf(definition);
                    renderer.Configure(quality, width, height);
                    var stats = new FrameStats();
                    var clock = Stopwatch.StartNew();
                    var last = clock.Elapsed;

                    while (true)
                    {
                        if (!window.PumpMessages())
                        {
                            if (stats.Count > 0) log.Resultats.Add(PassResult.From(quality, definition, width, height, stats.Summarize()));
                            return false;
                        }
                        var elapsed = clock.Elapsed;
                        if (elapsed >= passLength) break;

                        renderer.Render((float)elapsed.TotalSeconds, (float)((done + elapsed.TotalSeconds) / total));

                        var now = clock.Elapsed;
                        // Les premières secondes (montée en fréquence, caches) ne comptent pas
                        if (elapsed >= options.Warmup) stats.Add((now - last).TotalMilliseconds);
                        last = now;
                    }

                    log.Resultats.Add(PassResult.From(quality, definition, width, height, stats.Summarize()));
                    done += passLength.TotalSeconds;
                }
                return true;
            }
            finally
            {
                log.MessagesDirect3D.AddRange(gpu.DrainDebugMessages());
            }
        }
    }
}
