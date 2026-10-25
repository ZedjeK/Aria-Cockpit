namespace Aria.Gui.Services;

/// <summary>
/// Une seule mesure en jeu à la fois : l'analyse de Mon PC et l'assistant du Réglage GPU se désactivent mutuellement,
/// pour qu'une analyse ne mesure jamais un palier de test sans le savoir.
/// </summary>
public static class MeasurementGate
{
    private static bool _analysis, _assistant;

    /// <summary>Levé (sur le fil de l'interface) quand l'une des deux mesures démarre ou s'arrête.</summary>
    public static event Action? Changed;

    public static bool AnalysisRunning
    {
        get => _analysis;
        set { if (_analysis != value) { _analysis = value; Changed?.Invoke(); } }
    }

    public static bool AssistantRunning
    {
        get => _assistant;
        set { if (_assistant != value) { _assistant = value; Changed?.Invoke(); } }
    }
}
