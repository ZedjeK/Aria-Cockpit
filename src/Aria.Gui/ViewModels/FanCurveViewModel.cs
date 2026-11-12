using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Nvidia;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

/// <summary>Un point modifiable de la courbe.</summary>
public sealed partial class FanPointRow(FanCurvePoint point) : ObservableObject
{
    [ObservableProperty] private int temperatureC = point.TemperatureC;
    [ObservableProperty] private int percent = point.Percent;

    public FanCurvePoint ToPoint() => new(TemperatureC, Percent);
}

/// <summary>Ligne de « Mes courbes » : la courbe, si c'est elle qui tourne, et le résultat du dernier clic sur cette ligne.</summary>
public sealed partial class FanProfileRow(SavedFanProfile profile) : ObservableObject
{
    public SavedFanProfile Profile { get; } = profile;
    public string Name => Profile.Name;
    public string Summary => Profile.Summary;

    /// <summary>C'est cette courbe qu'Aria fait tourner.</summary>
    [ObservableProperty] private bool isActive;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasNote))] private string note = "";
    [ObservableProperty] private bool noteIsError;
    public bool HasNote => Note.Length > 0;

    /// <summary>Résultat affiché sur la ligne, là où l'on vient de cliquer.</summary>
    public void Say(string text, bool error = false)
    {
        Note = text;
        NoteIsError = error;
    }
}

/// <summary>
/// Carte « Ventilateur » de Réglage GPU : un préréglage ou une courbe personnalisée, que la courbe fait tourner
/// tant qu'Aria est ouvert (<see cref="FanCurveService"/>). Rien n'est appliqué sans confirmation.
/// </summary>
public sealed partial class FanCurveViewModel : ObservableObject
{
    public const string AutoChoice = "Automatique (pilote NVIDIA)";
    public const string CustomChoice = "Personnalisée";

    private bool _loaded, _filling;
    private IReadOnlyList<FanCurvePoint> _custom = FanCurve.Balanced.Points;

    /// <summary>Nom de la courbe enregistrée chargée dans « Personnalisée » (null dès qu'un point change).</summary>
    private string? _customName;

    /// <summary>
    /// Courbe en cours telle que la page la connaît (null : pilote). Si elle ne correspond plus à celle qui tourne,
    /// c'est qu'elle a été lancée ou arrêtée ailleurs (menu de l'icône) : la page se remet d'accord en s'affichant.
    /// </summary>
    private string? _knownRunning;

    public IReadOnlyList<string> Choices { get; } = [AutoChoice, .. FanCurve.Presets.Select(p => p.Name), CustomChoice];

    public ObservableCollection<FanPointRow> Points { get; } = [];

