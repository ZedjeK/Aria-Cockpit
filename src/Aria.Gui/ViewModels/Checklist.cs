using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Aria.Gui.ViewModels;

public enum BadgeKind { Reboot, Comfort, Caution, Info, Good }

/// <summary>Petite étiquette affichée à droite d'une ligne (« REDÉMARRAGE », « CONFORT »…).</summary>
public sealed record Badge(string Text, BadgeKind Kind);

/// <summary>
/// Une ligne de liste à cocher, réutilisée par les pages Réglages et Mises à jour.
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

    public CheckGroup(string title, IEnumerable<CheckItem> items)
    {
        Title = title;
        Items = new ObservableCollection<CheckItem>(items);
        foreach (var item in Items) item.PropertyChanged += OnItemChanged;
    }

    public string Title { get; }
    public ObservableCollection<CheckItem> Items { get; }
    public string Header => $"{Title.ToUpperInvariant()} ({Items.Count})";

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
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_bulkUpdate && e.PropertyName == nameof(CheckItem.IsSelected)) OnPropertyChanged(nameof(AllSelected));
    }
}
