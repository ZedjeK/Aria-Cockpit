using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Backup;
using Aria.Core.Models;
using Aria.Core.Settings;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

/// <summary>Une sauvegarde, présentée sous forme de carte.</summary>
/// <param name="OtherComputer">Nom du PC d'origine, s'il ne s'agit pas de celui-ci.</param>
/// <param name="IsReadable">Faux si le fichier est abîmé : la carte reste visible mais ne peut pas être choisie.</param>
public sealed record BackupCard(string Path, string Date, string Reason, string Summary, string Names, string? OtherComputer, bool IsReadable)
{
    public bool HasNames => Names.Length > 0;
    public bool IsOtherComputer => OtherComputer is not null;
    public string OtherComputerText => $"Créée sur un autre PC : {OtherComputer}";
}

/// <summary>
/// Page Restauration : choix d'une sauvegarde, puis liste à cocher « valeur actuelle → valeur enregistrée ».
/// La restauration suit le même parcours que l'optimisation (sauvegarde, point de restauration, vérification).
/// </summary>
public sealed partial class RestoreViewModel : ChangeViewModelBase
{
    private static readonly ChangeTexts Texts = new(
        ConfirmTitle: "Restaurer les réglages",
        ConfirmVerb: "Restaurer",
        BackupReason: "Avant restauration",
        Mode: RunMode.Restore,
        DoneStatus: "RESTAURÉ",
        DoneLabel: "RESTAURÉS",
        ApplyingTitle: "RESTAURATION EN COURS");

    private readonly AppState _state;
    private BackupCard? _card;
    private BackupFile? _file;
    private AnalysisResult? _analysis;
    private IReadOnlyList<PlanItem> _plan = [];
    private IReadOnlyList<string> _missingNotes = [];

    // ---- Liste des sauvegardes ----
    [ObservableProperty] private IReadOnlyList<BackupCard> backups = [];
    [ObservableProperty] private bool hasBackups;
    [ObservableProperty] private bool hasNoBackups;

    // ---- Affichage : liste, détail d'une sauvegarde, ou résultat ----
    [ObservableProperty] private bool showSelection;
    [ObservableProperty] private bool showList = true;
    [ObservableProperty] private bool showDetail;
    [ObservableProperty] private bool isLoading;

    // ---- Détail de la sauvegarde choisie ----
    [ObservableProperty] private string selectionTitle = "";
    [ObservableProperty] private string selectionSubtitle = "";
    [ObservableProperty] private IReadOnlyList<CheckGroup> groups = [];
    [ObservableProperty] private bool hasChanges;
    [ObservableProperty] private bool hasNoChanges;
    [ObservableProperty] private IReadOnlyList<SimpleRow> unchanged = [];
    [ObservableProperty] private IReadOnlyList<SimpleRow> missing = [];
    [ObservableProperty] private string unchangedHeader = "";
    [ObservableProperty] private string missingHeader = "";
    [ObservableProperty] private bool hasUnchanged;
    [ObservableProperty] private bool hasMissing;
    [ObservableProperty] private bool hasOthers;

