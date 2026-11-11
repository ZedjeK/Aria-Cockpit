namespace Aria.Gui.Services;

/// <summary>
/// Opérations longues en cours (application de réglages, restauration, nettoyage, installation de mises à jour,
/// mesures). Tant qu'il y en a une, fermer la fenêtre range Aria dans la zone de notification au lieu de l'arrêter
/// au milieu.
/// </summary>
public static class BackgroundWork
{
    private static readonly object Gate = new();
    private static readonly List<string> Labels = [];

    /// <summary>Une opération a démarré ou s'est terminée (levé sur le fil qui l'a signalée).</summary>
    public static event Action? Changed;

    /// <summary>Signale qu'une opération démarre ou se termine. Le libellé complète « … en cours ».</summary>
    public static void Set(string label, bool running)
    {
        lock (Gate)
        {
            if (running == Labels.Contains(label)) return;
            if (running) Labels.Add(label);
            else Labels.Remove(label);
        }
        Changed?.Invoke();
    }

    /// <summary>Opérations en cours, dans l'ordre où elles ont démarré, mesures comprises.</summary>
    public static IReadOnlyList<string> Running
    {
        get
        {
            List<string> all;
            lock (Gate) all = [.. Labels];
            if (MeasurementGate.AssistantRunning) all.Add("assistant de réglage GPU");
            if (MeasurementGate.AnalysisRunning) all.Add("analyse de performance");
            return all;
        }
    }
}
