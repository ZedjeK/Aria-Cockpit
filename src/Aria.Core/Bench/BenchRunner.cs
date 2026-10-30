using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Aria.Core.Inventory;

namespace Aria.Core.Bench;

/// <summary>Une passe du banc d'essai, telle qu'Aria-Bench l'écrit dans son journal JSON.</summary>
/// <param name="ArrierePlanS">Secondes mesurées hors du premier plan (le pilote bride alors le banc, souvent à 30 FPS).</param>
public sealed record BenchPass(string Qualite, string Definition, int Images, double FpsMoyen, double FpsMedian, double Fps1PourcentBas,
    int Saccades, int Verifications, int ErreursCalcul, long PixelsDifferents, string? Plafond, double Secondes = 0, double ArrierePlanS = 0);

/// <summary>
/// Contrôle de stabilité d'Aria-Bench (--calcul, --vram, --auto-controle) : calcul vérifié à pleine puissance et test de la
/// mémoire vidéo. Statut : « reussi », « attention » (bande passante qui ne monte plus) ou « echec ».
/// </summary>
public sealed record BenchStability(string Statut, string Mode, bool AutoControle, long ExecutionsCalcul, long EcartsCalcul,
    int Passes, long MotsFaux, double? BandePassanteLectureMedianeGoS, bool PasPlusRapide,
    IReadOnlyList<string>? Raisons = null, IReadOnlyList<string>? Avertissements = null);

/// <summary>Contrôle de stabilité demandé à Aria-Bench à la place du rendu 3D.</summary>
public enum BenchCheck
{
    /// <summary>Calcul vérifié à pleine puissance (--calcul) : les fautes du cœur (décalage, undervolt).</summary>
    Compute,
    /// <summary>Calcul seul, puis mémoire vidéo seule (--vram) : données corrompues et bande passante.</summary>
    Memory,
    /// <summary>Erreurs injectées exprès (--auto-controle) : le contrôle doit toutes les retrouver.</summary>
    SelfCheck
}

