using System.Text.RegularExpressions;
using Aria.Core.Models;

namespace Aria.Core.Diagnostics.Checks;

/// <summary>Écran de jeu branché sur la sortie de la carte mère au lieu de la carte graphique.</summary>
public sealed class DisplayConnectionCheck : IDiagnosticCheck
{
    public string Name => "branchement des écrans";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var hw = ctx.Hardware;
        // Sur portable, passer par la puce intégrée est normal (Optimus).
        if (hw.IsLaptop || !hw.HasNvidiaGpu) yield break;

        foreach (var display in hw.Displays.Where(d => !d.AdapterName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)))
        {
            yield return new("display.connection",
                $"{display.MonitorName} branché sur la carte mère", display.IsPrimary ? Severity.Critical : Severity.Info,
                $"Cet écran est relié à « {display.AdapterName} » : la carte NVIDIA ne calcule pas son image." +
                (display.IsPrimary ? "" : " Acceptable pour un écran secondaire hors jeu."),
                "Brancher le câble sur une sortie de la carte graphique (à l'arrière, plus bas que les sorties de la carte mère).");
        }
    }
}

/// <summary>Lien PCIe de la carte graphique (nombre de lignes et génération).</summary>
public sealed partial class PcieLinkCheck : IDiagnosticCheck
{
    public string Name => "lien PCIe";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var link = ctx.Hardware.NvidiaLink;
        var gpu = ctx.Hardware.NvidiaGpu;
        if (link is null || gpu is null || link.PcieWidthMax == 0) yield break;

        var summary = $"PCIe Gen{link.PcieGenMax} x{link.PcieWidthMax}";
        if (ctx.Hardware.IsLaptop)
        {
            yield return new("pcie.link", summary, Severity.Ok, "PC portable : lien fixé par le fabricant.");
            yield break;
        }

        // Certaines cartes n'ont physiquement que 8 lignes (RTX 4060, 5060, 3050…)
        var nativeWidth = NativeX8().IsMatch(gpu.Name) ? 8 : 16;
        var problem = false;

        if (link.PcieWidthMax < nativeWidth)
        {
            problem = true;
            yield return new("pcie.width",
                $"Carte graphique en x{link.PcieWidthMax} au lieu de x{nativeWidth}", Severity.Warning,
                "La carte ne dispose pas de toutes ses lignes PCIe.",
                "Vérifier que la carte est dans le premier port PCIe (le plus proche du processeur) et bien enfoncée. " +
                "Sur certaines cartes mères, un SSD NVMe dans certains emplacements partage les lignes de ce port : voir le manuel.");
        }

        if (link.PcieGenGpuMax > 0 && link.PcieGenMax < link.PcieGenGpuMax)
        {
            problem = true;
            // Sur une carte x8, perdre une génération pèse beaucoup plus lourd
            yield return new("pcie.gen",
                $"Lien PCIe Gen{link.PcieGenMax} alors que la carte supporte Gen{link.PcieGenGpuMax}",
                nativeWidth == 8 ? Severity.Warning : Severity.Info,
                nativeWidth == 8
                    ? "Cette carte n'utilise que 8 lignes : une génération de moins réduit nettement sa bande passante."
                    : "Impact généralement faible en x16.",
                "Causes possibles : carte mère ou processeur plus anciens (normal), câble « riser » (boîtiers à carte verticale), ou génération du port forcée dans le BIOS.");
        }

        if (!problem)
            yield return new("pcie.link", summary, Severity.Ok, "Lien PCIe complet.");
    }

    [GeneratedRegex(@"RTX\s*(3050|4060|5050|5060)|GTX\s*1630", RegexOptions.IgnoreCase)]
    private static partial Regex NativeX8();
}

