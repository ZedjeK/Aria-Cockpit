using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Cleanup;
using Aria.Core.Models;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

/// <summary>
/// Page Nettoyage : analyse de l'espace récupérable (lecture seule), liste à cocher, confirmation,
/// puis suppression des seuls éléments cochés. Mêmes noms de propriétés que les autres pages, pour réutiliser
/// le voile de progression et l'écran de résultat.
/// </summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly CleanupPaths _paths = CleanupPaths.ForThisPc();
    private IReadOnlyList<CleanupItem> _items = [];

    // ---- Liste ----
    [ObservableProperty] private IReadOnlyList<CheckGroup> groups = [];
    [ObservableProperty] private bool hasItems;
    [ObservableProperty] private bool hasNothing;
    [ObservableProperty] private IReadOnlyList<SimpleRow> empty = [];
    [ObservableProperty] private string emptyHeader = "";
    [ObservableProperty] private bool hasEmpty;
    [ObservableProperty] private bool hasScanned;
    [ObservableProperty] private string scanText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    private int selectedCount;

    [ObservableProperty] private string selectionText = "";
    [ObservableProperty] private string cleanText = "Nettoyer";

    // ---- Travail en cours (analyse ou nettoyage) ----
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CleanCommand))]
    private bool isApplying;

    [ObservableProperty] private string applyingTitle = "";
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private double progressValue;

    // ---- Résultat (noms attendus par ChangeResultView) ----
    [ObservableProperty] private bool showResults;
    [ObservableProperty] private IReadOnlyList<ResultRow> results = [];
    [ObservableProperty] private int appliedCount;
    [ObservableProperty] private string appliedLabel = "NETTOYÉS";
    [ObservableProperty] private int declinedCount;
    [ObservableProperty] private int failedCount;
    [ObservableProperty] private IReadOnlyList<string> resultNotes = [];
    [ObservableProperty] private bool needsReboot;
    [ObservableProperty] private string? reportPath;

    private IEnumerable<CheckItem> AllItems => Groups.SelectMany(g => g.Items);

    /// <summary>Première analyse à l'ouverture de la page.</summary>
    public Task EnsureScannedAsync() => HasScanned || IsApplying ? Task.CompletedTask : ScanAsync();

    // ---------------------------------------------------------------- Analyse

    [RelayCommand]
    private async Task ScanAsync()
    {
        ApplyingTitle = "ANALYSE DE L'ESPACE";
        ProgressText = "Calcul de la taille des fichiers récupérables (lecture seule)…";
        ProgressValue = 0;
        IsApplying = true;
        try
        {
            var paths = _paths;
            _items = await Task.Run(() => CleanupService.Scan(paths));
            Build();
            HasScanned = true;
            ScanText = $"Analysé à {DateTime.Now:HH:mm} · rien n'est supprimé tant que vous ne cliquez pas sur « Nettoyer ».";
        }
        catch (Exception e)
        {
            ScanText = $"Analyse impossible : {e.Message} (détails dans {ErrorLog.Write(e)})";
        }
        finally
        {
            IsApplying = false;
        }
    }

    private void Build()
    {
        var available = _items.Where(i => !i.IsEmpty).ToList();
        CheckGroup Group(string title, bool recommended) => new(title,
            available.Where(i => i.SelectedByDefault == recommended).Select(ToCheckItem));

        Groups = new[] { Group("Recommandé", true), Group("À la demande", false) }.Where(g => g.Items.Count > 0).ToList();
        foreach (var item in AllItems) item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CheckItem.IsSelected)) UpdateSelection();
        };

        Empty = _items.Where(i => i.IsEmpty).Select(i => new SimpleRow(i.Title, "rien à nettoyer")).ToList();
        EmptyHeader = $"Déjà propres ({Empty.Count})";
        HasEmpty = Empty.Count > 0;
        HasItems = Groups.Count > 0;
        HasNothing = !HasItems;
        UpdateSelection();
    }

    private static CheckItem ToCheckItem(CleanupItem item)
    {
        var badges = new List<Badge>();
        if (item.IsLong) badges.Add(new Badge("PLUSIEURS MINUTES", BadgeKind.Info));
        if (item.Kind == CleanupKind.PreviousWindows) badges.Add(new Badge("SANS RETOUR ARRIÈRE", BadgeKind.Caution));
        else if (item.Warning is not null) badges.Add(new Badge("À SAVOIR", BadgeKind.Comfort));

        var reason = item.Warning is null ? item.Description : $"{item.Description} {item.Warning}";
        return new CheckItem(item.Title, SizeText(item), "", reason, item.SelectedByDefault, item, badges);
    }

    private static string SizeText(CleanupItem item) =>
        item.Bytes is { } bytes ? CleanupReport.Size(bytes) : "taille connue après nettoyage";

    private void UpdateSelection()
    {
        var chosen = AllItems.Where(i => i.IsSelected).Select(i => (CleanupItem)i.Payload).ToList();
        var known = chosen.Where(i => i.Bytes is not null).Sum(i => i.Bytes!.Value);
        var unknown = chosen.Any(i => i.Bytes is null);
        SelectedCount = chosen.Count;
        SelectionText = chosen.Count == 0
            ? "Aucun élément sélectionné"
            : $"{chosen.Count} élément{(chosen.Count > 1 ? "s" : "")} · {(unknown ? "au moins " : "")}{CleanupReport.Size(known)} récupérables";
        CleanText = $"Nettoyer la sélection ({chosen.Count})";
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in AllItems) item.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in AllItems) item.IsSelected = false;
    }

    // ---------------------------------------------------------------- Nettoyage

    private bool CanClean() => SelectedCount > 0 && !IsApplying;

    [RelayCommand(CanExecute = nameof(CanClean))]
    private async Task Clean()
    {
        // Ordre de la liste d'origine (fichiers rapides d'abord, Windows.old en dernier)
        var chosenSet = AllItems.Where(i => i.IsSelected).Select(i => (CleanupItem)i.Payload).ToHashSet();
        var chosen = _items.Where(chosenSet.Contains).ToList();

        var warnings = new List<string> { "Suppression définitive : ni sauvegarde ni point de restauration ne peuvent récupérer ces fichiers." };
        warnings.AddRange(chosen.Where(i => i.Warning is not null).Select(i => $"{i.Title} : {i.Warning}"));
        if (chosen.Any(i => i.IsLong)) warnings.Add("Certains éléments prennent plusieurs minutes : laissez Aria ouverte jusqu'à la fin.");

        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Nettoyer",
                ConfirmText: $"Supprimer ({chosen.Count})",
                Rows: chosen.Select(i => new DialogRow(i.Title, null, SizeText(i))).ToList(),
                Warnings: warnings,
                Note: "Seuls ces emplacements sont touchés. Les fichiers en cours d'utilisation sont ignorés.",
                IsRisky: true,
                Icon: "Delete24")))
            return;

        ApplyingTitle = "NETTOYAGE EN COURS";
        ProgressText = "Préparation…";
        ProgressValue = 0;
        IsApplying = true;
        var progress = new Progress<CleanupStep>(step =>
        {
            ProgressText = step.Text;
            ProgressValue = step.Total == 0 ? 0 : 100.0 * step.Done / step.Total;
        });

        try
        {
            var paths = _paths;
            var done = await Task.Run(() => CleanupService.Run(paths, chosen, progress));
            var declined = _items.Where(i => !i.IsEmpty && !chosenSet.Contains(i))
                .Select(i => new CleanupResult(i, CleanupStatus.Declined, 0, 0, 0));
            ShowOutcome([.. done, .. declined]);
        }
        catch (Exception e)
        {
            Results = [];
            ResultNotes = [$"Erreur inattendue : {e.Message}", $"Détails : {ErrorLog.Write(e)}"];
            ShowResults = true;
        }
        finally
        {
            IsApplying = false;
        }
    }

    private void ShowOutcome(IReadOnlyList<CleanupResult> all)
    {
        Results = all.Select(r => r.Status switch
        {
            CleanupStatus.Done => new ResultRow(r.Item.Title, $"{CleanupReport.Size(r.Freed)} libérés", "", "NETTOYÉ", Severity.Ok, null, false),
            CleanupStatus.Partial => new ResultRow(r.Item.Title, $"{CleanupReport.Size(r.Freed)} libérés", "", "EN PARTIE", Severity.Info,
                r.Error ?? $"{r.Skipped} fichier(s) ignoré(s) : en cours d'utilisation.", false),
            CleanupStatus.Declined => new ResultRow(r.Item.Title, "", "", "NON RETENU", Severity.Info, null, false),
            _ => new ResultRow(r.Item.Title, "", "", "ÉCHEC", Severity.Critical, r.Error, false)
        }).ToList();

        var done = all.Where(r => r.Status != CleanupStatus.Declined).ToList();
        AppliedCount = done.Count(r => r.Status is CleanupStatus.Done or CleanupStatus.Partial);
        DeclinedCount = all.Count(r => r.Status == CleanupStatus.Declined);
        FailedCount = done.Count(r => r.Status == CleanupStatus.Failed);

        var notes = new List<string> { $"Espace libéré au total : {CleanupReport.Size(done.Sum(r => r.Freed))}" };
        var skipped = done.Sum(r => r.Skipped);
        if (skipped > 0) notes.Add($"{skipped} fichier(s) ignoré(s) car en cours d'utilisation : normal, ils seront nettoyés une prochaine fois.");
        try
        {
            ReportPath = CleanupReport.Save(all, DateTime.Now);
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

    /// <summary>Retour à la liste, avec une nouvelle analyse pour montrer ce qui reste.</summary>
    [RelayCommand]
    private async Task Finish()
    {
        ShowResults = false;
        await ScanAsync();
    }
}
