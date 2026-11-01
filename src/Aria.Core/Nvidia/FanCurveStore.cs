using System.Text.Json;
using Aria.Core.Backup;

namespace Aria.Core.Nvidia;

/// <summary>Courbe choisie (préréglage ou personnalisée), et si Aria est en train de la faire tourner.</summary>
/// <param name="Active">
/// Vrai tant qu'Aria pilote les ventilateurs. Encore vrai au lancement suivant : Aria s'est arrêté sans rendre la main
/// au pilote (plantage, fin de tâche), et les ventilateurs sont peut-être restés à leur dernière vitesse.
/// </param>
public sealed record FanCurveChoice(string Name, IReadOnlyList<FanCurvePoint> Points, bool Active)
{
    public FanCurve Curve => new(Name, Points);
}

/// <summary>Courbe de ventilation enregistrée (ventilateur.json, dans le dossier des données d'Aria).</summary>
public static class FanCurveStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath => Path.Combine(BackupStore.DataRoot, "ventilateur.json");

    /// <summary>Courbe enregistrée, ou null (aucune, ou fichier illisible ou incohérent).</summary>
    public static FanCurveChoice? Load()
    {
        try
        {
            return File.Exists(FilePath) ? FromJson(File.ReadAllText(FilePath)) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(FanCurveChoice choice)
    {
        Directory.CreateDirectory(BackupStore.DataRoot);
        File.WriteAllText(FilePath, ToJson(choice));
    }

    /// <summary>Note que la courbe tourne (ou plus) ; sans courbe enregistrée, rien à noter.</summary>
    public static void SetActive(bool active)
    {
        if (Load() is { } choice && choice.Active != active) Save(choice with { Active = active });
    }

    internal static string ToJson(FanCurveChoice choice) => JsonSerializer.Serialize(choice, Json);

    internal static FanCurveChoice? FromJson(string json)
    {
        try
        {
            var choice = JsonSerializer.Deserialize<FanCurveChoice>(json, Json);
            return choice is { Name: not null, Points: not null } && choice.Curve.Check() is null ? choice : null;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
