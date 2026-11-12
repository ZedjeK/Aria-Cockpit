using System.Text.Json;
using System.Text.Json.Serialization;
using Aria.Core.Backup;

namespace Aria.Core.Settings;

/// <summary>Ce que fait la croix de la fenêtre.</summary>
public enum CloseBehavior
{
    /// <summary>Range Aria dans son icône si elle a quelque chose d'actif, l'arrête sinon.</summary>
    Auto,
    /// <summary>Range toujours Aria dans son icône.</summary>
    AlwaysTray,
    /// <summary>Arrête toujours Aria (la courbe de ventilation rend alors la main au pilote).</summary>
    AlwaysQuit
}

/// <summary>
/// Paramètres de l'application elle-même (page Paramètres), par opposition aux réglages du PC (page Optimisation).
/// Les valeurs par défaut sont le comportement d'avant cette page : rien ne change tant qu'on n'y touche pas.
/// </summary>
/// <param name="Close">Ce que fait la croix de la fenêtre.</param>
/// <param name="NotifyOnHide">Notification Windows quand la fenêtre se range dans l'icône.</param>
/// <param name="ReportsMonths">Rapports et fiches PC gardés ce nombre de mois.</param>
/// <param name="BackupsMonths">Sauvegardes gardées ce nombre de mois…</param>
/// <param name="KeepBackups">… sauf les plus récentes, toujours gardées.</param>
public sealed record AppPreferences(
    CloseBehavior Close = CloseBehavior.Auto,
    bool NotifyOnHide = true,
    int ReportsMonths = 6,
    int BackupsMonths = 12,
    int KeepBackups = 10)
{
    public const int MinMonths = 1, MaxMonths = 120, MinKeep = 1, MaxKeep = 100;

    public static AppPreferences Default { get; } = new();

    public RetentionSettings Retention => new(ReportsMonths, BackupsMonths, KeepBackups);

    /// <summary>Valeurs ramenées dans les bornes : un fichier modifié à la main ne peut pas demander « 0 mois ».</summary>
    public AppPreferences Sanitized() => this with
    {
        Close = Enum.IsDefined(Close) ? Close : CloseBehavior.Auto,
        ReportsMonths = Math.Clamp(ReportsMonths, MinMonths, MaxMonths),
        BackupsMonths = Math.Clamp(BackupsMonths, MinMonths, MaxMonths),
        KeepBackups = Math.Clamp(KeepBackups, MinKeep, MaxKeep)
    };
}

/// <summary>Paramètres enregistrés dans parametres.json, dans le dossier des données d'Aria (modifiable par les seuls administrateurs).</summary>
public static class AppPreferencesStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static string FilePath => Path.Combine(BackupStore.DataRoot, "parametres.json");

    /// <summary>Paramètres enregistrés ; ceux par défaut si le fichier est absent, illisible ou incohérent.</summary>
    public static AppPreferences Load()
    {
        try
        {
            return File.Exists(FilePath) ? FromJson(File.ReadAllText(FilePath)) : AppPreferences.Default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return AppPreferences.Default;
        }
    }

    /// <summary>Lève <see cref="IOException"/> ou <see cref="UnauthorizedAccessException"/> si l'écriture échoue.</summary>
    public static void Save(AppPreferences preferences)
    {
        Directory.CreateDirectory(BackupStore.DataRoot);
        File.WriteAllText(FilePath, ToJson(preferences.Sanitized()));
    }

    internal static string ToJson(AppPreferences preferences) => JsonSerializer.Serialize(preferences, Json);

    internal static AppPreferences FromJson(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<AppPreferences>(json, Json) ?? AppPreferences.Default).Sanitized();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return AppPreferences.Default;
        }
    }
}
