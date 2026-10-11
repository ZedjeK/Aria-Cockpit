using Aria.Core.Backup;
using Aria.Core.Diagnostics;
using Aria.Core.Models;

namespace Aria.Core.Reporting;

/// <summary>Rapport texte complet, enregistré dans C:\ProgramData\Aria\Rapports.</summary>
public static class ReportFile
{
    public static string Folder => Path.Combine(BackupStore.DataRoot, "Rapports");

    public static string Save(
        RunMode mode,
        HardwareProfile hw,
        IReadOnlyList<DiagnosticFinding>? findings,
        BiosGuide bios,
        IReadOnlyList<SettingResult> results,
        IEnumerable<string> notes)
    {
        Directory.CreateDirectory(Folder);
        var now = DateTime.Now;
        var path = Path.Combine(Folder, $"rapport_{now:yyyy-MM-dd_HHmmss}.txt");

        using var w = new StreamWriter(path);
        w.WriteLine($"Aria — rapport du {now:dd/MM/yyyy à HH:mm:ss} — {Environment.MachineName}");
        w.WriteLine($"Mode : {mode switch { RunMode.Simulate => "simulation (rien n'a été modifié)", RunMode.Restore => "restauration", _ => "optimisation" }}");

        InventoryPrinter.Print(hw, w);
        if (findings is not null) DiagnosticPrinter.Print(findings, bios, w);
        PrintResults(results, w);

        var noteList = notes.ToList();
        if (noteList.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("== NOTES ==");
            foreach (var n in noteList) w.WriteLine($"  {n}");
        }
        return path;
    }

    public static void PrintResults(IReadOnlyList<SettingResult> results, TextWriter w)
    {
        w.WriteLine();
        w.WriteLine("== RÉGLAGES ==");
        foreach (var group in results.GroupBy(r => r.Category))
        {
            w.WriteLine();
            w.WriteLine($"  {SettingCategoryNames.Of(group.Key)}");
            foreach (var r in group)
            {
                var change = r.Status is SettingStatus.Unchanged or SettingStatus.NotApplicable
                    ? r.Current?.Display ?? ""
                    : $"{r.Current?.Display ?? "?"} → {r.Target?.Display ?? "?"}";
                w.WriteLine($"  {StatusText(r),-22} {r.DisplayName} : {change}");
                if (r.Error is not null) w.WriteLine($"  {"",-22} ↳ {r.Error}");
                else if (r.Status == SettingStatus.NotApplicable) w.WriteLine($"  {"",-22} ↳ {r.Reason}");
            }
        }

        var reboot = results.Any(r => r.Status == SettingStatus.Changed && r.RequiresReboot);
        w.WriteLine();
        var simulated = Count(results, SettingStatus.Simulated);
        w.WriteLine($"  Bilan : " + (simulated > 0 ? $"{simulated} serai(en)t modifié(s) · " : "") +
                    $"{Count(results, SettingStatus.Changed)} appliqués · {Count(results, SettingStatus.Declined)} non retenus · " +
                    $"{Count(results, SettingStatus.Failed)} échecs · {Count(results, SettingStatus.Unchanged)} déjà corrects" +
                    (reboot ? " · redémarrage requis" : ""));
    }

    public static string StatusText(SettingResult r) => r.Status switch
    {
        SettingStatus.Unchanged => "[OK] déjà correct",
        SettingStatus.Changed => "[✓] appliqué",
        SettingStatus.Simulated => "[~] serait modifié",
        SettingStatus.Failed => "[X] échec",
        SettingStatus.NotApplicable => "[-] non applicable",
        SettingStatus.Declined => "[ ] non retenu",
        _ => ""
    };

    private static int Count(IEnumerable<SettingResult> results, SettingStatus status) => results.Count(r => r.Status == status);
}
