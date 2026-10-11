using Aria.Core.Diagnostics;
using Aria.Core.Inventory;
using Aria.Core.Models;
using Aria.Core.Settings;

namespace Aria.Gui.Services;

/// <summary>Résultat d'une analyse complète : matériel, diagnostic et état actuel des réglages.</summary>
public sealed record AnalysisResult(
    DateTime At,
    HardwareProfile Hardware,
    BiosGuide Bios,
    IReadOnlyList<DiagnosticFinding> Findings,
    IReadOnlyList<ISetting> Settings,
    IReadOnlyList<PlanItem> Plan);

public static class AnalysisService
{
    /// <summary>Lecture seule. À appeler hors du fil de l'interface (WMI peut prendre quelques secondes).</summary>
    public static AnalysisResult Run()
    {
        var hw = HardwareInventory.Collect();
        var bios = BiosGuide.Load(hw.Board, hw.Warnings);
        var findings = DiagnosticRunner.Run(new DiagnosticContext(hw, bios));
        var settings = SettingsCatalog.Build(hw);
        var plan = SettingsPlanner.Build(hw, settings);
        return new AnalysisResult(DateTime.Now, hw, bios, findings, settings, plan);
    }
}
