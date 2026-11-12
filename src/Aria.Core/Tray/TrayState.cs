using Aria.Core.Nvidia;

namespace Aria.Core.Tray;

/// <summary>Où en est le réglage GPU, d'après ce que la carte répond (et non d'après ce qu'Aria croit avoir posé).</summary>
public enum GpuProfileStatus
{
    /// <summary>Pas de carte NVIDIA réglable, ou carte illisible.</summary>
    Unknown,
    /// <summary>Valeurs d'usine, aucun réglage d'Aria enregistré.</summary>
    Factory,
    /// <summary>Le réglage enregistré par Aria est bien en place sur la carte.</summary>
    Applied,
    /// <summary>Un réglage d'Aria est enregistré, mais la carte ne l'a plus (pilote rechargé, mis à jour, planté).</summary>
    Lost,
    /// <summary>La carte n'est pas aux valeurs d'usine, sans réglage d'Aria : un autre outil l'a réglée.</summary>
    Foreign
}

/// <summary>Voir <see cref="TrayState.OnClose"/>.</summary>
public enum CloseAction
{
    /// <summary>La fenêtre se range dans l'icône, Aria continue.</summary>
    Hide,
    /// <summary>Aria s'arrête.</summary>
    Quit,
    /// <summary>Une opération est en cours : Aria ne s'arrête qu'après confirmation.</summary>
    AskThenQuit
}

/// <summary>Dessin de l'icône de la zone de notification.</summary>
public enum TrayIconKind
{
    /// <summary>Rien d'actif : icône délavée.</summary>
    Idle,
    /// <summary>Réglage GPU en place, ou courbe de ventilation en cours.</summary>
    Active,
    /// <summary>Le réglage enregistré n'est plus sur la carte : pastille jaune.</summary>
    Alert
}

/// <summary>Ce que le menu de l'icône propose de lancer.</summary>
public static class TrayChoices
{
    /// <summary>
    /// Courbes de ventilation du menu : les préréglages, puis « Mes courbes », puis la dernière courbe personnalisée
    /// si elle n'a pas été enregistrée sous un nom. Une seule entrée par nom.
    /// </summary>
    public static IReadOnlyList<FanCurve> FanCurves(IEnumerable<SavedFanProfile> saved, FanCurveChoice? last)
    {
        var curves = new List<FanCurve>(FanCurve.Presets);
        foreach (var curve in saved.Select(p => p.Curve).Concat(last is null ? [] : [last.Curve]))
            if (!curves.Any(c => SameName(c.Name, curve.Name)))
                curves.Add(curve);
        return curves;
    }

    public static bool SameName(string? a, string? b) =>
        a is not null && b is not null && string.Equals(a.Trim(), b.Trim(), StringComparison.CurrentCultureIgnoreCase);
}

