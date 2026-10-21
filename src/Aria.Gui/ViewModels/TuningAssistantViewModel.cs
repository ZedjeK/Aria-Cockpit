using System.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Models;
using Aria.Core.Nvidia;
using Aria.Core.Performance;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

public enum AssistantPhase { Idle, Measuring, Review, MemoryPrompt, Done }

/// <summary>
/// Assistant guidé de l'onglet Réglage GPU : une mesure de départ en jeu, puis un palier à la fois.
/// Chaque palier est appliqué jusqu'au redémarrage seulement, mesuré pendant 3 minutes de jeu, puis jugé :
/// plantage détecté ou artefacts signalés → retour au dernier palier stable. Rien n'est gardé sans le choix de l'utilisateur.
/// </summary>
public sealed partial class TuningAssistantViewModel(Func<GpuTuner?> tuner, Func<Task> refresh) : ObservableObject
{
    public static readonly TimeSpan MeasureDuration = TimeSpan.FromMinutes(3);

    private TuningGoal _goal;
    private GpuTuningState _start = new(0, 0, 0, 0, false);
    private GpuTuningState _lastStable = new(0, 0, 0, 0, false);
    private PerformanceReport? _baseline, _lastStableReport, _current;
    private IReadOnlyList<GpuTuningState> _steps = [];
    private int _index = -1;
    private bool _memoryPhase, _aborted;
    private CancellationTokenSource? _stop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsActive), nameof(IsMeasuring), nameof(IsReview), nameof(IsMemoryPrompt), nameof(IsDone))]
    private AssistantPhase phase;

    [ObservableProperty] private string title = "";
    [ObservableProperty] private string instruction = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string headline = "";
    [ObservableProperty] private IReadOnlyList<StatTile> tiles = [];
    [ObservableProperty] private IReadOnlyList<FindingItem> incidents = [];
    [ObservableProperty] private bool hasIncidents;
    [ObservableProperty] private bool showQuestion;
    [ObservableProperty] private bool showRetry;
    [ObservableProperty] private bool showGoBack;
    [ObservableProperty] private string doneText = "";
    [ObservableProperty] private bool doneChanged;

    public bool IsIdle => Phase == AssistantPhase.Idle;
    public bool IsActive => Phase != AssistantPhase.Idle;
    public bool IsMeasuring => Phase == AssistantPhase.Measuring;
    public bool IsReview => Phase == AssistantPhase.Review;
    public bool IsMemoryPrompt => Phase == AssistantPhase.MemoryPrompt;
    public bool IsDone => Phase == AssistantPhase.Done;

    [RelayCommand] private Task StartSilence() => Begin(TuningGoal.Silence);
    [RelayCommand] private Task StartUndervolt() => Begin(TuningGoal.Undervolt);
    [RelayCommand] private Task StartPerformance() => Begin(TuningGoal.Performance);

    private async Task Begin(TuningGoal goal)
    {
        if (tuner() is not { } t) return;
        var (name, what) = goal switch
        {
            TuningGoal.Silence => ("Silence", "baisser la limite de puissance par paliers (90 %, 85 %, 80 %…) pour réduire bruit et chauffe"),
            TuningGoal.Undervolt => ("Undervolt", "plafonner la carte à sa fréquence habituelle en jeu, puis augmenter le décalage GPU par paliers de 30 MHz : même fréquence, moins de watts"),
            _ => ("Performance", "augmenter le décalage GPU par paliers de 30 MHz, sans plafond")
        };
        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: $"Assistant · {name}",
                ConfirmText: "Commencer",
                Message: $"L'assistant va {what}.",
                Rows:
                [
                    new("1. Mesure de départ", "réglage d'origine, 3 min de jeu"),
                    new("2. Un palier à la fois", "3 min de jeu chacun, même jeu, même scène"),
                    new("3. Votre verdict", "artefacts ou plantage → retour au palier stable"),
                    new("4. À la fin", "vous choisissez de garder ou d'annuler")
                ],
                Warnings: ["Un palier trop poussé peut faire planter le jeu ou le pilote (écran noir quelques secondes). Le matériel ne risque rien, et chaque palier s'efface au redémarrage."],
                Note: "Aria émet un son à la fin de chaque mesure : revenez alors dans Aria (Alt+Tab).",
                IsRisky: true)))
            return;

        _goal = goal;
        _memoryPhase = false;
        _aborted = false;
        var origin = GpuProfile.Origin ?? await Task.Run(() => GpuProfile.ReadCurrent(t));
        _start = TuningPlan.Start(origin);
        _lastStable = _start;
        _index = -1;
        if (await ApplyAsync(_start)) await MeasureAsync();
    }

    private async Task MeasureAsync()
    {
        if (await GpuTelemetry.OpenSharedAsync() is not { } gpu) return;
        var step = _index < 0 ? null : _steps[_index];
        Title = step is null ? "MESURE DE DÉPART" : $"{(_memoryPhase ? "MÉMOIRE · " : "")}PALIER {_index + 1}/{_steps.Count}";
        Instruction = step is null
            ? $"Réglage d'origine en place ({_start.Describe()}). Lancez votre jeu et jouez normalement pendant 3 minutes, toujours dans la même scène pour les paliers suivants."
            : $"Palier en place : {step.Describe()}. Retournez dans le jeu, même scène, pendant 3 minutes. Surveillez bandes, scintillements ou couleurs bizarres.";
        Phase = AssistantPhase.Measuring;
        Progress = 0;
        UpdateProgress(TimeSpan.Zero);

        _stop = new CancellationTokenSource();
        PerformanceReport report;
        try
        {
            report = await PerformanceSession.RunAsync(gpu, MeasureDuration, new Progress<TimeSpan>(UpdateProgress), _stop.Token);
        }
        finally
        {
            _stop.Dispose();
            _stop = null;
        }
        if (_aborted) return;
        SystemSounds.Asterisk.Play();
        _current = report;
        Review(report);
    }

    private void UpdateProgress(TimeSpan elapsed)
    {
        Progress = Math.Clamp(elapsed / MeasureDuration, 0, 1);
        var left = MeasureDuration - elapsed;
        ProgressText = $"MESURE EN COURS · {(left < TimeSpan.Zero ? TimeSpan.Zero : left):m\\:ss} RESTANTES";
    }

    private void Review(PerformanceReport report)
    {
        var isBaseline = _index < 0;
        Incidents = report.Findings.Where(f => report.Unstable && f.Severity == Severity.Critical)
            .Select(f => new FindingItem(f.Title, f.Detail, f.Advice, [], f.Severity)).ToList();
        HasIncidents = Incidents.Count > 0;
        Tiles = BuildTiles(report, isBaseline ? null : _baseline);

        if (isBaseline && report.Conclusive && !report.Unstable)
        {
            // Mesure de départ réussie : on enchaîne directement sur le premier palier
            _baseline = report;
            _lastStableReport = report;
            _steps = tuner() is { } t ? TuningPlan.Steps(_goal, _start, t.Limits, report.Stats) : [];
            if (_steps.Count == 0)
            {
                _ = FinishAsync("Aucun palier à proposer pour cet objectif avec ce réglage de départ.");
                return;
            }
            _index = 0;
            _ = NextStepAsync();
            return;
        }

        Headline = report.Unstable ? "Plantage détecté pendant ce palier"
            : !report.Conclusive ? "Mesure non concluante : la carte n'a pas assez travaillé"
            : report.Headline;
        ShowQuestion = !isBaseline && report.Conclusive && !report.Unstable;
        ShowRetry = !report.Conclusive || report.Unstable;
        ShowGoBack = !isBaseline && (report.Unstable || !report.Conclusive);
        Phase = AssistantPhase.Review;
    }

    private async Task NextStepAsync()
    {
        if (await ApplyAsync(_steps[_index])) await MeasureAsync();
    }

    /// <summary>« Non, tout était normal » : ce palier est stable, on passe au suivant.</summary>
    [RelayCommand]
    private async Task Stable()
    {
        _lastStable = _steps[_index];
        _lastStableReport = _current;
        _index++;
        if (_index < _steps.Count) await NextStepAsync();
        else await EndPhaseAsync();
    }

    /// <summary>Artefacts, plantage, ou mesure ratée : retour au dernier palier stable.</summary>
    [RelayCommand]
    private async Task GoBack()
    {
        if (await ApplyAsync(_lastStable)) await EndPhaseAsync();
    }

    /// <summary>« Garder ce palier » : terminer ici avec le palier en place.</summary>
    [RelayCommand]
    private Task KeepThis()
    {
        _lastStable = _steps[_index];
        _lastStableReport = _current;
        return FinishAsync(null);
    }

    /// <summary>Refaire la mesure. Un plantage du pilote efface le réglage : le palier est donc réappliqué.</summary>
    [RelayCommand]
    private Task Retry() => _index < 0 ? MeasureAsync() : NextStepAsync();

    [RelayCommand]
    private async Task TestMemory()
    {
        if (tuner() is not { } t) return;
        _memoryPhase = true;
        _steps = TuningPlan.MemorySteps(_lastStable, t.Limits);
        _index = 0;
        await NextStepAsync();
    }

    [RelayCommand]
    private Task SkipMemory() => FinishAsync(null);

    private Task EndPhaseAsync()
    {
        if (!_memoryPhase && _goal != TuningGoal.Silence && tuner() is { } t && TuningPlan.MemorySteps(_lastStable, t.Limits).Count > 0)
        {
            Title = "MÉMOIRE VIDÉO (FACULTATIF)";
            Instruction = $"Réglage stable retenu : {_lastStable.Describe()}. Tester aussi la mémoire, par paliers de +250 MHz jusqu'à +1000 MHz ? " +
                          "Gain modeste ; une mémoire trop poussée peut afficher des artefacts ou devenir plus lente sans planter.";
            Phase = AssistantPhase.MemoryPrompt;
            return Task.CompletedTask;
        }
        return FinishAsync(null);
    }

    private async Task FinishAsync(string? note)
    {
        await ApplyAsync(_lastStable);
        var changed = !_lastStable.SameTuning(_start);
        DoneChanged = changed;
        Title = "TERMINÉ";
        DoneText = note
                   ?? (changed
                       ? $"Meilleur réglage stable : {_lastStable.Describe()}. Il est en place jusqu'au prochain redémarrage."
                       : "Aucun palier n'a tenu : la carte est revenue à son réglage de départ.");
        Tiles = changed && _lastStableReport is { } r ? BuildTiles(r, _baseline) : [];
        Phase = AssistantPhase.Done;
    }

    [RelayCommand]
    private async Task KeepAtStartup()
    {
        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Garder au démarrage",
                ConfirmText: "Garder",
                Message: $"Réglage gardé : {_lastStable.Describe()}.",
                Warnings: ["La tâche planifiée « Aria\\Profil GPU » réappliquera ce réglage à chaque ouverture de session, avec les droits administrateur, sans fenêtre."],
                Note: "Revenir d'origine (onglet Réglage GPU ou Restauration) supprime la tâche.")))
            return;
        if (await ApplyAsync(_lastStable with { AtStartup = true })) Phase = AssistantPhase.Idle;
    }

    [RelayCommand]
    private void KeepUntilReboot() => Phase = AssistantPhase.Idle;

    /// <summary>Tout annuler : retour au réglage de départ.</summary>
    [RelayCommand]
    private async Task CancelAll()
    {
        _aborted = true;
        _stop?.Cancel();
        await ApplyAsync(_start);
        Phase = AssistantPhase.Idle;
    }

    /// <summary>Arrêter la mesure maintenant : le verdict porte sur ce qui a été enregistré.</summary>
    [RelayCommand]
    private void StopMeasure() => _stop?.Cancel();

    private async Task<bool> ApplyAsync(GpuTuningState state)
    {
        if (tuner() is not { } t) return false;
        try
        {
            var exe = Environment.ProcessPath ?? "Aria.exe";
            await Task.Run(() => GpuProfile.Apply(t, state, exe));
            return true;
        }
        catch (GpuTuningException e)
        {
            _aborted = true;
            Title = "ASSISTANT INTERROMPU";
            DoneText = $"Le pilote a refusé un réglage : {e.Message}";
            DoneChanged = false;
            Tiles = [];
            Phase = AssistantPhase.Done;
            return false;
        }
        finally
        {
            await refresh();
        }
    }

    /// <summary>Chiffres clés d'une mesure, avec ceux de la mesure de départ pour comparer.</summary>
    private static IReadOnlyList<StatTile> BuildTiles(PerformanceReport r, PerformanceReport? baseline)
    {
        if (!r.Conclusive) return [];
        var s = r.Stats;
        var b = baseline?.Stats;
        string Before(Func<PerformanceStats, double?> pick, string unit) =>
            b is null || pick(b) is not { } before || pick(s) is not { } now ? "" : $"départ {before:0} {unit} ({TuningPlan.Delta(now, before, unit)})";
        return
        [
            new("FRÉQUENCE MOY.", $"{s.AvgClock:0}", "MHz", Before(x => x.AvgClock, "MHz")),
            new("CONSO. MOY.", s.AvgPower is { } p ? $"{p:0}" : "–", "W", Before(x => x.AvgPower, "W")),
            new("TEMPÉRATURE MAX", $"{s.MaxTemp}", "°C", Before(x => x.MaxTemp, "°C")),
            new("CHARGE GPU MOY.", $"{s.AvgLoad:0}", "%", Before(x => x.AvgLoad, "%"))
        ];
    }
}
