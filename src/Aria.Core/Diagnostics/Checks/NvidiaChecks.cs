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

        // La table des modèles distingue ce que le nom seul ne dit pas (GTX 16 = Turing sans DLSS, RTX 2050 = Ampere…)
        var model = GpuCatalog.Find(gpu.Name);
        if (model is { Dlss: DlssSupport.None })
        {
            yield return new("nvidia.dlss", "DLSS indisponible sur cette carte", Severity.Info,
                $"La {gpu.Name} ({GpuCatalog.Describe(model.Architecture)}) n'a pas les cœurs Tensor du DLSS.",
                "Dans les jeux, préférez FSR ou XeSS : ces techniques de mise à l'échelle fonctionnent sur toutes les cartes.");
            yield break;
        }
        var frameGeneration = model?.Dlss switch
        {
            DlssSupport.MultiFrameGeneration => " Elle gère aussi la génération multi-images (plusieurs images générées pour une calculée).",
            DlssSupport.FrameGeneration => " Elle gère aussi la génération d'images (Frame Generation).",
            _ => ""
        };

        switch (NvidiaRules.Generation(gpu.Name))
        {
            case NvidiaGeneration.Rtx40 or NvidiaGeneration.Rtx50:
                yield return new("nvidia.dlss", "DLSS : carte compatible avec les modèles les plus récents", Severity.Ok,
                    $"La {gpu.Name} accélère le FP8 : le modèle DLSS le plus récent améliore l'image pour un faible coût.{frameGeneration}",
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

/// <summary>
/// Limite de puissance de la carte NVIDIA comparée à sa valeur d'usine (lue par NVML) : une carte bridée
/// par un autre outil (NVIDIA App, MSI Afterburner, profil du fabricant) perd des performances sans qu'on le sache.
/// </summary>
public sealed class GpuPowerLimitCheck : IDiagnosticCheck
{
    public string Name => "limite de puissance GPU";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        if (ctx.Hardware.NvidiaGpu is null || GpuProfile.Tuner is not { } tuner || !tuner.Limits.SupportsPowerLimit) yield break;
        GpuTuningState state;
        try { state = GpuProfile.ReadCurrent(tuner); }
        catch (GpuTuningException) { yield break; }
        if (Describe(state.PowerLimitMilliwatts, tuner.Limits.DefaultPowerMilliwatts, GpuProfile.OriginFor(tuner) is not null) is { } finding)
            yield return finding;
    }

    /// <summary>Constat selon l'écart à la valeur d'usine (null si elle est respectée, à 2 % près). Séparé pour être testable.</summary>
    internal static DiagnosticFinding? Describe(uint currentMw, uint defaultMw, bool setByAria)
    {
        if (defaultMw == 0) return null;
        var ratio = (double)currentMw / defaultMw;
        if (Math.Abs(ratio - 1) < 0.02) return null;
        var watts = $"{currentMw / 1000} W au lieu de {defaultMw / 1000} W ({ratio * 100:0} %)";
        var who = setByAria
            ? "Elle a été réglée par Aria (onglet Réglage GPU)."
            : "Elle a été réglée par un autre outil : NVIDIA App (« Limites de performance »), MSI Afterburner ou un profil du fabricant de la carte.";
        return ratio < 1
            ? new("nvidia.power", "Limite de puissance de la carte réduite", Severity.Info,
                $"{watts}. {who} La carte reste plus fraîche et plus silencieuse, au prix de quelques pour cent de performance.",
                setByAria ? null : "Si ce n'est pas voulu : remettez par défaut les « Limites de performance » de NVIDIA App, ou réglez la limite dans l'onglet Réglage GPU.")
            : new("nvidia.power", "Limite de puissance de la carte relevée", Severity.Info,
                $"{watts}. {who}",
                "Vérifiez que votre alimentation a la puissance recommandée et que le câble d'alimentation de la carte (12V-2x6 / 12VHPWR) est enfoncé à fond.");
    }
}