    // ---- Sélection ----
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RestoreCommand))]
    private int selectedCount;

    [ObservableProperty] private string selectionText = "";
    [ObservableProperty] private string restoreText = "Restaurer";

    [ObservableProperty] private string? error;
    [ObservableProperty] private bool hasError;

    public RestoreViewModel(AppState state)
    {
        _state = state;
        _state.PropertyChanged += OnStateChanged;
        Reload();
    }

    partial void OnErrorChanged(string? value) => HasError = !string.IsNullOrEmpty(value);
    partial void OnShowSelectionChanged(bool value) => UpdateView();
    protected override void ResultsChanged() => UpdateView();
    protected override void ApplyingChanged() => RestoreCommand.NotifyCanExecuteChanged();

    private void UpdateView()
    {
        ShowList = !ShowSelection && !ShowResults;
        ShowDetail = ShowSelection && !ShowResults;
    }

    /// <summary>Nouvelle analyse pendant qu'une sauvegarde est affichée : les valeurs actuelles sont relues.</summary>
    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Analysis) && ShowDetail && !IsApplying) _ = BuildSelectionAsync();
    }

    private IEnumerable<CheckItem> AllItems => ItemsOf(Groups);

    // ---------------------------------------------------------------- Liste

    /// <summary>Relit le dossier des sauvegardes (appelé à chaque affichage de la page).</summary>
    public void Reload()
    {
        try
        {
            Backups = BackupStore.List().Select(ToCard).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Backups = [];
            Error = $"Dossier des sauvegardes illisible : {e.Message}";
        }
        HasBackups = Backups.Count > 0;
        HasNoBackups = !HasBackups;
    }

    private static BackupCard ToCard(string path)
    {
        try
        {
            var file = BackupStore.Load(path);
            var count = file.Entries.Count;
            var names = string.Join("  ·  ", file.Entries.Take(4).Select(e => e.DisplayName)) + (count > 4 ? $"  ·  +{count - 4}" : "");
            var other = string.Equals(file.Computer, Environment.MachineName, StringComparison.OrdinalIgnoreCase) ? null : file.Computer;
            return new BackupCard(path, $"{file.CreatedAt:dd/MM/yyyy à HH:mm}", file.Reason.ToUpperInvariant(),
                count == 1 ? "1 réglage enregistré" : $"{count} réglages enregistrés", names, other, IsReadable: true);
        }
        catch (Exception e)
        {
            return new BackupCard(path, Path.GetFileNameWithoutExtension(path), "ILLISIBLE",
                $"Fichier abîmé : {e.Message}", "", null, IsReadable: false);
        }
    }

    // ---------------------------------------------------------------- Détail

    /// <summary>Lit la valeur actuelle de chaque réglage enregistré (hors du fil de l'interface).</summary>
    private async Task<bool> BuildSelectionAsync()
    {
        if (_file is not { } file || _state.Analysis is not { } analysis) return false;

        IsLoading = true;
        try
        {
            var (plan, missingRows, notFound) = await Task.Run(() => PlanFor(analysis, file));
            _analysis = analysis;
            _plan = plan;
            _missingNotes = notFound;
            Show(file, plan, missingRows);
            return true;
        }
        catch (Exception e)
        {
            Error = $"Lecture des réglages impossible : {e.Message} (détails dans {ErrorLog.Write(e)})";
            return false;
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Plan de restauration dans l'ordre du catalogue, et réglages introuvables ou illisibles sur ce PC.
    /// Les réglages absents du catalogue sont notés dans le rapport (les illisibles y figurent déjà, en échec).
    /// </summary>
    private static (IReadOnlyList<PlanItem> Plan, IReadOnlyList<SimpleRow> Missing, IReadOnlyList<string> NotFound) PlanFor(
        AnalysisResult analysis, BackupFile file)
    {
        var order = analysis.Settings.Select((s, i) => (s.Id, i)).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().i);
        var reason = $"Retour à la valeur enregistrée le {file.CreatedAt:dd/MM/yyyy à HH:mm}";
        var plan = new List<PlanItem>();
        var missingRows = new List<SimpleRow>();
        var notFound = new List<string>();

        foreach (var entry in file.Entries)
        {
            var setting = analysis.Settings.FirstOrDefault(s => s.Id == entry.Id);
            if (setting is null)
            {
                missingRows.Add(new SimpleRow(entry.DisplayName, "Introuvable sur ce PC (écran débranché, carte graphique changée ?)"));
                notFound.Add($"Non restauré, introuvable sur ce PC : {entry.DisplayName} (valeur enregistrée : {entry.Display})");
                continue;
            }
            var item = SettingsPlanner.PlanRestore(analysis.Hardware, setting, entry.Value, reason);
            if (item.ReadError is not null) missingRows.Add(new SimpleRow(entry.DisplayName, item.ReadError));
            plan.Add(item);
        }
        return (plan.OrderBy(p => order.GetValueOrDefault(p.Setting.Id, int.MaxValue)).ToList(), missingRows, notFound);
    }

    private void Show(BackupFile file, IReadOnlyList<PlanItem> plan, IReadOnlyList<SimpleRow> missingRows)
    {
        var count = file.Entries.Count;
        SelectionTitle = $"Sauvegarde du {file.CreatedAt:dd/MM/yyyy à HH:mm}";
        SelectionSubtitle = $"{file.Reason}  ·  {(count == 1 ? "1 réglage enregistré" : $"{count} réglages enregistrés")}";

        Groups = plan.Where(p => p.NeedsChange)
            .GroupBy(p => p.Setting.Category)
            .Select(g => new CheckGroup(SettingCategoryNames.Of(g.Key), g.Select(ToCheckItem)))
            .ToList();
        foreach (var item in AllItems) item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CheckItem.IsSelected)) UpdateSelection();
        };

        Unchanged = plan.Where(p => !p.NeedsChange && p.ReadError is null)
            .Select(p => new SimpleRow(p.Setting.DisplayName, p.Current?.Display ?? "")).ToList();
        Missing = missingRows;

        UnchangedHeader = $"Déjà à la valeur enregistrée ({Unchanged.Count})";
        MissingHeader = $"Introuvables sur ce PC ({Missing.Count})";
        HasUnchanged = Unchanged.Count > 0;
        HasMissing = Missing.Count > 0;
        HasOthers = HasUnchanged || HasMissing;
        HasChanges = Groups.Count > 0;
        HasNoChanges = !HasChanges;
        UpdateSelection();
    }

    private static CheckItem ToCheckItem(PlanItem p)
    {
        var badges = new List<Badge>();
        if (p.Setting.RequiresReboot) badges.Add(new Badge("REDÉMARRAGE", BadgeKind.Reboot));
        return new CheckItem(p.Setting.DisplayName, p.Current!.Display, p.Target!.Value.Display, p.Target.Reason,
            selected: true, p, badges);
    }

    private void UpdateSelection()
    {
        var selected = AllItems.Where(i => i.IsSelected).ToList();
        var reboot = selected.Count(i => ((PlanItem)i.Payload).Setting.RequiresReboot);
        SelectedCount = selected.Count;
        SelectionText = selected.Count switch
        {
            0 => "Aucun réglage sélectionné",
            1 => "1 réglage à restaurer",
            var n => $"{n} réglages à restaurer"
        } + (reboot > 0 ? $" · {reboot} nécessite{(reboot > 1 ? "nt" : "")} un redémarrage" : "");
        RestoreText = $"Restaurer la sélection ({selected.Count})";
    }

    // ---------------------------------------------------------------- Commandes

    [RelayCommand]
    private void Refresh()
    {
        Error = null;
        Reload();
    }

    [RelayCommand]
    private async Task Choose(BackupCard? card)
    {
        if (card is not { IsReadable: true }) return;
        Error = null;

        // Analyse absente (échec au démarrage) : on la relance avant de lire les valeurs actuelles
        if (_state.Analysis is null) await _state.AnalyzeAsync();
        if (_state.Analysis is null)
        {
            Error = _state.Error ?? "L'analyse du PC n'est pas disponible : réessayez dans un instant.";
            return;
        }

        try
        {
            _file = BackupStore.Load(card.Path);
        }
        catch (Exception e)
        {
            Error = $"Sauvegarde illisible : {e.Message}";
            return;
        }
        _card = card;
        if (await BuildSelectionAsync()) ShowSelection = true;
    }

    [RelayCommand]
    private void Back()
    {
        ShowSelection = false;
        Error = null;
        Reload();
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

    private bool CanRestore() => SelectedCount > 0 && !IsApplying;

    [RelayCommand(CanExecute = nameof(CanRestore))]
    private async Task Restore()
    {
        if (_analysis is not { } analysis || _card is not { } card) return;

        var chosen = AllItems.Where(i => i.IsSelected).Select(i => (PlanItem)i.Payload).ToHashSet();
        var ordered = _plan.Where(chosen.Contains).ToList();
        await RunAsync(analysis, _plan, ordered, Texts, [$"Restauration de : {card.Path}", .. _missingNotes]);
    }

    /// <summary>Retour à la liste des sauvegardes (la restauration en a créé une nouvelle), puis nouvelle analyse.</summary>
    [RelayCommand]
    private async Task Finish()
    {
        ShowResults = false;
        ShowSelection = false;
        Reload();
        await _state.AnalyzeAsync();
    }
}
