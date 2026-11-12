using Aria.Core.Settings;

namespace Aria.Gui.Services;

/// <summary>
/// Réglages du fond animé en cours. La page Paramètres les change, le fond de la fenêtre les suit tout de suite,
/// sans relire le fichier à chaque passage.
/// </summary>
public static class BackdropService
{
    public static BackgroundSettings Current { get; private set; } = BackgroundSettings.Default;

    /// <summary>Les réglages viennent de changer (levé sur le fil qui appelle <see cref="Set"/>).</summary>
    public static event Action? Changed;

    public static void Set(BackgroundSettings settings)
    {
        var sanitized = settings.Sanitized();
        if (sanitized == Current) return;
        Current = sanitized;
        Changed?.Invoke();
    }
}
