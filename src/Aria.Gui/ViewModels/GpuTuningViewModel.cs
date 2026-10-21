using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Nvidia;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

/// <summary>
/// Onglet Réglage GPU : 4 curseurs, appliqués d'un bloc. « Essayer » tient jusqu'au redémarrage,
/// « Garder au démarrage » passe par la tâche planifiée. Rien n'est appliqué sans confirmation.
/// </summary>
public sealed partial class GpuTuningViewModel : ObservableObject
{
    /// <summary>Bornes d'Aria, plus prudentes que celles de la carte (qui accepte ±1000 MHz).</summary>
    private const int CoreOffsetLimit = 300, MemoryOffsetMin = -500, MemoryOffsetMax = 1500, MinCapMHz = 1200;

    private GpuTuner? _tuner;
    private bool _loaded;

    public GpuTelemetryViewModel Telemetry { get; } = new();

    /// <summary>Assistant guidé : pendant qu'il travaille, le réglage manuel est en pause.</summary>
    public TuningAssistantViewModel Assistant { get; }

    public GpuTuningViewModel()
    {
        Assistant = new TuningAssistantViewModel(() => _tuner, () => RefreshAsync(resetSliders: true));
        Assistant.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(TuningAssistantViewModel.IsActive)) return;
            ApplyCommand.NotifyCanExecuteChanged();
            RevertCommand.NotifyCanExecuteChanged();
            ResetSlidersCommand.NotifyCanExecuteChanged();
        };
    }

    [ObservableProperty] private bool isAvailable;
    [ObservableProperty] private bool isUnavailable;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(RevertCommand), nameof(ResetSlidersCommand))] private bool isBusy;

    // ---- Curseurs ----
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(PowerText), nameof(HasChanges))] private double powerWatts;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CapText), nameof(HasChanges))] private bool capEnabled;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CapText), nameof(HasChanges))] private double capMHz;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CoreText), nameof(HasChanges))] private double coreOffset;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(MemoryText), nameof(HasChanges))] private double memoryOffset;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasChanges))] private bool atStartup;

    // ---- Plages des curseurs ----
    [ObservableProperty] private double powerMin, powerMax = 1, capMin = MinCapMHz, capMax = 3000, coreMin, coreMax, memoryMin, memoryMax;

    // ---- État ----
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasChanges))] private GpuTuningState? current;
    [ObservableProperty] private string currentText = "";
    [ObservableProperty] private string originText = "";
    [ObservableProperty] private bool hasOrigin;
    [ObservableProperty] private string message = "";
    [ObservableProperty] private bool hasMessage;
    [ObservableProperty] private bool messageIsError;

    // ---- Garde-fous ----
    [ObservableProperty] private string crashText = "";
    [ObservableProperty] private bool hasCrashes;
    [ObservableProperty] private string taskText = "";
    [ObservableProperty] private bool hasTaskProblem;

    private uint _defaultPower;

    public string PowerText => _defaultPower == 0 ? $"{PowerWatts:0} W" : $"{PowerWatts:0} W · {PowerWatts * 100_000 / _defaultPower:0} % de la valeur d'usine";
    public string CapText => CapEnabled ? $"{CapMHz:0} MHz" : "Aucun plafond";
    public string CoreText => $"{CoreOffset:+0;-0;0} MHz";
    public string MemoryText => $"{MemoryOffset:+0;-0;0} MHz";

    /// <summary>Le réglage des curseurs diffère de celui en place.</summary>
    public bool HasChanges => Current is { } c && (!Target().SameTuning(c) || AtStartup != c.AtStartup);

    /// <summary>Page affichée : lecture de la carte (une fois), télémétrie, garde-fous.</summary>
    public async void OnShown()
    {
        Telemetry.Start();
        if (!_loaded)
        {
            _loaded = true;
            _tuner = await Task.Run(() => GpuProfile.Tuner);
            IsAvailable = _tuner is not null;
            IsUnavailable = !IsAvailable;
            if (_tuner is null) return;
            SetRanges(_tuner.Limits);
        }
        if (_tuner is not null) await RefreshAsync(resetSliders: Current is null || !HasChanges);
    }

    public void OnHidden() => Telemetry.Stop();

    private void SetRanges(GpuTuningLimits l)
    {
        _defaultPower = l.DefaultPowerMilliwatts;
        PowerMin = Math.Ceiling(l.MinPowerMilliwatts / 1000.0);
        PowerMax = Math.Floor(l.MaxPowerMilliwatts / 1000.0);
        CapMin = Math.Max(MinCapMHz, l.MinClockMHz + 15);
        CapMax = l.MaxClockMHz;
        CoreMin = Math.Max(-CoreOffsetLimit, l.MinCoreOffsetMHz);
        CoreMax = Math.Min(CoreOffsetLimit, l.MaxCoreOffsetMHz);
        MemoryMin = Math.Max(MemoryOffsetMin, l.MinMemoryOffsetMHz);
        MemoryMax = Math.Min(MemoryOffsetMax, l.MaxMemoryOffsetMHz);
    }

    private async Task RefreshAsync(bool resetSliders)
    {
        var tuner = _tuner!;
        var (state, origin, saved, crashes, taskCommand) = await Task.Run(() =>
        {
            var s = GpuProfile.ReadCurrent(tuner);
            var file = GpuProfile.Load();
            var crashList = file is null ? [] : GpuProfile.DriverCrashesSince(file.AppliedAt);
            var command = s.AtStartup ? StartupTask.Command() : null;
            return (s, GpuProfile.Origin, file, crashList, command);
        });

        Current = state;
        CurrentText = state.Describe();
        HasOrigin = origin is not null;
        OriginText = origin?.Describe() ?? "";
        if (resetSliders) LoadSliders(state);

        // Plantages du pilote depuis le dernier réglage d'Aria
        HasCrashes = saved is not null && crashes.Count > 0;
        CrashText = HasCrashes
            ? $"Le pilote NVIDIA a planté {crashes.Count} fois depuis le réglage du {saved!.AppliedAt:dd/MM à HH:mm} " +
              $"(dernière fois le {crashes[^1]:dd/MM à HH:mm}). Ce réglage est probablement instable : revenez au réglage d'origine ou réduisez les décalages."
            : "";

        // Tâche de démarrage absente ou pointant vers un ancien emplacement d'Aria
        var exe = Environment.ProcessPath ?? "";
        var wanted = saved is not null && GpuTuningState.TryParse(saved.Current, out var keep) && keep.AtStartup;
        HasTaskProblem = wanted && (!state.AtStartup || !string.Equals(taskCommand, exe, StringComparison.OrdinalIgnoreCase));
        TaskText = !HasTaskProblem ? ""
            : !state.AtStartup
                ? "Le réglage devait être réappliqué au démarrage, mais la tâche planifiée « Aria\\Profil GPU » a disparu."
                : $"La tâche de démarrage lance encore Aria depuis « {taskCommand} », un ancien emplacement.";
    }

    private void LoadSliders(GpuTuningState s)
    {
        PowerWatts = Math.Round(s.PowerLimitWatts);
        CapEnabled = s.MaxClockMHz != 0;
        CapMHz = s.MaxClockMHz != 0 ? s.MaxClockMHz : CapMax;
        CoreOffset = s.CoreOffsetMHz;
        MemoryOffset = s.MemoryOffsetMHz;
        AtStartup = s.AtStartup;
    }

    private GpuTuningState Target() => new(
        (uint)Math.Round(PowerWatts) * 1000,
        CapEnabled ? (uint)Math.Round(CapMHz) : 0,
        (int)Math.Round(CoreOffset),
        (int)Math.Round(MemoryOffset),
        AtStartup);

    private bool CanAct() => IsAvailable && !IsBusy && !Assistant.IsActive;
    private bool CanApply() => CanAct() && HasChanges;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName == nameof(HasChanges)) ApplyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        var target = Target();
        if (Current is null || _tuner is null) return;

        var warnings = new List<string>
        {
            "Un réglage trop poussé peut faire planter un jeu ou le pilote (écran noir quelques secondes). Le matériel ne risque rien : le pilote garde la main sur la tension."
        };
        if (target.MemoryOffsetMHz > 1000)
            warnings.Add("Mémoire au-delà de +1000 MHz : la GDDR6X corrige ses propres erreurs, une mémoire trop poussée ne plante pas mais devient plus lente.");
        if (target.AtStartup && !Current.AtStartup)
            warnings.Add("« Garder au démarrage » crée la tâche planifiée « Aria\\Profil GPU » : Aria se lancera en silence, avec les droits administrateur, à chaque ouverture de session pour réappliquer ce réglage.");

        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Appliquer le réglage GPU",
                ConfirmText: "Appliquer",
                Rows:
                [
                    new("Limite de puissance", $"{Current.PowerLimitWatts:0} W", $"{target.PowerLimitWatts:0} W"),
                    new("Plafond de fréquence", Cap(Current), Cap(target)),
                    new("Décalage GPU", $"{Current.CoreOffsetMHz:+0;-0;0} MHz", $"{target.CoreOffsetMHz:+0;-0;0} MHz"),
                    new("Décalage mémoire", $"{Current.MemoryOffsetMHz:+0;-0;0} MHz", $"{target.MemoryOffsetMHz:+0;-0;0} MHz"),
                    new("Au démarrage de Windows", Current.AtStartup ? "Oui" : "Non", target.AtStartup ? "Oui" : "Non")
                ],
                Warnings: warnings,
                Note: target.AtStartup
                    ? "Revenir d'origine (ici ou dans Restauration) supprime la tâche et remet tout comme avant."
                    : "Sans « Garder au démarrage », un redémarrage remet la carte comme avant.",
                IsRisky: true)))
            return;

        await RunAsync(() => GpuProfile.Apply(_tuner, target, ExePath()), "Réglage appliqué.");
    }

    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Revert()
    {
        if (_tuner is null) return;
        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Revenir d'origine",
                ConfirmText: "Revenir d'origine",
                Message: OriginText.Length > 0 ? $"La carte retrouve son réglage d'avant Aria : {OriginText}." : "La carte retrouve son réglage d'avant Aria.",
                Note: "La tâche de démarrage « Aria\\Profil GPU » est supprimée si elle existe.")))
            return;
        await RunAsync(() => GpuProfile.RevertToOrigin(_tuner, ExePath()), "Réglage d'origine remis.");
    }

    /// <summary>Remet les curseurs sur le réglage en place, sans rien appliquer.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void ResetSliders()
    {
        if (Current is { } c) LoadSliders(c);
    }

    /// <summary>Recrée la tâche de démarrage vers cet Aria.exe (clic de l'utilisateur).</summary>
    [RelayCommand]
    private Task FixTask() => RunAsync(() => StartupTask.Create(ExePath()), "Tâche de démarrage mise à jour.");

    private async Task RunAsync(Action action, string success)
    {
        IsBusy = true;
        try
        {
            await Task.Run(action);
            Show(success, error: false);
        }
        catch (Exception e) when (e is GpuTuningException or IOException or UnauthorizedAccessException)
        {
            Show(e.Message, error: true);
        }
        finally
        {
            await RefreshAsync(resetSliders: true);
            IsBusy = false;
        }
    }

    private void Show(string text, bool error)
    {
        Message = text;
        MessageIsError = error;
        HasMessage = true;
    }

    private static string Cap(GpuTuningState s) => s.MaxClockMHz == 0 ? "Aucun" : $"{s.MaxClockMHz} MHz";

    private static string ExePath() => Environment.ProcessPath ?? "Aria.exe";
}
