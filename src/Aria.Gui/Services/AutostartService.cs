using Aria.Core.Tray;

namespace Aria.Gui.Services;

/// <summary>
/// « Démarrer avec Windows », vu de l'interface : l'état de la tâche planifiée, lu une fois (interroger le
/// planificateur lance un programme), partagé par la page Paramètres et le menu de l'icône, qui restent ainsi d'accord.
/// </summary>
public static class AutostartService
{
    /// <summary>L'option est activée ; null tant que le planificateur n'a pas été interrogé.</summary>
    public static bool? Enabled { get; private set; }

    /// <summary>L'état vient d'être lu ou changé (levé sur un fil quelconque).</summary>
    public static event Action? Changed;

    /// <summary>Interroge le planificateur, hors du fil de l'interface.</summary>
    public static async Task<bool> RefreshAsync()
    {
        var enabled = await Task.Run(TrayStartup.Enabled);
        Enabled = enabled;
        Changed?.Invoke();
        return enabled;
    }

    /// <summary>
    /// Crée ou supprime la tâche. Lève <see cref="InvalidOperationException"/> si la copie de l'exe ou la tâche est
    /// refusée ; l'état n'est alors pas changé.
    /// </summary>
    public static void Set(bool enabled)
    {
        if (enabled) TrayStartup.Enable(Environment.ProcessPath ?? "Aria-Cockpit.exe");
        else TrayStartup.Disable();
        Enabled = enabled;
        Changed?.Invoke();
    }
}
