using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Aria.Gui.Services;

/// <summary>Une ligne de la liste des changements : nom, et éventuellement « avant → après ».</summary>
public sealed record DialogRow(string Name, string? From = null, string? To = null)
{
    public bool HasFrom => !string.IsNullOrEmpty(From);
    public bool HasTo => !string.IsNullOrEmpty(To);
}

/// <summary>Contenu d'une confirmation.</summary>
/// <param name="Rows">Changements listés en lignes alignées.</param>
/// <param name="Warnings">Points d'attention, en orange.</param>
/// <param name="Note">Texte discret en bas (sauvegarde, point de restauration…).</param>
/// <param name="IsRisky">Action lourde (redémarrage) : bouton de confirmation en avant mais « Annuler » actif par défaut.</param>
/// <param name="ShowCancel">Faux pour un simple message (un seul bouton).</param>
/// <param name="Icon">Icône d'en-tête (nom WPF-UI, ex. « Delete24 ») ; par défaut bouclier, ou bouton marche/arrêt si IsRisky.</param>
public sealed record DialogRequest(
    string Title,
    string ConfirmText,
    string? Message = null,
    IReadOnlyList<DialogRow>? Rows = null,
    IReadOnlyList<string>? Warnings = null,
    string? Note = null,
    string CancelText = "Annuler",
    bool IsRisky = false,
    bool ShowCancel = true,
    string? Icon = null);

/// <summary>
/// Confirmations affichées DANS la fenêtre d'Aria (voile + panneau néon), au lieu d'une fenêtre Windows séparée.
/// Une seule à la fois : si une autre arrive, la précédente est considérée comme annulée.
/// </summary>
public sealed partial class DialogService : ObservableObject
{
    public static DialogService Current { get; } = new();

    private TaskCompletionSource<bool>? _pending;

    [ObservableProperty] private bool isOpen;
    [ObservableProperty] private string title = "";
    [ObservableProperty] private string? message;
    [ObservableProperty] private IReadOnlyList<DialogRow> rows = [];
    [ObservableProperty] private IReadOnlyList<string> warnings = [];
    [ObservableProperty] private string? note;
    [ObservableProperty] private string confirmText = "";
    [ObservableProperty] private string cancelText = "";
    [ObservableProperty] private bool isRisky;
    [ObservableProperty] private bool showCancel = true;
    [ObservableProperty] private string? icon;
    [ObservableProperty] private bool hasMessage;
    [ObservableProperty] private bool hasRows;
    [ObservableProperty] private bool hasWarnings;
    [ObservableProperty] private bool hasNote;

    /// <summary>Affiche la confirmation et attend la réponse : vrai si l'utilisateur confirme.</summary>
    public Task<bool> ConfirmAsync(DialogRequest request)
    {
        _pending?.TrySetResult(false);
        _pending = new TaskCompletionSource<bool>();

        Title = request.Title;
        Message = request.Message;
        Rows = request.Rows ?? [];
        Warnings = request.Warnings ?? [];
        Note = request.Note;
        ConfirmText = request.ConfirmText;
        CancelText = request.CancelText;
        IsRisky = request.IsRisky;
        ShowCancel = request.ShowCancel;
        Icon = request.Icon;
        HasMessage = !string.IsNullOrWhiteSpace(Message);
        HasRows = Rows.Count > 0;
        HasWarnings = Warnings.Count > 0;
        HasNote = !string.IsNullOrWhiteSpace(Note);
        IsOpen = true;
        return _pending.Task;
    }

    [RelayCommand]
    private void Confirm() => Close(true);

    [RelayCommand]
    private void Cancel() => Close(false);

    private void Close(bool result)
    {
        if (!IsOpen) return;
        IsOpen = false;
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(result);
    }
}
