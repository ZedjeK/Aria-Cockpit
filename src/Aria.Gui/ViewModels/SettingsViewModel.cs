using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Models;
using Aria.Core.Settings;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

public sealed partial class SettingsViewModel : ChangeViewModelBase
{
    private static readonly ChangeTexts Texts = new(
        ConfirmTitle: "Appliquer les changements",
        ConfirmVerb: "Appliquer",
        BackupReason: "Avant optimisation",
        Mode: RunMode.Interactive,
        DoneStatus: "APPLIQUÉ",
        DoneLabel: "APPLIQUÉS",
        ApplyingTitle: "APPLICATION EN COURS");

    private readonly AppState _state;
    private AnalysisResult? _analysis;

    // ---- Liste ----
    [ObservableProperty] private IReadOnlyList<CheckGroup> groups = [];
    [ObservableProperty] private bool hasChanges;
    [ObservableProperty] private bool hasNoChanges;
    [ObservableProperty] private IReadOnlyList<SimpleRow> unchanged = [];
    [ObservableProperty] private IReadOnlyList<SimpleRow> notApplicable = [];
    [ObservableProperty] private IReadOnlyList<SimpleRow> unreadable = [];
    [ObservableProperty] private string unchangedHeader = "";
    [ObservableProperty] private string notApplicableHeader = "";
    [ObservableProperty] private string unreadableHeader = "";
    [ObservableProperty] private bool hasNotApplicable;
    [ObservableProperty] private bool hasUnreadable;

    // ---- Sélection ----
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private int selectedCount;

    [ObservableProperty] private string selectionText = "";
    [ObservableProperty] private string applyText = "Appliquer";

    public SettingsViewModel(AppState state)
    {
        _state = state;
        _state.PropertyChanged += OnStateChanged;
        Rebuild();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Analysis)) Rebuild();
    }

    protected override void ApplyingChanged() => ApplyCommand.NotifyCanExecuteChanged();

    private IEnumerable<CheckItem> AllItems => ItemsOf(Groups);

    private void Rebuild()
    {
        _analysis = _state.Analysis;
        ShowResults = false;
        if (_analysis is null) return;

        var plan = _analysis.Plan;
        Groups = plan.Where(p => p.NeedsChange)
            .GroupBy(p => p.Setting.Category)
            .Select(g => new CheckGroup(SettingCategoryNames.Of(g.Key), g.Select(ToCheckItem)))
            .ToList();
        foreach (var item in AllItems) item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CheckItem.IsSelected)) UpdateSelection();
        };

        Unchanged = plan.Where(p => !p.NeedsChange && p.ReadError is null && p.NotApplicableReason is null)
            .Select(p => new SimpleRow(p.Setting.DisplayName, p.Current?.Display ?? "")).ToList();
        NotApplicable = plan.Where(p => p.NotApplicableReason is not null)
            .Select(p => new SimpleRow(p.Setting.DisplayName, p.NotApplicableReason!)).ToList();
        Unreadable = plan.Where(p => p.ReadError is not null)
            .Select(p => new SimpleRow(p.Setting.DisplayName, p.ReadError!)).ToList();

        UnchangedHeader = $"Déjà corrects ({Unchanged.Count})";
        NotApplicableHeader = $"Non applicables ({NotApplicable.Count})";
        UnreadableHeader = $"Lecture impossible ({Unreadable.Count})";
        HasNotApplicable = NotApplicable.Count > 0;
        HasUnreadable = Unreadable.Count > 0;
        HasChanges = Groups.Count > 0;
        HasNoChanges = !HasChanges;
        UpdateSelection();
    }

    private static CheckItem ToCheckItem(PlanItem p)
    {
        var badges = new List<Badge>();
        if (p.Setting.RequiresReboot) badges.Add(new Badge("REDÉMARRAGE", BadgeKind.Reboot));
        if (!p.Setting.SelectedByDefault)
            badges.Add(new Badge(p.Setting.Category == SettingCategory.Privacy ? "AU CHOIX" : "CONFORT", BadgeKind.Comfort));
        return new CheckItem(p.Setting.DisplayName, p.Current!.Display, p.Target!.Value.Display, p.Target.Reason,
            p.Setting.SelectedByDefault, p, badges);
    }

    private void UpdateSelection()
    {
        var selected = AllItems.Where(i => i.IsSelected).ToList();
        var reboot = selected.Count(i => ((PlanItem)i.Payload).Setting.RequiresReboot);
        SelectedCount = selected.Count;
        SelectionText = selected.Count switch
        {
            0 => "Aucun changement sélectionné",
            1 => "1 changement sélectionné",
            var n => $"{n} changements sélectionnés"
        } + (reboot > 0 ? $" · {reboot} nécessite{(reboot > 1 ? "nt" : "")} un redémarrage" : "");
        ApplyText = $"Appliquer la sélection ({selected.Count})";
    }

    // ---------------------------------------------------------------- Commandes

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

    [RelayCommand]
    private Task Reanalyze() => _state.AnalyzeAsync();

    private bool CanApply() => SelectedCount > 0 && !IsApplying;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        if (_analysis is not { } analysis) return;

        // Dans l'ordre du catalogue (ex. plan d'alimentation avant état minimal du processeur)
        var chosen = AllItems.Where(i => i.IsSelected).Select(i => (PlanItem)i.Payload).ToHashSet();
        var ordered = analysis.Plan.Where(chosen.Contains).ToList();
        await RunAsync(analysis, analysis.Plan, ordered, Texts);
    }

    /// <summary>Retour à la liste, avec une nouvelle analyse pour afficher l'état réel.</summary>
    [RelayCommand]
    private async Task Finish()
    {
        ShowResults = false;
        await _state.AnalyzeAsync();
    }
}
