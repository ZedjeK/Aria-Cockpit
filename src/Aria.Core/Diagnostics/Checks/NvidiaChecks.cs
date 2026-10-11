using Aria.Core.Models;
using Aria.Core.Nvidia;

namespace Aria.Core.Diagnostics.Checks;

/// <summary>NVAPI joignable ? Sans lui, les réglages NVIDIA ne sont pas proposés.</summary>
public sealed class NvapiCheck : IDiagnosticCheck
{
    public string Name => "accès au pilote NVIDIA";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var hw = ctx.Hardware;
        if (!hw.HasNvidiaGpu || hw.Nvidia is not { } nvidia) yield break;

        if (nvidia.Error is { } error)
            yield return new("nvidia.nvapi", "Réglages NVIDIA indisponibles", Severity.Info,
                $"Le pilote NVIDIA ne répond pas ({error}) : les réglages NVIDIA ne sont pas proposés.",
                "Installer ou réinstaller le pilote NVIDIA (page Mises à jour), puis relancer l'analyse.");
        else if (nvidia.DriverSettingsError is { } settingsError)
            yield return new("nvidia.nvapi", "Réglages du pilote NVIDIA illisibles", Severity.Info,
                $"Lecture du profil global impossible ({settingsError}) : seule la plage dynamique des écrans est proposée.",
                "Fermer le Panneau de configuration NVIDIA et l'application NVIDIA, puis relancer l'analyse.");
    }
}

/// <summary>G-SYNC possible mais éteint : Aria ne peut pas l'activer (absent de l'API officielle), elle guide.</summary>
public sealed class GsyncCheck : IDiagnosticCheck
{
    public string Name => "G-SYNC";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var hw = ctx.Hardware;
        if (hw.Nvidia is not { IsAvailable: true } nvidia) yield break;

        foreach (var display in hw.Displays.Where(NvidiaRules.IsNvidiaDriven))
        {
            if (nvidia.Display(display.DeviceName)?.Vrr is not { } vrr) continue;

            if (vrr.IsOn)
                yield return new("nvidia.gsync", $"G-SYNC actif : {display.MonitorName}", Severity.Ok,
                    $"Fréquence adaptative jusqu'à {display.MaxRefreshHz} Hz : ni déchirure ni saccade quand les images par seconde varient.");
            else if (vrr.Possible)
                yield return new("nvidia.gsync", $"G-SYNC disponible mais désactivé : {display.MonitorName}",
                    display.IsPrimary ? Severity.Warning : Severity.Info,
                    "L'écran et son branchement permettent G-SYNC (ou G-SYNC Compatible), mais il n'est pas activé.",
                    "L'activer, puis relancer l'analyse : Aria proposera alors la synchronisation et la limite d'images adaptées.",
                    [
                        "Application NVIDIA → Système → Écrans → G-SYNC : activer",
                        "Ou Panneau de configuration NVIDIA → Affichage → Configurer G-SYNC → Activer G-SYNC, G-SYNC Compatible",
                        "Choisir « Activer pour le mode plein écran »"
                    ]);
        }
    }
}

/// <summary>
/// Conseil DLSS selon la génération (sans rien modifier) : le remplacement de modèle se règle jeu par jeu
/// dans l'application NVIDIA, les guides déconseillant le réglage global.
/// </summary>
public sealed class DlssAdviceCheck : IDiagnosticCheck
{
    public string Name => "conseil DLSS";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        if (ctx.Hardware.NvidiaGpu is not { } gpu) yield break;

        switch (NvidiaRules.Generation(gpu.Name))
        {
            case NvidiaGeneration.Rtx40 or NvidiaGeneration.Rtx50:
                yield return new("nvidia.dlss", "DLSS : carte compatible avec les modèles les plus récents", Severity.Ok,
                    $"La {gpu.Name} accélère le FP8 : le modèle DLSS le plus récent améliore l'image pour un faible coût.",
                    "À régler jeu par jeu dans l'application NVIDIA, plutôt qu'en global.",
                    [
                        "Application NVIDIA → Graphismes → Paramètres du programme → choisir le jeu",
                        "Remplacement DLSS – Paramètres prédéfinis du modèle → le préréglage le plus récent",
                        "« Mouvement fluide » génère des images pour les jeux sans DLSS Frame Generation, avec un peu plus de latence : " +
                        "à activer aussi jeu par jeu, plutôt pour les jeux solo"
                    ]);
                break;

            case NvidiaGeneration.Rtx20 or NvidiaGeneration.Rtx30:
                yield return new("nvidia.dlss", "DLSS : garder le préréglage K", Severity.Ok,
                    $"La {gpu.Name} n'accélère pas le FP8 : les modèles DLSS 4.5 les plus récents peuvent y coûter des images par seconde.",
                    "Si vous remplacez le modèle DLSS d'un jeu, préférez le préréglage K.",
                    [
                        "Application NVIDIA → Graphismes → Paramètres du programme → choisir le jeu",
                        "Remplacement DLSS – Paramètres prédéfinis du modèle → Preset K (ou laisser le choix du jeu)"
                    ]);
                break;
        }
    }
}
