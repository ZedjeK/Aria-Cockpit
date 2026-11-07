namespace Aria.Core.Models;

/// <summary>
/// Catégories de réglages, dans l'ordre d'affichage (listes, menu des catégories, rapports, fiche PC, console).
/// Les sauvegardes enregistrent la catégorie par son nom : l'ordre peut changer sans les rendre illisibles.
/// </summary>
public enum SettingCategory { Windows, Battery, Nvidia, Privacy, Startup, Apps }

/// <summary>Nom affiché de chaque groupe de réglages (listes, rapports, fiche PC, console).</summary>
public static class SettingCategoryNames
{
    public static string Of(SettingCategory category) => category switch
    {
        SettingCategory.Battery => "Batterie",
        SettingCategory.Nvidia => "NVIDIA",
        SettingCategory.Privacy => "Confidentialité",
        SettingCategory.Startup => "Démarrage",
        SettingCategory.Apps => "Applications",
        _ => "Windows"
    };

    /// <summary>Ce que contient la catégorie, en une ou deux phrases, affiché en tête dans le menu des catégories.</summary>
    public static string Describe(SettingCategory category) => category switch
    {
        SettingCategory.Battery =>
            "Plafonne la charge pour ménager la batterie d'un portable souvent branché : une batterie qui reste pleine, " +
            "surtout au chaud, perd sa capacité bien plus vite.",
        SettingCategory.Nvidia =>
            "Profil global du pilote NVIDIA : latence, synchronisation, limite d'images, cache des shaders. Les profils " +
            "propres à chaque jeu restent prioritaires.",
        SettingCategory.Privacy =>
            "Limite les données envoyées à Microsoft et les publicités de Windows, et arrête des services inutiles. " +
            "Aucun gain de FPS : c'est pour la vie privée et la surface d'attaque.",
        SettingCategory.Startup =>
            "Programmes qui se lancent seuls à l'ouverture de session. En désactiver quelques-uns rend le démarrage plus " +
            "rapide et libère de la mémoire ; rien n'est désinstallé.",
        SettingCategory.Apps =>
            "Applications installées d'office avec Windows ou en promotion sur les PC neufs. Seules celles présentes sur " +
            "ce PC sont listées ; la désinstallation ne concerne que votre compte.",
        _ =>
            "Réglages du système pour le jeu et l'affichage : fréquence des écrans, alimentation, Mode Jeu, planification " +
            "GPU, souris."
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

    /// <summary>
    /// Vrai : non applicable, le réglage disparaît du plan (pas de ligne « non applicable »), tout en restant
    /// dans le catalogue pour la restauration (ex. une application déjà absente).
    /// </summary>
    bool HiddenWhenNotApplicable => false;
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
