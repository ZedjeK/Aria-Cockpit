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
        /// <summary>Intervalle entre deux vérifications de l'image de contrôle.</summary>
        public static readonly TimeSpan VerifyInterval = TimeSpan.FromSeconds(3);

        /// <summary>Enchaîne les définitions demandées. Renvoie faux si l'utilisateur a arrêté avant la fin.</summary>
        public static bool Execute(BenchOptions options, BenchLog log)
        {
            Win32Window.EnablePerMonitorDpi();
            using var window = new Win32Window("Aria Bench", fullscreen: !options.Windowed);
            using var gpu = new GpuDevice(window.Handle, window.ClientWidth, window.ClientHeight, options.Debug, log.Remarques);
            log.Carte = gpu.Info;
            log.Contexte = SystemInfo.Read();
            if (log.Contexte.OnBattery) log.Remarques.Add("PC sur batterie : la carte est souvent bridée (60 FPS, puissance réduite). Branchez-le pour une vraie mesure.");
            if (log.Contexte.EconomieEnergie) log.Remarques.Add("Économiseur d'énergie de Windows actif : les performances peuvent être réduites.");
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
                    var check = new ImageVerification { ImageWidth = width };
                    var stats = new FrameStats();
                    var clock = Stopwatch.StartNew();
                    var last = clock.Elapsed;
                    // Référence prise à la fin de la chauffe : carte et pilote stabilisés
                    var nextCheck = options.Warmup;

                    PassResult Result() =>
                        PassResult.From(quality, definition, width, height, stats.Summarize(), check,
                            FrameCap.Detect(stats.Summarize().MedianFps, log.Contexte.FrequenceEcranHz, log.Contexte.OnBattery));

                    while (true)
                    {
                        if (!window.PumpMessages())
                        {
                            if (stats.Count > 0) log.Resultats.Add(Result());
                            return false;
                        }
                        var elapsed = clock.Elapsed;
                        if (elapsed >= passLength) break;

                        if (elapsed >= nextCheck)
                        {
                            // Vérification hors mesure : son temps n'est compté dans aucune image
                            if (check.Reference is null) check.SetReference(renderer.RenderCheck());
                            else check.Check(renderer.RenderCheck(), renderer.RenderCheck);
                            nextCheck += VerifyInterval;
                            last = clock.Elapsed;
                            continue;
                        }

                        renderer.Render((float)elapsed.TotalSeconds, (float)((done + elapsed.TotalSeconds) / total));

                        var now = clock.Elapsed;
                        // Les premières secondes (montée en fréquence, caches) ne comptent pas
                        if (elapsed >= options.Warmup) stats.Add((now - last).TotalMilliseconds);
                        last = now;
                    }

                    var result = Result();
                    log.Resultats.Add(result);
                    if (check.ReferenceUpdates > 0)
                        log.Remarques.Add($"{result.Qualite} {result.Definition} : image de référence mise à jour {check.ReferenceUpdates} fois après contre-vérification "
                                          + "(le pilote a réoptimisé ses shaders : calcul légèrement différent mais stable, ce n'est pas une erreur).");
                    if (check.FailureBounds is { } box)
                        log.Remarques.Add($"Écarts en {result.Qualite} {result.Definition} dans le rectangle x={box.X}, y={box.Y}, {box.Width} × {box.Height} pixels.");
                    if (options.SaveImages && check.Reference is { } reference && check.FirstFailure is { } failure)
                    {
                        // Images de diagnostic à côté du journal : référence, image fautive et carte des écarts
                        var folder = Path.GetDirectoryName(Path.GetFullPath(options.OutputPath ?? Path.Combine(AppContext.BaseDirectory, "x")))!;
                        var stem = Path.Combine(folder, $"bench-{quality.Id}-{definition}");
                        ImageVerification.SaveBmp(stem + "-reference.bmp", reference, width, height);
                        ImageVerification.SaveBmp(stem + "-erreur.bmp", failure, width, height);
                        ImageVerification.SaveBmp(stem + "-ecarts.bmp", ImageVerification.DifferenceMap(reference, failure), width, height);
                    }
                    if (result.ErreursCalcul > 0)
                        log.Remarques.Add($"Erreurs de calcul en {result.Qualite} {result.Definition} : {result.ErreursCalcul} vérification(s) sur {result.Verifications} "
                                          + $"(jusqu'à {result.PixelsDifferents:N0} pixels différents). La carte n'est pas stable avec ce réglage.");
                    if (result.Plafond is not null && !log.Remarques.Contains(result.Plafond)) log.Remarques.Add(result.Plafond);
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
