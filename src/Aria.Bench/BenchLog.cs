using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aria.Bench;

/// <summary>
/// Journal d'une exécution, écrit en JSON à côté de l'exe (et copié dans bench-dernier.json).
/// C'est ce fichier qu'Aria Cockpit lira pour récupérer les résultats.
/// </summary>
public sealed class BenchLog
{
    public const string StatusRunning = "en cours", StatusDone = "terminé", StatusInterrupted = "interrompu", StatusError = "erreur";

    public string Outil { get; init; } = "Aria-Bench";
    public string Version { get; init; } = "";
    public DateTime Date { get; init; } = DateTime.Now;
    public string Statut { get; set; } = StatusRunning;
    public string Options { get; init; } = "";
    public GpuInfo? Carte { get; set; }
    public SystemContext? Contexte { get; set; }
    public List<PassResult> Resultats { get; init; } = [];
    public List<string> Remarques { get; init; } = [];
    public List<string> Erreurs { get; init; } = [];
    public List<string> MessagesDirect3D { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static BenchLog FromJson(string json) =>
        JsonSerializer.Deserialize<BenchLog>(json, Json) ?? throw new JsonException("Journal vide");

    /// <summary>Écrit le journal ; une copie « bench-dernier.json » à côté facilite la lecture du dernier passage.</summary>
    public void Save(string path)
    {
        var json = ToJson();
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(folder);
        File.WriteAllText(path, json);
        File.WriteAllText(Path.Combine(folder, "bench-dernier.json"), json);
    }
}

public sealed record GpuInfo(string Nom, string VendorId, string DeviceId, long MemoireMo, string PiloteWindows, string? PiloteNvidia,
    IReadOnlyList<string> CartesDetectees);

/// <summary>Résultat d'une passe (un préréglage à une définition).</summary>
/// <param name="Verifications">Images de contrôle recalculées pendant la passe.</param>
/// <param name="ErreursCalcul">Vérifications dont l'image différait de la référence : signe d'instabilité.</param>
/// <param name="PixelsDifferents">Le plus grand nombre de pixels différents constaté.</param>
/// <param name="Plafond">Explication si les FPS semblent bridés par une limite, sinon absent.</param>
/// <param name="ReferencesAjustees">Références remplacées après contre-vérification (pilote qui réoptimise ses shaders).</param>
public sealed record PassResult(string Qualite, string Definition, int Largeur, int Hauteur, int Images, double Secondes,
    double FpsMoyen, double FpsMedian, double TempsMoyenMs, double Fps1PourcentBas, double TempsP99Ms, double TempsMaxMs, int Saccades,
    int Verifications, int ErreursCalcul, long PixelsDifferents, string? Empreinte, string? Plafond, int ReferencesAjustees = 0)
{
    public static PassResult From(QualityPreset quality, int definition, int width, int height, FrameSummary s,
        ImageVerification? check = null, string? cap = null) =>
        new(quality.Label, BenchOptions.Label(definition), width, height, s.Frames, s.Seconds,
            s.AverageFps, s.MedianFps, s.AverageMs, s.Low1PercentFps, s.P99Ms, s.MaxMs, s.Stutters,
            check?.Checks ?? 0, check?.Failures ?? 0, check?.MaxDifferentPixels ?? 0, check?.Fingerprint, cap, check?.ReferenceUpdates ?? 0);
}

/// <summary>Conditions de la mesure : elles expliquent un résultat bridé.</summary>
public sealed record SystemContext(string Alimentation, bool EconomieEnergie, int FrequenceEcranHz)
{
    [JsonIgnore] public bool OnBattery => Alimentation == "batterie";
}

/// <summary>Erreur attendue (carte absente, pilote qui lâche…) : son message va tel quel dans le journal.</summary>
public sealed class BenchException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Version du pilote telle que Windows la donne (32.0.16.1088) et sa forme NVIDIA (610.88).</summary>
public static class DriverVersion
{
    public static string Windows(long umd) =>
        $"{(umd >> 48) & 0xFFFF}.{(umd >> 32) & 0xFFFF}.{(umd >> 16) & 0xFFFF}.{umd & 0xFFFF}";

    /// <summary>Les 5 derniers chiffres de la version Windows : 32.0.16.1088 → 610.88.</summary>
    public static string? Nvidia(long umd)
    {
        var third = (umd >> 16) & 0xFFFF;
        var fourth = umd & 0xFFFF;
        if (fourth > 9999) return null;
        var digits = $"{third % 10}{fourth:D4}";
        return $"{int.Parse(digits[..3])}.{digits[3..]}";
    }
}