/// <summary>Resizable BAR, avec ses prérequis : mode UEFI, CSM désactivé, Above 4G Decoding.</summary>
public sealed partial class ResizableBarCheck : IDiagnosticCheck
{
    public string Name => "Resizable BAR";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var hw = ctx.Hardware;
        var link = hw.NvidiaLink;
        var gpu = hw.NvidiaGpu;
        if (link is null || gpu is null || link.Bar1TotalMiB == 0) yield break;

        if (link.Bar1TotalMiB > 256)
        {
            yield return new("rebar", "Resizable BAR actif", Severity.Ok, $"Fenêtre mémoire : {link.Bar1TotalMiB} Mio.");
            yield break;
        }

        if (!SupportsRebar().IsMatch(gpu.Name)) yield break; // RTX 20 et plus anciennes : non pris en charge

        const string gainNote = "Gain variable selon les jeux : le pilote NVIDIA ne l'utilise que pour une liste de jeux validés.";

        if (hw.IsLaptop)
        {
            yield return new("rebar", "Resizable BAR inactif", Severity.Info,
                $"Sur portable, il dépend du fabricant. {gainNote}",
                "Vérifier si une mise à jour du BIOS du portable l'ajoute (page support du fabricant).");
            yield break;
        }

        if (hw.Firmware.BootMode == BootMode.Legacy)
        {
            yield return new("rebar", "Resizable BAR impossible : Windows démarre en ancien mode (Legacy)", Severity.Warning,
                $"Le Resizable BAR exige le mode UEFI. {gainNote}",
                "NE PAS désactiver le CSM dans cet état : Windows ne démarrerait plus. Il faut d'abord convertir le disque " +
                "système en GPT (outil officiel Microsoft MBR2GPT, après sauvegarde) ou réinstaller Windows en mode UEFI.");
            yield break;
        }

        var steps = new List<string> { "Dans cet ordre :" };
        steps.AddRange(ctx.Bios.Steps("csm").Select((s, i) => i == 0 ? $"1. {s}" : $"   {s}"));
        steps.AddRange(ctx.Bios.Steps("above_4g").Select((s, i) => i == 0 ? $"2. {s}" : $"   {s}"));
        steps.AddRange(ctx.Bios.Steps("rebar").Select((s, i) => i == 0 ? $"3. {s}" : $"   {s}"));

        yield return new("rebar", "Resizable BAR inactif", Severity.Warning,
            $"Windows démarre bien en UEFI : l'activation est sans risque. {gainNote}",
            "Activer les trois options BIOS ci-dessous. Si l'option Re-Size BAR n'apparaît pas, activer d'abord Above 4G Decoding, " +
            "ou mettre à jour le BIOS.",
            steps, NeedsBios: true);
    }

    [GeneratedRegex(@"RTX\s*[A-Z]*\s*(30|40|50)\d{2}", RegexOptions.IgnoreCase)]
    private static partial Regex SupportsRebar();
}

/// <summary>Âge du pilote graphique NVIDIA.</summary>
public sealed class GpuDriverCheck : IDiagnosticCheck
{
    public string Name => "pilote graphique";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var gpu = ctx.Hardware.NvidiaGpu;
        if (gpu?.DriverDate is not { } date) yield break;

        var months = (DateTime.Today - date).TotalDays / 30.4;
        var version = gpu.VendorDriverVersion ?? gpu.DriverVersion;
        const string advice = "Mettre à jour via l'application NVIDIA ou le site nvidia.com.";

        if (months > 12)
            yield return new("gpu.driver", $"Pilote NVIDIA ancien ({version})", Severity.Warning,
                $"Daté du {date:dd/MM/yyyy}. Les pilotes récents corrigent des bugs et optimisent les nouveaux jeux.", advice);
        else if (months > 6)
            yield return new("gpu.driver", $"Pilote NVIDIA de plus de 6 mois ({version})", Severity.Info,
                $"Daté du {date:dd/MM/yyyy}.", advice);
        else
            yield return new("gpu.driver", $"Pilote NVIDIA récent ({version})", Severity.Ok, $"Daté du {date:dd/MM/yyyy}.");
    }
}
