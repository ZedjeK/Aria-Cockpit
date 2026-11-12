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

/// <summary>Vitesse du fond animé. Dans le fichier : « lente », « normale », « rapide ».</summary>
public enum BackgroundSpeed
{
    Slow,
    Normal,
    Fast
}

/// <summary>Écrit la vitesse en français, comme le veut le contrat des applis Aria ; une valeur inconnue donne « normale ».</summary>
public sealed class BackgroundSpeedConverter : JsonConverter<BackgroundSpeed>
{
    public override BackgroundSpeed Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            reader.Skip();
            return BackgroundSpeed.Normal;
        }
        return reader.GetString()?.Trim().ToLowerInvariant() switch
        {
            "lente" => BackgroundSpeed.Slow,
            "rapide" => BackgroundSpeed.Fast,
            _ => BackgroundSpeed.Normal
        };
    }

    public override void Write(Utf8JsonWriter writer, BackgroundSpeed value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            BackgroundSpeed.Slow => "lente",
            BackgroundSpeed.Fast => "rapide",
            _ => "normale"
        });
}

/// <summary>
/// Fond animé de la fenêtre (le balayage et ses points). Dans le fichier : fond.actif, fond.vitesse, fond.minimum,
/// fond.maximum.
/// </summary>
/// <param name="Active">Fond animé affiché.</param>
/// <param name="Speed">Vitesse du balayage.</param>
/// <param name="Minimum">Nombre de points tirés au hasard à chaque passage : au moins…</param>
/// <param name="Maximum">… et au plus.</param>
public sealed record BackgroundSettings(
    [property: JsonPropertyName("actif")] bool Active = true,
    [property: JsonPropertyName("vitesse"), JsonConverter(typeof(BackgroundSpeedConverter))] BackgroundSpeed Speed = BackgroundSpeed.Normal,
    [property: JsonPropertyName("minimum")] int Minimum = 2,
    [property: JsonPropertyName("maximum")] int Maximum = 5)
{
    public const int MinPoints = 1, MaxPoints = 12;

    public static BackgroundSettings Default { get; } = new();

    /// <summary>Durée d'un passage du balayage.</summary>
    [JsonIgnore]
    public TimeSpan PassDuration => TimeSpan.FromSeconds(Speed switch
    {
        BackgroundSpeed.Slow => 14,
        BackgroundSpeed.Fast => 3.5,
        _ => 7
    });

    /// <summary>Bornes ramenées entre 1 et 12, et remises dans l'ordre si le fichier les a inversées.</summary>
    public BackgroundSettings Sanitized()
    {
        var low = Math.Clamp(Minimum, MinPoints, MaxPoints);
        var high = Math.Clamp(Maximum, MinPoints, MaxPoints);
        return this with
        {
            Speed = Enum.IsDefined(Speed) ? Speed : BackgroundSpeed.Normal,
            Minimum = Math.Min(low, high),
            Maximum = Math.Max(low, high)
        };
    }
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

    /// <summary>Fond animé ; absent du fichier, c'est celui par défaut.</summary>
    [JsonPropertyName("fond")]
    public BackgroundSettings Background { get; init; } = BackgroundSettings.Default;

    public RetentionSettings Retention => new(ReportsMonths, BackupsMonths, KeepBackups);

    /// <summary>Valeurs ramenées dans les bornes : un fichier modifié à la main ne peut pas demander « 0 mois ».</summary>
    public AppPreferences Sanitized() => this with
    {
        Close = Enum.IsDefined(Close) ? Close : CloseBehavior.Auto,
        ReportsMonths = Math.Clamp(ReportsMonths, MinMonths, MaxMonths),
        BackupsMonths = Math.Clamp(BackupsMonths, MinMonths, MaxMonths),
        KeepBackups = Math.Clamp(KeepBackups, MinKeep, MaxKeep),
        // « fond » à null dans un fichier modifié à la main : le fond par défaut
        Background = (Background ?? BackgroundSettings.Default).Sanitized()
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