/// <summary>Ce qu'Aria Cockpit retient d'une exécution du banc d'essai.</summary>
public sealed record BenchResult(string Statut, IReadOnlyList<BenchPass> Resultats, IReadOnlyList<string> Erreurs, IReadOnlyList<string> Remarques,
    BenchStability? Stabilite = null)
{
    public bool Completed => Statut == "terminé";
    public bool Interrupted => Statut == "interrompu";
    public bool Failed => !Completed && !Interrupted;

    /// <summary>La dernière passe (l'assistant n'en lance qu'une).</summary>
    public BenchPass? Pass => Resultats.Count > 0 ? Resultats[^1] : null;

    /// <summary>Au moins une image de contrôle différait de la référence : la carte calcule faux.</summary>
    public bool CalculationErrors => Resultats.Any(r => r.ErreursCalcul > 0);

    /// <summary>Le pilote a lâché pendant le rendu (carte retirée, ne répond plus…) ou le banc s'est arrêté sans journal.</summary>
    public bool DriverFailure => Failed && Erreurs.Any(e =>
        e.Contains("DXGI_ERROR", StringComparison.Ordinal) || e.Contains("ne répond plus", StringComparison.Ordinal)
        || e.Contains("journal", StringComparison.Ordinal) || e.Contains("s'est arrêté", StringComparison.Ordinal));

    /// <summary>Le contrôle de stabilité a trouvé un calcul faux ou une donnée relue fausse (hors auto-contrôle).</summary>
    public bool StabilityFailed => Stabilite is { AutoControle: false, Statut: "echec" };

    /// <summary>Palier mémoire pas plus rapide que le précédent : la GDDR6X corrige au lieu d'accélérer.</summary>
    public bool NotFaster => Stabilite?.PasPlusRapide == true;

    /// <summary>L'auto-contrôle a retrouvé toutes les erreurs injectées : les verdicts des contrôles sont fiables.</summary>
    public bool SelfCheckPassed => Completed && Stabilite is { AutoControle: true, Statut: "reussi" };

    /// <summary>Temps hors du premier plan toléré : quelques secondes (une notification), au plus 2 % de la mesure au-delà.</summary>
    public const double MinBackgroundSeconds = 5;
    public const double MaxBackgroundShare = 0.02;
    /// <summary>Charge moyenne en dessous de laquelle des FPS collés à une limite veulent dire « carte pas mesurée à fond ».</summary>
    public const double FullLoadPercent = 90;

    /// <summary>Le banc a passé plus de quelques secondes hors du premier plan : le pilote l'a bridé, la mesure est faussée.</summary>
    public bool RanInBackground => Pass is { } p && p.ArrierePlanS > Math.Max(MinBackgroundSeconds, p.Secondes * MaxBackgroundShare);

    /// <summary>
    /// FPS collés à une limite alors que la carte n'a pas été chargée à fond : elle n'a pas été mesurée. Une petite carte qui fait
    /// vraiment 30 FPS en 4K tourne, elle, à pleine charge.
    /// </summary>
    public bool CappedBelowFullLoad(double averageLoadPercent) => Pass?.Plafond is not null && averageLoadPercent < FullLoadPercent;

    /// <summary>Verdict de stabilité : erreur de calcul (image ou contrôle), donnée relue fausse, ou plantage du pilote.</summary>
    public bool Unstable => CalculationErrors || DriverFailure || StabilityFailed;

    /// <summary>
    /// Rendu 3D et contrôles d'un même palier réunis : les passes des contrôles d'abord (la dernière reste celle du rendu,
    /// pour les FPS), le statut le plus grave, le dernier contrôle de stabilité.
    /// </summary>
    public static BenchResult Merge(BenchResult render, params BenchResult[] checks)
    {
        var all = checks.Append(render).ToList();
        var statut = all.Any(r => r.Failed) ? all.First(r => r.Failed).Statut : all.Any(r => r.Interrupted) ? "interrompu" : render.Statut;
        return new BenchResult(statut, [.. checks.SelectMany(c => c.Resultats), .. render.Resultats],
            [.. all.SelectMany(r => r.Erreurs)], [.. all.SelectMany(r => r.Remarques)],
            checks.LastOrDefault(c => c.Stabilite is not null)?.Stabilite ?? render.Stabilite);
    }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static BenchResult Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json, Json) ?? throw new JsonException("Journal vide");
        return new BenchResult(dto.Statut ?? "", dto.Resultats ?? [], dto.Erreurs ?? [], dto.Remarques ?? [], dto.Stabilite);
    }

    public static BenchResult Missing(string why) => new("erreur", [], [why], []);

    private sealed record Dto(string? Statut, List<BenchPass>? Resultats, List<string>? Erreurs, List<string>? Remarques, BenchStability? Stabilite);
}

/// <summary>
/// Lance Aria-Bench (livré à côté d'Aria-Cockpit.exe) pour une passe, attend la fin et lit son journal.
/// Le banc tourne dans son propre processus : s'il fait planter le pilote, Aria Cockpit reste debout et le constate.
/// </summary>
public static class BenchRunner
{
    public const string ExeName = "Aria-Bench.exe";
    /// <summary>Temps de démarrage du banc (shaders, image de référence) plus sa chauffe de 3 s, avant les images mesurées.</summary>
    public static readonly TimeSpan LeadTime = TimeSpan.FromSeconds(6);
    /// <summary>Marge ajoutée à la durée du banc pour couvrir la mesure de télémétrie qui démarre après <see cref="LeadTime"/>.</summary>
    public static readonly TimeSpan Tail = TimeSpan.FromSeconds(5);

    [DllImport("user32.dll", ExactSpelling = true)] private static extern bool AllowSetForegroundWindow(int processId);

    public static string? FindExe(string? folder = null)
    {
        var path = Path.Combine(folder ?? AppContext.BaseDirectory, ExeName);
        return File.Exists(path) ? path : null;
    }

    public static bool IsAvailable => FindExe() is not null;