    /// <summary>Courbes enregistrées (« Mes courbes »).</summary>
    public ObservableCollection<FanProfileRow> Profiles { get; } = [];
    [ObservableProperty] private bool hasProfiles;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))] private string profileName = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowsPoints))] [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))] private string selected = AutoChoice;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(StopCommand), nameof(SaveProfileCommand), nameof(ApplyProfileCommand), nameof(LoadProfileCommand), nameof(DeleteProfileCommand))] private bool isAvailable;
    [ObservableProperty] private bool isUnavailable;
    [ObservableProperty] private string rangeText = "";
    [ObservableProperty] private string statusText = "Pilote NVIDIA (automatique)";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StopCommand))] private bool isRunning;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMessage))] private string message = "";
    [ObservableProperty] private bool messageIsError;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(StopCommand), nameof(SaveProfileCommand), nameof(ApplyProfileCommand), nameof(LoadProfileCommand), nameof(DeleteProfileCommand))] private bool isBusy;

    public bool ShowsPoints => Selected != AutoChoice;
    public bool HasMessage => Message.Length > 0;

    public FanCurveViewModel()
    {
        FanCurveService.Changed += () => Application.Current?.Dispatcher.InvokeAsync(RefreshStatus);
        FanCurveService.Failed += error => Application.Current?.Dispatcher.InvokeAsync(
            () => Show($"Courbe arrêtée, ventilateurs rendus au pilote NVIDIA. {error}", error: true));
    }

    /// <summary>Page affichée : ventilateurs de la carte (une fois), courbe enregistrée, état en cours.</summary>
    public async Task LoadAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            var fans = await Task.Run(() => FanCurveService.Device);
            IsAvailable = fans is not null;
            IsUnavailable = !IsAvailable;
            if (fans is null) return;
            RangeText = $"{fans.FanCount} ventilateur{(fans.FanCount > 1 ? "s" : "")} · vitesse imposée de {fans.MinPercent} à {fans.MaxPercent} % " +
                        "(en dessous, seul le pilote peut les arrêter).";

            // Dernière courbe personnalisée (ou chargée depuis « Mes courbes ») : reprise dans « Personnalisée »
            var saved = await Task.Run(FanCurveStore.Load);
            if (saved is not null && !IsPreset(saved.Name))
            {
                _custom = saved.Points;
                _customName = saved.Name == CustomChoice ? null : saved.Name;
            }
            Selected = FanCurveService.Running is not { } running ? AutoChoice
                : IsPreset(running.Name) ? running.Name
                : CustomChoice;
            _knownRunning = FanCurveService.Running?.Name;
            if (FanCurveService.RecoveryNote is { } note) Show(note, FanCurveService.RecoveryFailed);
            await LoadProfilesAsync();
        }
        else if (FanCurveService.Running?.Name != _knownRunning)
        {
            FollowRunning();
            await LoadProfilesAsync();
        }
        RefreshStatus();
    }

    /// <summary>La courbe a changé hors de la page (menu de l'icône) : le choix affiché suit celle qui tourne.</summary>
    private void FollowRunning()
    {
        var running = FanCurveService.Running;
        _knownRunning = running?.Name;
        if (running is not null && !IsPreset(running.Name))
        {
            _custom = running.Points;
            _customName = running.Name == CustomChoice ? null : running.Name;
        }
        _filling = true;
        Selected = running is null ? AutoChoice : IsPreset(running.Name) ? running.Name : CustomChoice;
        _filling = false;
        if (running is not null) Fill(running.Points);
    }

    private static bool IsPreset(string name) => FanCurve.Presets.Any(p => p.Name == name);

    private async Task LoadProfilesAsync()
    {
        var list = await Task.Run(FanProfiles.All);
        Profiles.Clear();
        foreach (var profile in list) Profiles.Add(new FanProfileRow(profile));
        HasProfiles = Profiles.Count > 0;
        MarkRunning();
    }

    /// <summary>Repère « EN PLACE » : sur la courbe enregistrée qui tourne, s'il y en a une.</summary>
    private void MarkRunning()
    {
        var running = FanCurveService.Running;
        foreach (var row in Profiles) row.IsActive = row.Profile.IsRunning(running);
    }

    private void ClearNotes()
    {
        foreach (var row in Profiles) row.Say("");
    }

    partial void OnSelectedChanged(string value)
    {
        if (_filling) return;
        var points = value == CustomChoice ? _custom : FanCurve.Presets.FirstOrDefault(p => p.Name == value)?.Points;
        if (points is not null) Fill(points);
    }

    private void Fill(IReadOnlyList<FanCurvePoint> points)
    {
        _filling = true;
        foreach (var row in Points) row.PropertyChanged -= OnPointEdited;
        Points.Clear();
        foreach (var point in points)
        {
            var row = new FanPointRow(point);
            row.PropertyChanged += OnPointEdited;
            Points.Add(row);
        }
        _filling = false;
    }

    /// <summary>Un point d'un préréglage modifié : la courbe devient « Personnalisée », avec ses points tels quels.</summary>
    private void OnPointEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (_filling) return;
        _custom = Points.Select(r => r.ToPoint()).ToList();
        _customName = null;
        if (Selected == CustomChoice) return;
        _filling = true;
        Selected = CustomChoice;
        _filling = false;
    }

    private bool CanAct() => IsAvailable && !IsBusy;
    private bool CanStop() => CanAct() && IsRunning;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private Task Apply()
    {
        ClearNotes();
        return ApplySelectedAsync();
    }

    /// <summary>
    /// Pose la courbe affichée, après confirmation. Vrai : elle est en place ; faux : refusée (le message dit
    /// pourquoi) ; null : l'utilisateur a annulé.
    /// </summary>
    private async Task<bool?> ApplySelectedAsync()
    {
        if (Selected == AutoChoice)
        {
            await Stop();
            return true;
        }

        // Une courbe chargée depuis « Mes courbes » garde son nom tant qu'on n'y touche pas
        var name = Selected == CustomChoice ? _customName ?? CustomChoice : Selected;
        var curve = new FanCurve(name, Points.Select(r => r.ToPoint()).ToList());
        if (curve.Check() is { } error)
        {
            Show(error, error: true);
            return false;
        }

        var fans = FanCurveService.Device!;
        List<DialogRow> rows =
        [
            new($"Sous {curve.AutoBelowC} °C", To: "Pilote NVIDIA (arrêt au repos conservé)"),
            .. curve.Points.Select(p => new DialogRow($"{p.TemperatureC} °C", To: $"{Math.Clamp((uint)p.Percent, fans.MinPercent, fans.MaxPercent)} %")),
            new($"{FanCurveController.FullSpeedC} °C et plus", To: $"{fans.MaxPercent} %, quelle que soit la courbe")
        ];
        var warnings = new List<string>
        {
            "Aria pilote les ventilateurs tant qu'il est ouvert. S'il est fermé, le pilote NVIDIA reprend la main ; " +
            "s'il plante, les ventilateurs gardent leur dernière vitesse jusqu'au prochain lancement d'Aria (le pilote bride la carte si elle chauffe trop)."
        };
        if (!IsRunning && await Task.Run(fans.AnyManual))
            warnings.Add("Les ventilateurs sont déjà en manuel : un autre outil (MSI Afterburner, logiciel du fabricant…) les pilote peut-être. " +
                         "Fermez-le, sinon les deux se contrediront.");

        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: $"Courbe de ventilation « {curve.Name} »",
                ConfirmText: "Appliquer",
                Rows: rows,
                Warnings: warnings,
                Note: "« Rendre la main au pilote », ou quitter Aria, remet le réglage automatique de NVIDIA.")))
            return null;

        return await RunAsync(() => FanCurveService.Start(curve), $"Courbe « {curve.Name} » en place.");
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        await RunAsync(FanCurveService.Stop, "Ventilateurs rendus au pilote NVIDIA.");
        _filling = true;
        Selected = AutoChoice;
        _filling = false;
    }

    // ---- Mes courbes ----

    private bool CanSaveProfile() => CanAct() && ShowsPoints && ProfileName.Trim().Length > 0;

    /// <summary>Enregistre les points affichés sous un nom (sans rien appliquer).</summary>
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfile()
    {
        var name = ProfileName.Trim();
        var points = Points.Select(r => r.ToPoint()).ToList();
        if (new FanCurve(name, points).Check() is { } error)
        {
            Show(error, error: true);
            return;
        }
        if (await Task.Run(() => FanProfiles.Find(name)) is not null && !await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Remplacer la courbe",
                ConfirmText: "Remplacer",
                Message: $"Une courbe « {name} » est déjà enregistrée. La remplacer par celle-ci ?")))
            return;

        if (!await RunAsync(() => FanProfiles.Save(new SavedFanProfile(name, points, DateTime.Now)), $"Courbe « {name} » enregistrée.")) return;
        ProfileName = "";
        if (Selected == CustomChoice) _customName = name;
        await LoadProfilesAsync();
    }

    /// <summary>« Appliquer » d'une courbe enregistrée : elle est posée telle quelle, après la confirmation habituelle.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ApplyProfile(FanProfileRow row)
    {
        ClearNotes();
        Edit(row.Profile);
        switch (await ApplySelectedAsync())
        {
            case true:
                row.Say("En place : Aria pilote les ventilateurs avec cette courbe.");
                break;
            case false:
                row.Say(Message, error: true);
                break;
            default:
                row.Say("Rien n'a été appliqué. La courbe est seulement affichée dans les points, plus haut.");
                break;
        }
    }

    /// <summary>« Modifier » : met la courbe dans « Personnalisée » sans rien poser, pour en changer les points.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void LoadProfile(FanProfileRow row)
    {
        ClearNotes();
        Edit(row.Profile);
        row.Say("Affichée dans les points, plus haut : change-les, puis « Appliquer » sous la courbe. Rien n'est posé pour l'instant.");
    }

    /// <summary>Affiche une courbe enregistrée dans « Personnalisée », sous son nom.</summary>
    private void Edit(SavedFanProfile profile)
    {
        _custom = profile.Points;
        _filling = true;
        Selected = CustomChoice;
        _filling = false;
        Fill(profile.Points);
        _customName = profile.Name;
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task DeleteProfile(FanProfileRow row)
    {
        var profile = row.Profile;
        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Supprimer la courbe",
                ConfirmText: "Supprimer",
                Message: $"Supprimer la courbe enregistrée « {profile.Name} » ? Celle en place n'est pas touchée.",
                Icon: "Delete24")))
            return;
        await RunAsync(() => FanProfiles.Delete(profile), $"Courbe « {profile.Name} » supprimée.");
        await LoadProfilesAsync();
    }

    /// <summary>Lance l'action hors du fil de l'interface ; vrai si elle a réussi.</summary>
    private async Task<bool> RunAsync(Action action, string done)
    {
        IsBusy = true;
        try
        {
            await Task.Run(action);
            Show(done, error: false);
            return true;
        }
        catch (Exception e) when (e is GpuTuningException or IOException or UnauthorizedAccessException)
        {
            Show(e.Message, error: true);
            return false;
        }
        finally
        {
            IsBusy = false;
            _knownRunning = FanCurveService.Running?.Name;
            RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
        MarkRunning();
        IsRunning = FanCurveService.Running is not null;
        StatusText = FanCurveService.Running is not { } curve ? "Pilote NVIDIA (automatique)"
            : FanCurveService.Last is not { } s ? $"« {curve.Name} » · démarrage…"
            : $"« {curve.Name} » · {(s.TemperatureC is { } t ? $"{t} °C" : "température illisible")} · " +
              (s.Mode == FanMode.Auto ? "pilote (sous le seuil)" : $"{s.Percent} %");
    }

    private void Show(string text, bool error)
    {
        Message = text;
        MessageIsError = error;
    }
}
