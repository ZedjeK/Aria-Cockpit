using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Reporting;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

/// <summary>Une ligne de la liste des rapports : type (étiquette), date et bilan.</summary>
public sealed record ReportRow(string Path, Badge Badge, string Date, string Summary)
{
    public bool HasSummary => Summary.Length > 0;
    public bool IsHtml => Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Page Rapports : liste des rapports enregistrés et aperçu du rapport choisi.</summary>
public sealed partial class ReportsViewModel : ObservableObject
{
    private readonly AppState _state;

    [ObservableProperty] private IReadOnlyList<ReportRow> reports = [];
    [ObservableProperty] private bool hasReports;
    [ObservableProperty] private bool hasNoReports;
    [ObservableProperty] private string countText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenSelectedCommand))]
    private ReportRow? selected;

    [ObservableProperty] private string preview = "";
    [ObservableProperty] private bool hasSelection;

    public string Folder => ReportFile.Folder;

    public ReportsViewModel(AppState state)
    {
        _state = state;
        _state.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppState.HidePersonalInfo)) OnPropertyChanged(nameof(HidePersonalInfo));
        };
        Reload();
    }

    /// <summary>Choix partagé avec la page Mon PC.</summary>
    public bool HidePersonalInfo
    {
        get => _state.HidePersonalInfo;
        set => _state.HidePersonalInfo = value;
    }

    partial void OnSelectedChanged(ReportRow? value)
    {
        HasSelection = value is not null;
        Preview = value is null ? "" : ReadPreview(value);
    }

    private static string ReadPreview(ReportRow row)
    {
        if (row.IsHtml)
            return $"Fiche PC au format HTML.\n\n{row.Summary}\n\nCliquez sur « Ouvrir » pour l'afficher dans le navigateur.\n" +
                   "Elle s'imprime ou s'enregistre en PDF depuis le navigateur (Ctrl+P).";
        var path = row.Path;
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Lecture impossible : {e.Message}";
        }
    }

    /// <summary>Relit le dossier (appelé à chaque affichage de la page). Garde le rapport choisi s'il existe encore.</summary>
    public void Reload()
    {
        var keep = Selected?.Path;
        IReadOnlyList<ReportInfo> infos;
        try
        {
            infos = ReportIndex.List();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            infos = [];
            ErrorLog.Write(e);
        }

        Selected = null;
        Reports = infos.Select(ToRow).ToList();
        HasReports = Reports.Count > 0;
        HasNoReports = !HasReports;
        CountText = Reports.Count switch { 0 => "", 1 => "1 RAPPORT", var n => $"{n} RAPPORTS" };
        Selected = Reports.FirstOrDefault(r => r.Path == keep) ?? Reports.FirstOrDefault();
    }

    private static ReportRow ToRow(ReportInfo info) => new(
        info.Path,
        info.Kind switch
        {
            ReportKind.Settings => new Badge("RÉGLAGES", BadgeKind.Reboot),       // orange
            ReportKind.Restore => new Badge("RESTAURATION", BadgeKind.Good),      // turquoise
            ReportKind.Updates => new Badge("MISES À JOUR", BadgeKind.Info),      // cyan
            ReportKind.PcSheet => new Badge("FICHE PC", BadgeKind.Info),          // cyan
            ReportKind.Cleanup => new Badge("NETTOYAGE", BadgeKind.Good),         // turquoise
            ReportKind.GpuTuning => new Badge("RÉGLAGE GPU", BadgeKind.Reboot),   // orange
            _ => new Badge("SIMULATION", BadgeKind.Comfort)                       // cyan
        },
        $"{info.At:dd/MM/yyyy à HH:mm}",
        info.Summary);

    // ---------------------------------------------------------------- Commandes

    [RelayCommand]
    private void Refresh() => Reload();

    private bool CanOpenSelected() => Selected is not null;

    [RelayCommand(CanExecute = nameof(CanOpenSelected))]
    private void OpenSelected()
    {
        if (Selected is not { } row || !File.Exists(row.Path)) return;
        // Fiche HTML : navigateur sans les droits administrateur ; rapport texte : application associée
        if (row.IsHtml) Shell.OpenUnelevated(row.Path);
        else Shell.OpenFile(row.Path);
    }

    [RelayCommand]
    private async Task ExportHtml()
    {
        if (await SheetExporter.ExportAsync(_state) is not { } path) return;
        Reload();
        Selected = Reports.FirstOrDefault(r => r.Path == path) ?? Selected;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        Directory.CreateDirectory(Folder);
        Shell.OpenUnelevated(Folder);
    }
}
