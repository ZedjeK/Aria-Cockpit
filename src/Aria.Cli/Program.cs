using System.Diagnostics;
using System.Text;
using Aria.Cli;
using Aria.Cli.Ui;
using Aria.Core.Backup;
using Aria.Core.Diagnostics;
using Aria.Core.Inventory;
using Aria.Core.Models;
using Aria.Core.Reporting;
using Aria.Core.Settings;

Console.OutputEncoding = Encoding.UTF8;

var options = CommandLine.Parse(args);
if (options is null)
{
    Console.WriteLine(CommandLine.Help);
    return;
}

ConsoleUi.Title(options.Mode switch
{
    RunMode.Simulate => "Aria — simulation (rien ne sera modifié)",
    RunMode.Restore => "Aria — restauration",
    _ => "Aria"
});
ConsoleUi.Info("Analyse du matériel…");

var hw = HardwareInventory.Collect();
var bios = BiosGuide.Load(hw.Board, hw.Warnings);
var settings = SettingsCatalog.Build(hw);
var notes = new List<string>();
IReadOnlyList<DiagnosticFinding>? findings = null;
IReadOnlyList<SettingResult> results;

if (options.Mode == RunMode.Restore)
{
    results = RunRestore(options.BackupPath);
}
else
{
    // 1. Inventaire et diagnostic (lecture seule)
    InventoryPrinter.Print(hw, Console.Out);
    findings = DiagnosticRunner.Run(new DiagnosticContext(hw, bios));
    DiagnosticPrinter.Print(findings, bios, Console.Out);
    Console.WriteLine();

    // 2. État actuel, puis 3 à 5. liste à cocher, confirmation, sauvegarde, application
    var plan = SettingsPlanner.Build(hw, settings);
    ConsoleUi.ShowPlan(plan, "État actuel des réglages");
    results = options.Mode == RunMode.Simulate
        ? Simulate(plan)
        : ProcessChanges(plan, "Avant optimisation");
}

// 6. Rapport
Console.WriteLine();
if (results.Count > 0) ConsoleUi.ShowResults(results);
try
{
    ConsoleUi.Info($"Rapport enregistré : {ReportFile.Save(options.Mode, hw, findings, bios, results, notes)}");
}
catch (Exception e)
{
    ConsoleUi.Warning($"Rapport non enregistré : {e.Message}");
}

if (options.Mode != RunMode.Simulate) OfferRestart();

Console.WriteLine();
Console.WriteLine("Appuyez sur une touche pour quitter…");
Console.ReadKey(intercept: true);
return;

// ---------------------------------------------------------------------------------------------

// Mode simulation : on montre ce qui serait proposé.
IReadOnlyList<SettingResult> Simulate(IReadOnlyList<PlanItem> plan)
{
    notes.Add("Simulation : aucun réglage n'a été modifié.");
    return plan.Select(p => p.NeedsChange ? p.ToResult(SettingStatus.Simulated) : Untouched(p)).ToList();
}

// Liste à cocher → confirmation → sauvegarde → application des seules lignes cochées.
IReadOnlyList<SettingResult> ProcessChanges(IReadOnlyList<PlanItem> plan, string backupReason)
{
    var changes = plan.Where(p => p.NeedsChange).ToList();
    if (changes.Count == 0)
    {
        ConsoleUi.Success("Tous les réglages sont déjà à la bonne valeur : rien à changer.");
        return plan.Select(Untouched).ToList();
    }

    Console.WriteLine();
    var selected = ConsoleUi.SelectChanges(changes).ToHashSet();
    ApplyOutcome? outcome = null;

    if (selected.Count == 0)
        ConsoleUi.Info("Aucun changement retenu.");
    else if (!ConsoleUi.Confirm($"Appliquer {selected.Count} changement(s) ?"))
    {
        ConsoleUi.Info("Annulé : rien n'a été modifié.");
        selected.Clear();
    }
    else
    {
        // Sauvegarde, point de restauration et application, dans l'ordre du catalogue
        var ordered = plan.Where(selected.Contains).ToList();
        outcome = ChangeSession.Apply(hw, ordered, backupReason,
            new InlineProgress<ApplyStep>(step => { if (step.Done < step.Total) ConsoleUi.Info(step.Text); }));

        if (outcome.Error is not null)
        {
            ConsoleUi.Warning(outcome.Error);
            return outcome.Results.Concat(plan.Where(p => !selected.Contains(p))
                .Select(p => p.NeedsChange ? p.ToResult(SettingStatus.Declined) : Untouched(p))).ToList();
        }

        notes.Add($"Sauvegarde des valeurs d'origine : {outcome.BackupPath}");
        notes.Add(outcome.RestorePointMessage);
        ConsoleUi.Success($"Valeurs d'origine sauvegardées : {outcome.BackupPath}");
        if (outcome.RestorePointCreated) ConsoleUi.Success(outcome.RestorePointMessage);
        else ConsoleUi.Warning($"{outcome.RestorePointMessage} La sauvegarde Aria reste disponible.");
        if (outcome.Warning is not null)
        {
            notes.Add(outcome.Warning);
            ConsoleUi.Warning(outcome.Warning);
        }
    }

    return ChangeSession.Merge(plan, selected, outcome);
}

