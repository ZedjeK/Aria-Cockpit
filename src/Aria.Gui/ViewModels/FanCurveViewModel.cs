using System.Collections.ObjectModel;
using System.ComponentModel;
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

    public IReadOnlyList<string> Choices { get; } = [AutoChoice, .. FanCurve.Presets.Select(p => p.Name), CustomChoice];

    public ObservableCollection<FanPointRow> Points { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowsPoints))] private string selected = AutoChoice;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(StopCommand))] private bool isAvailable;
    [ObservableProperty] private bool isUnavailable;
    [ObservableProperty] private string rangeText = "";
    [ObservableProperty] private string statusText = "Pilote NVIDIA (automatique)";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StopCommand))] private bool isRunning;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasMessage))] private string message = "";
    [ObservableProperty] private bool messageIsError;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(StopCommand))] private bool isBusy;

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

            // Courbe personnalisée enregistrée : reprise dans « Personnalisée »
            var saved = await Task.Run(FanCurveStore.Load);
            if (saved is not null && FanCurve.Presets.All(p => p.Name != saved.Name)) _custom = saved.Points;
            Selected = FanCurveService.Running?.Name ?? AutoChoice;
            if (FanCurveService.RecoveryNote is { } note) Show(note, FanCurveService.RecoveryFailed);
        }
        RefreshStatus();
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
        if (Selected == CustomChoice) return;
        _filling = true;
        Selected = CustomChoice;
        _filling = false;
    }

    private bool CanAct() => IsAvailable && !IsBusy;
    private bool CanStop() => CanAct() && IsRunning;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Apply()
    {
        if (Selected == AutoChoice)
        {
            await Stop();
            return;
        }

        var curve = new FanCurve(Selected, Points.Select(r => r.ToPoint()).ToList());
        if (curve.Check() is { } error)
        {
            Show(error, error: true);
            return;
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
                Note: "« Rendre la main au pilote », ou la fermeture d'Aria, remet le réglage automatique de NVIDIA.")))
            return;

        await RunAsync(() => FanCurveService.Start(curve), $"Courbe « {curve.Name} » en place.");
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task Stop()
    {
        await RunAsync(FanCurveService.Stop, "Ventilateurs rendus au pilote NVIDIA.");
        _filling = true;
        Selected = AutoChoice;
        _filling = false;
    }

    private async Task RunAsync(Action action, string done)
    {
        IsBusy = true;
        try
        {
            await Task.Run(action);
            Show(done, error: false);
        }
        catch (GpuTuningException e)
        {
            Show(e.Message, error: true);
        }
        finally
        {
            IsBusy = false;
            RefreshStatus();
        }
    }

    private void RefreshStatus()
    {
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
