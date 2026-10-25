using System.Text.Json;
using Aria.Core.Backup;
using Aria.Core.Performance;

namespace Aria.Core.Nvidia;

/// <summary>
/// Résultat du mode Équilibre, gardé dans equilibre.json : il débloque Économie et Performance,
/// qui partent de ce réglage et se comparent à la même mesure de départ.
/// </summary>
/// <param name="Origin">Réglage de la carte avant l'assistant (format <see cref="GpuTuningState.ToRaw"/>).</param>
/// <param name="Result">Réglage retenu par Équilibre, recul de sécurité compris.</param>
/// <param name="FoundOffset">Un décalage stable a été trouvé (sinon : réglage d'origine plafonné).</param>
/// <param name="Baseline">Mesure de départ, au réglage d'origine.</param>
public sealed record BalancedRecord(
    DateTime At,
    string GpuName,
    string GpuUuid,
    string DriverVersion,
    string? Game,
    string Origin,
    string Result,
    bool FoundOffset,
    PerformanceStats Baseline)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(BackupStore.DataRoot, "equilibre.json");

    public GpuTuningState? ResultState => GpuTuningState.TryParse(Result, out var s) ? s : null;
    public GpuTuningState? OriginState => GpuTuningState.TryParse(Origin, out var s) ? s : null;

    /// <summary>Le résultat vaut pour cette carte (même identifiant NVML ; à défaut, même nom).</summary>
    public bool Matches(GpuIdentity gpu) =>
        GpuUuid.Length > 0 && gpu.Uuid.Length > 0
            ? string.Equals(GpuUuid, gpu.Uuid, StringComparison.OrdinalIgnoreCase)
            : string.Equals(GpuName, gpu.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Le pilote a changé depuis : refaire Équilibre est conseillé (la courbe fréquence/tension peut bouger).</summary>
    public bool DriverChanged(GpuIdentity gpu) =>
        DriverVersion.Length > 0 && gpu.DriverVersion.Length > 0 && !string.Equals(DriverVersion, gpu.DriverVersion, StringComparison.Ordinal);

    /// <summary>Résultat d'Équilibre pour cette carte ; null s'il n'existe pas, est illisible ou concerne une autre carte.</summary>
    public static BalancedRecord? LoadFor(GpuIdentity gpu) => Load() is { } r && r.Matches(gpu) && r.ResultState is not null ? r : null;

    public static BalancedRecord? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<BalancedRecord>(File.ReadAllText(FilePath), Json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(BackupStore.DataRoot);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Sérialisation seule (tests).</summary>
    internal string ToJson() => JsonSerializer.Serialize(this, Json);

    internal static BalancedRecord? FromJson(string json) => JsonSerializer.Deserialize<BalancedRecord>(json, Json);
}