// Restauration : la cible de chaque réglage est sa valeur sauvegardée.
IReadOnlyList<SettingResult> RunRestore(string? backupPath)
{
    var available = BackupStore.List();
    if (backupPath is null)
    {
        if (available.Count == 0)
        {
            ConsoleUi.Warning($"Aucune sauvegarde trouvée dans {BackupStore.Folder}.");
            return [];
        }
        backupPath = ConsoleUi.Choose("Quelle sauvegarde restaurer ?", available.Select(p =>
        {
            try
            {
                var f = BackupStore.Load(p);
                return (p, $"{f.CreatedAt:dd/MM/yyyy HH:mm} — {f.Reason} — {f.Entries.Count} réglage(s)");
            }
            catch (Exception) { return (p, $"{Path.GetFileName(p)} (illisible)"); }
        }).ToList());
    }

    BackupFile file;
    try { file = BackupStore.Load(backupPath); }
    catch (Exception e)
    {
        ConsoleUi.Warning($"Sauvegarde illisible : {e.Message}");
        return [];
    }
    notes.Add($"Restauration de : {backupPath}");

    var plan = new List<PlanItem>();
    var missing = new List<SettingResult>();
    foreach (var entry in file.Entries)
    {
        var setting = settings.FirstOrDefault(s => s.Id == entry.Id);
        if (setting is null)
            missing.Add(new SettingResult(entry.Id, entry.DisplayName, entry.Category, null, entry.Value, "",
                SettingStatus.Failed, false, "Réglage introuvable sur ce PC (écran débranché ?)"));
        else
            plan.Add(SettingsPlanner.PlanRestore(hw, setting, entry.Value, $"Valeur d'origine du {file.CreatedAt:dd/MM/yyyy HH:mm}"));
    }

    ConsoleUi.ShowPlan(plan, "Restauration : état actuel → valeur sauvegardée");
    foreach (var m in missing) ConsoleUi.Warning($"{m.DisplayName} : {m.Error}");

    return [.. ProcessChanges(plan, "Avant restauration"), .. missing];
}

// Fin de parcours : redémarrage classique ou directement dans le BIOS, toujours au choix.
void OfferRestart()
{
    var needsReboot = results.Any(r => r.Status == SettingStatus.Changed && r.RequiresReboot);
    var biosFixes = hw.Firmware.BootMode == BootMode.Uefi
                    && findings?.Any(f => f.NeedsBios && f.Severity >= Severity.Warning) == true;
    if (!needsReboot && !biosFixes) return;

    var choices = new List<(string, string)> { ("none", "Ne pas redémarrer maintenant") };
    if (needsReboot) choices.Add(("reboot", "Redémarrer Windows (termine l'application des réglages)"));
    if (biosFixes) choices.Add(("bios", "Redémarrer directement dans le BIOS" + (needsReboot ? " (termine aussi les réglages)" : "")));

    Console.WriteLine();
    ConsoleUi.Warning("Enregistrez votre travail avant de redémarrer.");
    var arguments = ConsoleUi.Choose("Redémarrage", choices) switch
    {
        "reboot" => "/r /t 10",
        "bios" => "/r /fw /t 10",
        _ => null
    };
    if (arguments is null) return;

    ConsoleUi.Info("Redémarrage dans 10 secondes… (annuler avec la commande : shutdown /a)");
    Process.Start(new ProcessStartInfo("shutdown", arguments) { UseShellExecute = false, CreateNoWindow = true });
}

static SettingResult Untouched(PlanItem p) => p.ToResult(
    p.ReadError is not null ? SettingStatus.Failed
    : p.NotApplicableReason is not null ? SettingStatus.NotApplicable
    : SettingStatus.Unchanged);
