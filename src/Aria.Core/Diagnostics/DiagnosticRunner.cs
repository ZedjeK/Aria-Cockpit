using Aria.Core.Diagnostics.Checks;
using Aria.Core.Models;

namespace Aria.Core.Diagnostics;

public static class DiagnosticRunner
{
    /// <summary>Ordre d'exécution = ordre logique du rapport (matériel → firmware → logiciel).</summary>
    private static readonly IDiagnosticCheck[] Checks =
    [
        new DisplayConnectionCheck(),
        new DisplayLinkCheck(),
        new MicrocodeCheck(),
        new MemorySpeedCheck(),
        new MemoryChannelCheck(),
        new PcieLinkCheck(),
        new ResizableBarCheck(),
        new NvapiCheck(),
        new GsyncCheck(),
        new DlssAdviceCheck(),
        new BootModeCheck(),
        new SecurityCheck(),
        new CpuFeaturesCheck(),
        new BiosVersionCheck(),
        new GpuDriverCheck(),
        new SsdHealthCheck(),
        new StorageCheck(),
        new NetworkCheck(),
        new SystemHealthCheck(),
        new StartupCheck()
    ];

    public static IReadOnlyList<DiagnosticFinding> Run(DiagnosticContext context)
    {
        var findings = new List<DiagnosticFinding>();
        foreach (var check in Checks)
        {
            try
            {
                findings.AddRange(check.Run(context));
            }
            catch (Exception e)
            {
                findings.Add(new DiagnosticFinding("error", $"Contrôle « {check.Name} » impossible", Severity.Info, e.Message));
            }
        }
        return findings;
    }
}