    public static IReadOnlyList<string> Arguments(string quality, int definition, TimeSpan duration, string output) =>
        ["--qualite", quality, "--def", definition.ToString(System.Globalization.CultureInfo.InvariantCulture),
         "--duree", ((int)Math.Ceiling(duration.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture),
         "--sortie", output];

    /// <summary>Arguments d'un contrôle de stabilité ; <paramref name="previous"/> : journal du palier mémoire précédent.</summary>
    public static IReadOnlyList<string> CheckArguments(BenchCheck check, TimeSpan duration, string output, string? previous = null)
    {
        var args = new List<string>
        {
            check switch { BenchCheck.Compute => "--calcul", BenchCheck.Memory => "--vram", _ => "--auto-controle" },
            "--duree", ((int)Math.Ceiling(duration.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--sortie", output
        };
        if (previous is not null && check == BenchCheck.Memory) args.AddRange(["--precedent", previous]);
        return args;
    }

    /// <summary>Définition du banc d'après la hauteur de l'écran : 1080p, 1440p ou 4K (jamais plus que l'écran, sauf s'il est plus petit que 1080p).</summary>
    public static int DefinitionFor(int screenHeight) => screenHeight >= 2160 ? 2160 : screenHeight >= 1440 ? 1440 : 1080;

    /// <summary>Définition suivante, pour charger davantage une carte que la limite d'images bride ; null après la 4K.</summary>
    public static int? NextDefinition(int definition) => definition switch { 1080 => 1440, 1440 => 2160, _ => null };

    /// <summary>PC portable sur batterie : la carte y est bridée (60 FPS, puissance réduite), la mesure serait faussée.</summary>
    public static bool OnBattery() =>
        OperatingSystem.IsWindows() && GetSystemPowerStatus(out var status) != 0 && status.ACLineStatus == 0;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>Hauteur de l'écran principal en pixels réels (0 si illisible).</summary>
    public static int PrimaryScreenHeight()
    {
        if (!OperatingSystem.IsWindows()) return 0;
        var mode = NativeDisplay.DEVMODE.Create();
        return NativeDisplay.EnumDisplaySettings(null!, -1, ref mode) ? mode.dmPelsHeight : 0;
    }

    /// <summary>
    /// Lance une passe et attend sa fin. Annulation : le banc est arrêté et le résultat porte le statut « interrompu ».
    /// </summary>
    public static Task<BenchResult> RunAsync(string quality, int definition, TimeSpan duration, string output, CancellationToken stop) =>
        RunProcessAsync(Arguments(quality, definition, duration, output), duration, output, stop);

    /// <summary>Lance un contrôle de stabilité (calcul vérifié, mémoire vidéo ou auto-contrôle) et attend sa fin.</summary>
    public static Task<BenchResult> RunCheckAsync(BenchCheck check, TimeSpan duration, string output, string? previous, CancellationToken stop) =>
        RunProcessAsync(CheckArguments(check, duration, output, previous), duration, output, stop);

    private static async Task<BenchResult> RunProcessAsync(IReadOnlyList<string> arguments, TimeSpan duration, string output, CancellationToken stop)
    {
        var exe = FindExe() ?? throw new FileNotFoundException($"{ExeName} est introuvable à côté d'Aria Cockpit.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (File.Exists(output)) File.Delete(output);

        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{ExeName} n'a pas démarré.");
        // Le banc peut prendre le premier plan même lancé sans clic (sinon le pilote le bride, souvent à 30 FPS)
        AllowSetForegroundWindow(process.Id);
        try
        {
            // Garde-fou : démarrage, chauffe, vérifications et fermeture tiennent largement dans 2 minutes de plus
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(duration + TimeSpan.FromMinutes(2));
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Arrêt demandé : fermeture propre (comme Échap), le banc écrit son journal avec ce qu'il a déjà mesuré
            if (stop.IsCancellationRequested && await CloseAsync(process).ConfigureAwait(false)) return Read(output);
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* déjà terminé */ }
            return stop.IsCancellationRequested
                ? new BenchResult("interrompu", [], [], [])
                : BenchResult.Missing("Le banc d'essai ne s'est pas terminé à temps : arrêté.");
        }
        return Read(output);
    }

    private static async Task<bool> CloseAsync(Process process)
    {
        try
        {
            if (process.HasExited || !process.CloseMainWindow()) return process.HasExited;
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Lit le journal ; absent ou illisible, c'est que le banc s'est arrêté brutalement.</summary>
    public static BenchResult Read(string output)
    {
        try
        {
            return File.Exists(output)
                ? BenchResult.Parse(File.ReadAllText(output))
                : BenchResult.Missing("Le banc d'essai s'est arrêté sans écrire son journal (plantage probable du pilote).");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return BenchResult.Missing($"Journal du banc d'essai illisible : {e.Message}");
        }
    }
}
