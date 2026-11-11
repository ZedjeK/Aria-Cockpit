using Aria.Core.Nvidia;
using Aria.Core.Settings;

namespace Aria.Core.Tray;

/// <summary>
/// « Démarrer avec Windows » : la tâche planifiée « Aria Cockpit\Zone de notification » lance Aria Cockpit à
/// l'ouverture de session, avec les droits administrateur (sans fenêtre UAC), directement rangée dans son icône.
/// Comme les autres tâches d'Aria, elle lance la copie protégée de l'exe (<see cref="LogonExe"/>). La tâche existe :
/// l'option est activée ; il n'y a pas d'autre réglage à garder d'accord avec elle.
/// </summary>
public static class TrayStartup
{
    public const string TaskName = @"Aria Cockpit\Zone de notification";

    /// <summary>Lancement sans fenêtre : l'icône seule, jusqu'à ce qu'on l'ouvre.</summary>
    public const string Argument = "--tray";

    /// <summary>L'option est activée (interroge le planificateur : à ne pas répéter en boucle).</summary>
    public static bool Enabled() => StartupTask.TaskExists(TaskName);

    /// <summary>Crée la tâche. Lève <see cref="InvalidOperationException"/> si la copie de l'exe ou la tâche est refusée.</summary>
    public static void Enable(string exePath) => LogonExe.CreateTask(TaskName, Argument, exePath, longRunning: true);

    public static void Disable()
    {
        if (Enabled()) StartupTask.Run($"/Delete /TN \"{TaskName}\" /F");
        LogonExe.RemoveIfUnused();
    }

    /// <summary>Courbe à relancer au lancement d'Aria : celle qui tournait quand Aria s'est arrêtée, si l'option est activée.</summary>
    public static FanCurve? CurveToResume(FanCurveChoice? saved, bool enabled) =>
        enabled && saved is { Resume: true } ? saved.Curve : null;

    /// <summary>
    /// Ce que fait un lancement quand Aria tourne déjà dans la session, selon les deux versions. Une version plus
    /// récente prend la place de l'ancienne (sinon une mise à jour rouvrirait indéfiniment l'ancienne fenêtre, Aria
    /// restant dans la zone de notification) ; une plus ancienne laisse la place ; la même ramène la fenêtre.
    /// </summary>
    public static SecondLaunch OnSecondLaunch(string? running, string? launched) =>
        !Version.TryParse(running, out var old) || !Version.TryParse(launched, out var mine) ? SecondLaunch.Activate
        : mine > old ? SecondLaunch.TakeOver
        : mine < old ? SecondLaunch.Refuse
        : SecondLaunch.Activate;
}

/// <summary>Voir <see cref="TrayStartup.OnSecondLaunch"/>.</summary>
public enum SecondLaunch
{
    /// <summary>Même version (ou version illisible) : la fenêtre déjà ouverte revient au premier plan.</summary>
    Activate,
    /// <summary>Le lancement est plus récent : l'exemplaire en place s'arrête et lui laisse la place.</summary>
    TakeOver,
    /// <summary>Le lancement est plus ancien : il s'arrête, en le disant.</summary>
    Refuse
}