/// <summary>
/// Ce que l'icône de la zone de notification, son infobulle, son menu et la règle de fermeture ont besoin de savoir.
/// </summary>
/// <param name="Profile">État du réglage GPU, relu sur la carte.</param>
/// <param name="ProfileText">Réglage lu sur la carte (« 320 W · plafond 2685 MHz · … ») ; null si illisible.</param>
/// <param name="FanCurve">Nom de la courbe de ventilation qu'Aria fait tourner ; null si le pilote a la main.</param>
/// <param name="TemperatureC">Température du GPU ; null si illisible.</param>
/// <param name="FanPercent">Vitesse des ventilateurs (le plus rapide) ; null si illisible.</param>
/// <param name="Work">Opérations en cours (« nettoyage », « installation des mises à jour »…).</param>
public sealed record TrayState(
    GpuProfileStatus Profile,
    string? ProfileText,
    string? FanCurve,
    uint? TemperatureC,
    uint? FanPercent,
    IReadOnlyList<string> Work)
{
    /// <summary>Longueur maximale d'une infobulle de la zone de notification.</summary>
    public const int MaxTooltip = 127;

    public static TrayState Empty { get; } = new(GpuProfileStatus.Unknown, null, null, null, null, []);

    /// <summary>
    /// Compare la carte au réglage enregistré. Le plafond de fréquence n'est pas relisible sur la carte : seuls la
    /// limite de puissance et les deux décalages servent de preuve.
    /// </summary>
    /// <param name="card">Réglage lu sur la carte.</param>
    /// <param name="factory">Valeurs d'usine de cette carte.</param>
    /// <param name="saved">Réglage enregistré par Aria pour cette carte ; null s'il n'y en a pas.</param>
    public static GpuProfileStatus ProfileStatus(GpuTuningState card, GpuTuningState factory, GpuTuningState? saved)
    {
        // Un réglage enregistré identique à l'usine (gardé pour sa seule tâche de démarrage) ne compte pas
        if (saved is not null && SameOnCard(saved, factory) && saved.MaxClockMHz == 0) saved = null;
        if (saved is not null) return SameOnCard(card, saved) ? GpuProfileStatus.Applied : GpuProfileStatus.Lost;
        return SameOnCard(card, factory) ? GpuProfileStatus.Factory : GpuProfileStatus.Foreign;
    }

    private static bool SameOnCard(GpuTuningState a, GpuTuningState b) =>
        a.PowerLimitMilliwatts == b.PowerLimitMilliwatts && a.CoreOffsetMHz == b.CoreOffsetMHz && a.MemoryOffsetMHz == b.MemoryOffsetMHz;

    /// <summary>
    /// Aria a quelque chose d'actif sur la machine : fermer la fenêtre la range dans la zone de notification au lieu
    /// de quitter. Un réglage perdu compte aussi : c'est justement ce que l'icône doit signaler.
    /// </summary>
    public bool KeepsRunning => Profile is GpuProfileStatus.Applied or GpuProfileStatus.Lost || FanCurve is not null || Work.Count > 0;

    /// <summary>
    /// Ce que fait la croix de la fenêtre, selon le choix fait dans les Paramètres. Une opération en cours n'est jamais
    /// coupée sans le dire : « toujours quitter » demande alors confirmation.
    /// </summary>
    public CloseAction OnClose(Settings.CloseBehavior behavior) => behavior switch
    {
        Settings.CloseBehavior.AlwaysTray => CloseAction.Hide,
        Settings.CloseBehavior.AlwaysQuit => Work.Count > 0 ? CloseAction.AskThenQuit : CloseAction.Quit,
        _ => KeepsRunning ? CloseAction.Hide : CloseAction.Quit
    };

    public TrayIconKind Icon => Profile == GpuProfileStatus.Lost ? TrayIconKind.Alert
        : Profile == GpuProfileStatus.Applied || FanCurve is not null ? TrayIconKind.Active
        : TrayIconKind.Idle;

    /// <summary>Pastille « ventilateurs pilotés par Aria ».</summary>
    public bool FanBadge => FanCurve is not null;

    /// <summary>« profil GPU appliqué », « réglage d'usine »… ; null si la carte n'est pas lisible.</summary>
    public string? ProfileLine => Profile switch
    {
        GpuProfileStatus.Applied => "profil GPU appliqué",
        GpuProfileStatus.Lost => "ALERTE : profil GPU perdu",
        GpuProfileStatus.Factory => "GPU au réglage d'usine",
        GpuProfileStatus.Foreign => "GPU réglé par un autre outil",
        _ => null
    };

    /// <summary>« courbe « Ma config » · 52 °C · ventilateurs 60 % » ; null si rien n'est lisible.</summary>
    public string? FanLine
    {
        get
        {
            var parts = new List<string>();
            if (FanCurve is not null) parts.Add($"courbe « {FanCurve} »");
            if (TemperatureC is { } t) parts.Add($"{t} °C");
            if (FanPercent is { } p) parts.Add($"ventilateurs {p} %");
            return parts.Count == 0 ? null : string.Join(" · ", parts);
        }
    }

    /// <summary>« installation des mises à jour en cours » ; null si rien ne tourne.</summary>
    public string? WorkLine => Work.Count == 0 ? null : $"{string.Join(", ", Work)} en cours";

    /// <summary>Résumé en une ligne, pour l'infobulle : l'essentiel d'abord, coupé à la longueur admise.</summary>
    public string Summary()
    {
        var parts = new[] { ProfileLine, FanLine, WorkLine }.Where(p => p is not null).ToList();
        var text = parts.Count == 0 ? "rien d'actif" : string.Join(" · ", parts);
        return Fit("Aria Cockpit · " + text);
    }

    /// <summary>Pourquoi Aria reste ouverte quand on ferme la fenêtre ; null si elle peut quitter.</summary>
    public string? KeepReason()
    {
        if (!KeepsRunning) return null;
        var reasons = new List<string>();
        if (Profile == GpuProfileStatus.Applied) reasons.Add("ton profil GPU est appliqué");
        if (Profile == GpuProfileStatus.Lost) reasons.Add("ton profil GPU n'est plus sur la carte");
        if (FanCurve is not null) reasons.Add($"la courbe « {FanCurve} » pilote les ventilateurs");
        if (WorkLine is { } work) reasons.Add(work);
        var text = string.Join(", ", reasons);
        return char.ToUpperInvariant(text[0]) + text[1..] + ".";
    }

    /// <summary>
    /// Ce qui change le dessin de l'icône ou la structure du menu. La température et la vitesse n'en font pas
    /// partie : elles bougent à chaque relevé et ne touchent que les textes.
    /// </summary>
    public string Signature => $"{Profile}|{FanCurve}|{string.Join(",", Work)}";

    internal static string Fit(string text) => text.Length <= MaxTooltip ? text : text[..(MaxTooltip - 1)] + "…";
}
