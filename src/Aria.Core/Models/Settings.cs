namespace Aria.Core.Models;

public enum SettingCategory { Windows, Nvidia, Privacy, Startup }

/// <summary>Nom affiché de chaque groupe de réglages (listes, rapports, fiche PC, console).</summary>
public static class SettingCategoryNames
{
    public static string Of(SettingCategory category) => category switch
    {
        SettingCategory.Nvidia => "NVIDIA",
        SettingCategory.Privacy => "Confidentialité",
        SettingCategory.Startup => "Démarrage",
        _ => "Windows"
    };
}

/// <param name="Raw">Valeur technique (comparée et sauvegardée).</param>
/// <param name="Display">Valeur lisible affichée dans le rapport.</param>
public sealed record SettingValue(string Raw, string Display)
{
    public bool SameAs(SettingValue other) => string.Equals(Raw, other.Raw, StringComparison.OrdinalIgnoreCase);
}

/// <param name="Reason">Pourquoi cette cible (affiché dans la liste à cocher et le rapport).</param>
public sealed record SettingTarget(SettingValue Value, string Reason);

/// <summary>
/// Modèle commun à tous les réglages : lire, calculer la cible, appliquer.
/// La restauration consiste à appliquer l'ancienne valeur sauvegardée.
/// </summary>
public interface ISetting
{
    /// <summary>Identifiant stable, utilisé dans la sauvegarde (ex. "windows.gamemode").</summary>
    string Id { get; }

    string DisplayName { get; }

    SettingCategory Category { get; }

    bool RequiresReboot { get; }

    /// <summary>Coché d'office dans la liste ? (faux pour les préférences de confort)</summary>
    bool SelectedByDefault { get; }

    /// <summary>null si le réglage concerne cette machine, sinon la raison (affichée dans le tableau).</summary>
    string? WhyNotApplicable(HardwareProfile hardware);

    SettingValue ReadCurrent(HardwareProfile hardware);

    SettingTarget ComputeTarget(HardwareProfile hardware);

    /// <summary>La valeur actuelle produit-elle déjà l'effet de la cible ?</summary>
    bool IsAtTarget(SettingValue current, SettingValue target);

    void Apply(HardwareProfile hardware, SettingValue value);
}

public enum SettingStatus
{
    /// <summary>Déjà à la bonne valeur.</summary>
    Unchanged,
    /// <summary>Modifié et vérifié.</summary>
    Changed,
    /// <summary>Mode simulation : aurait été proposé.</summary>
    Simulated,
    Failed,
    NotApplicable,
    /// <summary>Décoché par l'utilisateur.</summary>
    Declined
}

public sealed record SettingResult(
    string Id,
    string DisplayName,
    SettingCategory Category,
    SettingValue? Current,
    SettingValue? Target,
    string Reason,
    SettingStatus Status,
    bool RequiresReboot,
    string? Error = null);
