using System.Globalization;
using System.Text.Json;
using Aria.Core.Backup;

namespace Aria.Core.Nvidia;

/// <summary>Réglage GPU enregistré sous un nom, pour une carte : on le recharge dans les curseurs, il n'est jamais appliqué seul.</summary>
/// <param name="State">Réglage (format <see cref="GpuTuningState.ToRaw"/>, sans le démarrage : la case reste celle des curseurs).</param>
/// <param name="Note">D'où il vient : « Assistant Équilibre · 104,8 FPS au banc (2160p) · validé 15 min », ou « Réglage manuel ».</param>
public sealed record SavedGpuProfile(string Name, string State, DateTime At, string GpuUuid, string GpuName, string DriverVersion, string Note = "")
{
    public GpuTuningState? Tuning => GpuTuningState.TryParse(State, out var s) ? s : null;

    /// <summary>Le profil vaut pour cette carte (même identifiant NVML ; à défaut, même nom).</summary>
    public bool Matches(GpuIdentity gpu) =>
        GpuUuid.Length > 0 && gpu.Uuid.Length > 0
            ? string.Equals(GpuUuid, gpu.Uuid, StringComparison.OrdinalIgnoreCase)
            : string.Equals(GpuName, gpu.Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>Le pilote a changé depuis l'enregistrement : la courbe fréquence/tension peut avoir bougé.</summary>
    public bool DriverChanged(GpuIdentity gpu) =>
        DriverVersion.Length > 0 && gpu.DriverVersion.Length > 0 && !string.Equals(DriverVersion, gpu.DriverVersion, StringComparison.Ordinal);

    /// <summary>« 30/10/2026 · pilote 621.01 · Assistant Équilibre… ».</summary>
    public string Details =>
        string.Join(" · ", new[] { At.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture), DriverVersion.Length > 0 ? $"pilote {DriverVersion}" : "", Note }
            .Where(p => p.Length > 0));
}

/// <summary>Profils GPU enregistrés (profils.json), toutes cartes confondues ; la page n'affiche que ceux de la carte en place.</summary>
public static class GpuProfiles
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(BackupStore.DataRoot, "profils.json");

    /// <summary>Profils de cette carte, du plus récent au plus ancien.</summary>
    public static IReadOnlyList<SavedGpuProfile> For(GpuIdentity gpu) =>
        Load().Where(p => p.Matches(gpu) && p.Tuning is not null).OrderByDescending(p => p.At).ToList();

    /// <summary>Profil de même nom pour cette carte (il serait remplacé), ou null.</summary>
    public static SavedGpuProfile? Find(GpuIdentity gpu, string name) => Load().FirstOrDefault(p => p.Matches(gpu) && SameName(p.Name, name));

    public static void Save(SavedGpuProfile profile, GpuIdentity gpu) => Write(Upsert(Load(), profile, gpu));

    public static void Delete(SavedGpuProfile profile, GpuIdentity gpu) => Write(Remove(Load(), profile.Name, gpu));

    /// <summary>Ajoute le profil, en remplaçant celui de même nom pour la même carte.</summary>
    internal static List<SavedGpuProfile> Upsert(IEnumerable<SavedGpuProfile> profiles, SavedGpuProfile profile, GpuIdentity gpu) =>
        [.. Remove(profiles, profile.Name, gpu), profile];

    internal static List<SavedGpuProfile> Remove(IEnumerable<SavedGpuProfile> profiles, string name, GpuIdentity gpu) =>
        profiles.Where(p => !(p.Matches(gpu) && SameName(p.Name, name))).ToList();

    /// <summary>Nom proposé à la fin de l'assistant : « Équilibre du 30/10 ».</summary>
    public static string DefaultName(string mode, DateTime at) => $"{mode} du {at.ToString("dd/MM", CultureInfo.InvariantCulture)}";

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);

    internal static List<SavedGpuProfile> Load()
    {
        try
        {
            return File.Exists(FilePath) ? FromJson(File.ReadAllText(FilePath)) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return [];
        }
    }

    private static void Write(List<SavedGpuProfile> profiles)
    {
        Directory.CreateDirectory(BackupStore.DataRoot);
        File.WriteAllText(FilePath, ToJson(profiles));
    }

    internal static string ToJson(List<SavedGpuProfile> profiles) => JsonSerializer.Serialize(profiles, Json);

    internal static List<SavedGpuProfile> FromJson(string json) => JsonSerializer.Deserialize<List<SavedGpuProfile>>(json, Json) ?? [];
}
