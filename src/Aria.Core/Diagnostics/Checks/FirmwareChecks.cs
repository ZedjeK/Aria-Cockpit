using Aria.Core.Models;

namespace Aria.Core.Diagnostics.Checks;

/// <summary>Mode de démarrage UEFI ou ancien mode Legacy (CSM).</summary>
public sealed class BootModeCheck : IDiagnosticCheck
{
    public string Name => "mode de démarrage";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        switch (ctx.Hardware.Firmware.BootMode)
        {
            case BootMode.Uefi:
                yield return new("firmware.boot", "Démarrage en mode UEFI", Severity.Ok, "Mode moderne, requis pour le Resizable BAR et le Secure Boot.");
                break;
            case BootMode.Legacy:
                yield return new("firmware.boot", "Windows démarre en ancien mode (Legacy/CSM)", Severity.Warning,
                    "Ce mode empêche le Resizable BAR et le Secure Boot (exigé par certains anti-triches).",
                    "Ne pas désactiver le CSM dans le BIOS tant que le disque n'est pas converti : Windows ne démarrerait plus. " +
                    "Conversion possible avec l'outil officiel Microsoft MBR2GPT, après sauvegarde.");
                break;
        }
    }
}

/// <summary>Secure Boot, TPM et intégrité de la mémoire : surtout utiles aux anti-triches et à la sécurité.</summary>
public sealed class SecurityCheck : IDiagnosticCheck
{
    public string Name => "sécurité du firmware";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var fw = ctx.Hardware.Firmware;

        if (fw.SecureBoot == false && fw.BootMode == BootMode.Uefi)
            yield return new("firmware.secureboot", "Secure Boot désactivé", Severity.Info,
                "Exigé par certains anti-triches (par exemple Valorant ou Battlefield 6). Inutile sinon pour les performances.",
                "À activer si un jeu le demande. Sur certaines cartes, il faut d'abord restaurer les clés par défaut (« Install default Secure Boot keys »).",
                ctx.Bios.Steps("secure_boot"), NeedsBios: true);
        else if (fw.SecureBoot == true)
            yield return new("firmware.secureboot", "Secure Boot actif", Severity.Ok, "");

        if (fw.TpmReady == false)
            yield return new("firmware.tpm", "TPM absent ou désactivé", Severity.Info,
                "Requis par Windows 11 et par certains anti-triches.",
                "Activer le TPM intégré au processeur dans le BIOS.",
                ctx.PlatformSteps("tpm"), NeedsBios: true);
        else if (fw.TpmReady == true)
            yield return new("firmware.tpm", $"TPM {fw.TpmVersion} actif", Severity.Ok, "");

        // Jamais modifiée par Aria : simple information
        if (fw.MemoryIntegrity == true)
            yield return new("security.hvci", "Intégrité de la mémoire activée", Severity.Ok,
                "Protection importante contre les logiciels malveillants. Impact en jeu généralement faible sur les processeurs récents : Aria la conserve.");
        else if (fw.MemoryIntegrity == false)
            yield return new("security.hvci", "Intégrité de la mémoire désactivée", Severity.Info,
                "Cette protection bloque des pilotes malveillants. Son impact en jeu est généralement faible sur les processeurs récents.",
                "Conseillé de la réactiver : Sécurité Windows → Sécurité de l'appareil → Isolation du noyau.");
    }
}

/// <summary>SMT/Hyper-Threading et virtualisation.</summary>
public sealed class CpuFeaturesCheck : IDiagnosticCheck
{
    public string Name => "fonctions du processeur";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var cpu = ctx.Hardware.Cpu;

        if (cpu.Cores > 0 && cpu.Threads == cpu.Cores)
            yield return new("cpu.smt", "Un seul thread par cœur", Severity.Info,
                $"{cpu.Cores} cœurs / {cpu.Threads} threads. Certains modèles n'ont pas de SMT/Hyper-Threading (ex. Core Ultra 200S) ; " +
                "si le vôtre en dispose, il est désactivé dans le BIOS.",
                "Vérifier la fiche du processeur, puis l'activer si disponible.",
                ctx.PlatformSteps("smt"));

        if (ctx.Hardware.Firmware.Virtualization == false)
            yield return new("cpu.virtualization", "Virtualisation désactivée", Severity.Info,
                "Sans effet sur les jeux. Utile seulement pour WSL, les émulateurs Android ou les machines virtuelles.",
                "À activer uniquement si vous en avez besoin.",
                ctx.PlatformSteps("virtualization"));
    }
}

/// <summary>Âge du BIOS et outil de mise à jour de la marque.</summary>
public sealed class BiosVersionCheck : IDiagnosticCheck
{
    public string Name => "version du BIOS";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var board = ctx.Hardware.Board;
        if (board.BiosDate is not { } date) yield break;

        var model = $"{board.Manufacturer} {board.Product}";
        var title = $"BIOS {board.BiosVersion} du {date:dd/MM/yyyy}";

        if ((DateTime.Today - date).TotalDays > 730)
            yield return new("bios.version", $"{title} (plus de 2 ans)", Severity.Info,
                $"Carte mère : {model}. Les mises à jour améliorent souvent la compatibilité mémoire (XMP/EXPO) et la stabilité.",
                $"Comparer avec la dernière version sur la page support de la {board.Product}. " +
                "Ne jamais couper le courant pendant une mise à jour.",
                [ctx.Bios.Brand.UpdateTool]);
        else
            yield return new("bios.version", title, Severity.Ok, $"Carte mère : {model}.");
    }
}
