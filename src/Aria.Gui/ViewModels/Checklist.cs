using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aria.Gui.ViewModels;

public enum BadgeKind { Reboot, Comfort, Caution, Info, Good }

/// <summary>Petite étiquette affichée à droite d'une ligne (« REDÉMARRAGE », « CONFORT »…).</summary>
public sealed record Badge(string Text, BadgeKind Kind);

/// <summary>
/// Une ligne de liste à cocher, réutilisée par les pages Réglages, Mises à jour, Nettoyage et Restauration.
/// <see cref="Payload"/> contient l'objet métier (un réglage, une mise à jour…).
/// </summary>
public sealed partial class CheckItem(
    string title, string from, string to, string reason, bool selected, object payload, IReadOnlyList<Badge> badges)
    : ObservableObject
{
    [ObservableProperty]
    private bool isSelected = selected;

    public string Title { get; } = title;
    public string From { get; } = from;
    public string To { get; } = to;
    public bool HasFrom => !string.IsNullOrEmpty(From);
    public bool HasTo => !string.IsNullOrEmpty(To);
    public string Reason { get; } = reason;
    public object Payload { get; } = payload;
    public IReadOnlyList<Badge> Badges { get; } = badges;
}

/// <summary>Un groupe de lignes, avec une case « tout cocher » à trois états.</summary>
public sealed class CheckGroup : ObservableObject
{
    private bool _bulkUpdate;

    public CheckGroup(string title, IEnumerable<CheckItem> items, string? note = null, string? description = null)
    {
        Title = title;
        Note = note ?? "";
        Description = description ?? "";
        Items = new ObservableCollection<CheckItem>(items);
        foreach (var item in Items) item.PropertyChanged += OnItemChanged;
    }

    public string Title { get; }

    /// <summary>Explication commune à tout le groupe, affichée une fois sous son titre.</summary>
    public string Note { get; }
    public bool HasNote => Note.Length > 0;

    /// <summary>Ce que contient le groupe, affiché en tête de la catégorie dans le menu des catégories.</summary>
    public string Description { get; }
    public bool HasDescription => Description.Length > 0;
    public ObservableCollection<CheckItem> Items { get; }
    public string Header => $"{Title.ToUpperInvariant()} ({Items.Count})";

    /// <summary>Lignes cochées sur le total, pour le menu des catégories (« 2 / 3 »).</summary>
    public string Counter => $"{Items.Count(i => i.IsSelected)} / {Items.Count}";

    /// <summary>Vrai si tout est coché, faux si rien, null si mélange.</summary>
    public bool? AllSelected
    {
        get => Items.All(i => i.IsSelected) ? true : Items.Any(i => i.IsSelected) ? null : false;
        set
        {
            if (value is not bool selected) return;
            _bulkUpdate = true;
            foreach (var item in Items) item.IsSelected = selected;
            _bulkUpdate = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Counter));
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_bulkUpdate || e.PropertyName != nameof(CheckItem.IsSelected)) return;
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(Counter));
    }
}
