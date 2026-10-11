using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Models;
using Aria.Core.Reporting;
using Aria.Core.Settings;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

/// <summary>Ligne simple : un nom et une valeur (ou une raison).</summary>
public sealed record SimpleRow(string Name, string Value);

/// <summary>Ligne du résultat après application.</summary>
/// <param name="IsChange">Vrai si la valeur a réellement changé : « avant → après ». Sinon, seule la valeur actuelle s'affiche.</param>
public sealed record ResultRow(string Name, string Before, string After, string Status, Severity Tone, string? Error, bool IsChange)
{
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}

/// <summary>Textes propres à chaque page (Réglages ou Restauration).</summary>
public sealed record ChangeTexts(
    string ConfirmTitle,
    string ConfirmVerb,
    string BackupReason,
    RunMode Mode,
    string DoneStatus,
    string DoneLabel,
    string ApplyingTitle);

/// <summary>
/// Socle des pages qui modifient des réglages : confirmation, sauvegarde des valeurs actuelles,
/// point de restauration, application avec progression, puis écran de résultat et rapport.
/// </summary>
public abstract partial class ChangeViewModelBase : ObservableObject
{
    // ---- Application en cours ----
    [ObservableProperty] private bool isApplying;
    [ObservableProperty] private string applyingTitle = "APPLICATION EN COURS";
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private double progressValue;

    // ---- Résultat ----
    [ObservableProperty] private bool showResults;
    [ObservableProperty] private IReadOnlyList<ResultRow> results = [];
    [ObservableProperty] private int appliedCount;
    [ObservableProperty] private string appliedLabel = "APPLIQUÉS";
    [ObservableProperty] private int declinedCount;
    [ObservableProperty] private int failedCount;
    [ObservableProperty] private IReadOnlyList<string> resultNotes = [];
    [ObservableProperty] private bool needsReboot;
    [ObservableProperty] private string? reportPath;

    partial void OnIsApplyingChanged(bool value) => ApplyingChanged();
    partial void OnShowResultsChanged(bool value) => ResultsChanged();

    /// <summary>Pour réactiver le bouton « Appliquer » de la page.</summary>
    protected virtual void ApplyingChanged() { }

    /// <summary>Pour basculer l'affichage de la page.</summary>
    protected virtual void ResultsChanged() { }

    /// <param name="plan">Toutes les lignes affichées (pour le bilan).</param>
    /// <param name="ordered">Lignes cochées, dans l'ordre d'application.</param>
    /// <param name="extraNotes">Notes ajoutées en tête du rapport (ex. sauvegarde restaurée).</param>
    protected async Task RunAsync(AnalysisResult analysis, IReadOnlyList<PlanItem> plan, IReadOnlyList<PlanItem> ordered, ChangeTexts texts,
        IEnumerable<string>? extraNotes = null)
    {
        var confirmed = await DialogService.Current.ConfirmAsync(new DialogRequest(
            Title: texts.ConfirmTitle,
            ConfirmText: $"{texts.ConfirmVerb} ({ordered.Count})",
            Rows: ordered.Select(p => new DialogRow(p.Setting.DisplayName, p.Current!.Display, p.Target!.Value.Display)).ToList(),
            Note: "Avant toute modification, Aria sauvegarde les valeurs actuelles et crée un point de restauration Windows."));
        if (!confirmed) return;

        ApplyingTitle = texts.ApplyingTitle;
        AppliedLabel = texts.DoneLabel;
        IsApplying = true;
        ProgressValue = 0;
        ProgressText = "Préparation…";
        var progress = new Progress<ApplyStep>(step =>
        {
            ProgressText = step.Text;
            ProgressValue = step.Total == 0 ? 0 : 100.0 * step.Done / step.Total;
        });

        try
        {
            var hw = analysis.Hardware;
            var outcome = await Task.Run(() => ChangeSession.Apply(hw, ordered, texts.BackupReason, progress));
            ShowOutcome(analysis, plan, ChangeSession.Merge(plan, ordered, outcome), outcome, texts, extraNotes);
        }
        catch (Exception e)
        {
            ResultNotes = [$"Erreur inattendue : {e.Message}", $"Détails : {ErrorLog.Write(e)}"];
            Results = [];
            ShowResults = true;
        }
        finally
        {
            IsApplying = false;
        }
    }

    private void ShowOutcome(AnalysisResult analysis, IReadOnlyList<PlanItem> plan, IReadOnlyList<SettingResult> merged, ApplyOutcome outcome,
        ChangeTexts texts, IEnumerable<string>? extraNotes)
    {
        var proposed = plan.Where(p => p.NeedsChange).Select(p => p.Setting.Id).ToHashSet();
        Results = merged.Where(r => proposed.Contains(r.Id)).Select(r => r.Status switch
        {
            SettingStatus.Changed => new ResultRow(r.DisplayName, r.Current?.Display ?? "", r.Target?.Display ?? "",
                r.RequiresReboot ? $"{texts.DoneStatus} · AU REDÉMARRAGE" : texts.DoneStatus, Severity.Ok, null, IsChange: true),
            SettingStatus.Failed => new ResultRow(r.DisplayName, r.Current?.Display ?? "", "",
                "ÉCHEC", Severity.Critical, r.Error, IsChange: false),
            _ => new ResultRow(r.DisplayName, r.Current?.Display ?? "", "",
                "NON RETENU", Severity.Info, null, IsChange: false)
        }).ToList();

        AppliedCount = merged.Count(r => r.Status == SettingStatus.Changed);
        DeclinedCount = merged.Count(r => r.Status == SettingStatus.Declined);
        FailedCount = merged.Count(r => r.Status == SettingStatus.Failed && proposed.Contains(r.Id));
        NeedsReboot = merged.Any(r => r.Status == SettingStatus.Changed && r.RequiresReboot);

        var notes = new List<string>(extraNotes ?? []);
        if (outcome.Error is not null) notes.Add(outcome.Error);
        if (outcome.BackupPath is not null) notes.Add($"Sauvegarde des valeurs d'avant : {outcome.BackupPath}");
        if (outcome.RestorePointMessage.Length > 0) notes.Add(outcome.RestorePointMessage);
        if (outcome.Warning is not null) notes.Add(outcome.Warning);

        try
        {
            // Rapport de restauration : sans le diagnostic, qui n'a pas changé
            var findings = texts.Mode == RunMode.Restore ? null : analysis.Findings;
            ReportPath = ReportFile.Save(texts.Mode, analysis.Hardware, findings, analysis.Bios, merged, notes);
            notes.Add($"Rapport : {ReportPath}");
        }
        catch (Exception e)
        {
            ReportPath = null;
            notes.Add($"Rapport non enregistré : {e.Message}");
        }

        ResultNotes = notes;
        ShowResults = true;
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (ReportPath is { } path && File.Exists(path)) Shell.OpenFile(path);
    }

    [RelayCommand]
    private async Task Restart()
    {
        if (await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Redémarrer",
                ConfirmText: "Redémarrer",
                Message: "Le PC va redémarrer dans 10 secondes pour terminer l'application des réglages.",
                Warnings: ["Enregistrez votre travail avant de continuer."],
                IsRisky: true)))
            RestartService.Restart(intoBios: false);
    }

    /// <summary>Les lignes d'une liste à cocher, cochées ou non.</summary>
    protected static IEnumerable<CheckItem> ItemsOf(IEnumerable<CheckGroup> groups) => groups.SelectMany(g => g.Items);
}
