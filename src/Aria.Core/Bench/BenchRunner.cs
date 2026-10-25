using System.Diagnostics;
using System.Text.Json;
using Aria.Core.Inventory;

namespace Aria.Core.Bench;

/// <summary>Une passe du banc d'essai, telle qu'Aria-Bench l'écrit dans son journal JSON.</summary>
public sealed record BenchPass(string Qualite, string Definition, int Images, double FpsMoyen, double FpsMedian, double Fps1PourcentBas,
    int Saccades, int Verifications, int ErreursCalcul, long PixelsDifferents, string? Plafond);

/// <summary>Ce qu'Aria Cockpit retient d'une exécution du banc d'essai.</summary>
public sealed record BenchResult(string Statut, IReadOnlyList<BenchPass> Resultats, IReadOnlyList<string> Erreurs, IReadOnlyList<string> Remarques)
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

    /// <summary>Verdict de stabilité : erreur de calcul ou plantage du pilote.</summary>
    public bool Unstable => CalculationErrors || DriverFailure;

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static BenchResult Parse(string json)
    {
        var dto = JsonSerializer.Deserialize<Dto>(json, Json) ?? throw new JsonException("Journal vide");
        return new BenchResult(dto.Statut ?? "", dto.Resultats ?? [], dto.Erreurs ?? [], dto.Remarques ?? []);
    }

    public static BenchResult Missing(string why) => new("erreur", [], [why], []);

    private sealed record Dto(string? Statut, List<BenchPass>? Resultats, List<string>? Erreurs, List<string>? Remarques);
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
    public static async Task<BenchResult> RunAsync(string quality, int definition, TimeSpan duration, string output, CancellationToken stop)
    {
        var exe = FindExe() ?? throw new FileNotFoundException($"{ExeName} est introuvable à côté d'Aria Cockpit.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        if (File.Exists(output)) File.Delete(output);

        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var arg in Arguments(quality, definition, duration, output)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{ExeName} n'a pas démarré.");
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
