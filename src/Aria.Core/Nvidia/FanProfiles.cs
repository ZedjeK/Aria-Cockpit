using System.Text.Json;
using Aria.Core.Backup;

namespace Aria.Core.Nvidia;

/// <summary>Courbe de ventilation enregistrée sous un nom : on la recharge dans la carte Ventilateur, elle n'est jamais appliquée seule.</summary>
public sealed record SavedFanProfile(string Name, IReadOnlyList<FanCurvePoint> Points, DateTime At)
{
    public FanCurve Curve => new(Name, Points);

    /// <summary>« 45 °C 35 % · 60 °C 50 % · 70 °C 65 % ».</summary>
    public string Summary => string.Join(" · ", Points.Select(p => $"{p.TemperatureC} °C {p.Percent} %"));
}

/// <summary>Courbes de ventilation enregistrées (ventilateur-profils.json), valables pour toutes les cartes.</summary>
public static class FanProfiles
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(BackupStore.DataRoot, "ventilateur-profils.json");

    /// <summary>Courbes enregistrées, de la plus récente à la plus ancienne.</summary>
    public static IReadOnlyList<SavedFanProfile> All() => Load().OrderByDescending(p => p.At).ToList();

    /// <summary>Courbe de même nom (elle serait remplacée), ou null.</summary>
    public static SavedFanProfile? Find(string name) => Load().FirstOrDefault(p => SameName(p.Name, name));

    public static void Save(SavedFanProfile profile) => Write(Upsert(Load(), profile));

    public static void Delete(SavedFanProfile profile) => Write(Remove(Load(), profile.Name));

    /// <summary>Ajoute la courbe, en remplaçant celle de même nom.</summary>
    internal static List<SavedFanProfile> Upsert(IEnumerable<SavedFanProfile> profiles, SavedFanProfile profile) =>
        [.. Remove(profiles, profile.Name), profile];

    internal static List<SavedFanProfile> Remove(IEnumerable<SavedFanProfile> profiles, string name) =>
        profiles.Where(p => !SameName(p.Name, name)).ToList();

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);

    internal static List<SavedFanProfile> Load()
    {
        try
        {
            return File.Exists(FilePath) ? FromJson(File.ReadAllText(FilePath)) : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static void Write(List<SavedFanProfile> profiles)
    {
        Directory.CreateDirectory(BackupStore.DataRoot);
        File.WriteAllText(FilePath, ToJson(profiles));
    }

    internal static string ToJson(List<SavedFanProfile> profiles) => JsonSerializer.Serialize(profiles, Json);

    /// <summary>Courbes lisibles et cohérentes seulement : une courbe abîmée est ignorée, pas les autres.</summary>
    internal static List<SavedFanProfile> FromJson(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<SavedFanProfile>>(json, Json) ?? [])
                .Where(p => p is { Name: not null, Points: not null } && p.Name.Trim().Length > 0 && p.Curve.Check() is null)
                .ToList();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return [];
        }
    }
}
