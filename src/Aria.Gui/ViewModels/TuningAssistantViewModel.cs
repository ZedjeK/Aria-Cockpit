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
    /// <summary>Recherche en cours ; null pendant la mesure de départ.</summary>
    private TuningSearch? _search;
    private bool _memoryPhase, _aborted;
    private CancellationTokenSource? _stop;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsActive), nameof(IsMeasuring), nameof(IsReview), nameof(IsMemoryPrompt), nameof(IsDone))]
    private AssistantPhase phase;

    [ObservableProperty] private string title = "";
    [ObservableProperty] private string instruction = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private bool isWaiting;
    [ObservableProperty] private string gameText = "";
    [ObservableProperty] private string headline = "";
    [ObservableProperty] private IReadOnlyList<StatTile> tiles = [];
    [ObservableProperty] private IReadOnlyList<FindingItem> incidents = [];
    [ObservableProperty] private bool hasIncidents;
    [ObservableProperty] private bool showQuestion;
    [ObservableProperty] private string questionText = "";
    [ObservableProperty] private string yesText = "";
    [ObservableProperty] private string noText = "";
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
        var (name, what, verdict) = goal switch
        {
            TuningGoal.Silence => ("Silence",
                "baisser la limite de puissance : grands pas (environ 10 %), puis pas moyens (environ 5 %) dès que vous trouvez la perte trop grande, puis un dernier affinage",
                "fluidité qui baisse trop → pas plus fin"),
            TuningGoal.Undervolt => ("Undervolt",
                "plafonner la carte à sa fréquence habituelle en jeu, puis augmenter le décalage GPU : +100 MHz par palier, +30 après le premier problème, puis un affinage de 15 MHz",
                "artefacts ou plantage → pas plus fin"),
            _ => ("Performance",
                "augmenter le décalage GPU sans plafond : +100 MHz par palier, +30 après le premier problème, puis un affinage de 15 MHz",
                "artefacts ou plantage → pas plus fin")
        };
        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: $"Assistant · {name}",
                ConfirmText: "Commencer",
                Message: $"L'assistant va {what}.",
                Rows:
                [
                    new("1. Mesure de départ", "réglage d'origine, 3 min de jeu"),
                    new("2. Un palier à la fois", "3 min de jeu chacun, même jeu, même scène"),
                    new("3. Votre verdict", verdict),
                    new("4. À la fin", "vous choisissez de garder ou d'annuler")
                ],
                Warnings: ["Un palier trop poussé peut faire planter le jeu ou le pilote (écran noir quelques secondes). Le matériel ne risque rien, et chaque palier s'efface au redémarrage."],
                Note: "Chaque mesure démarre dès qu'Aria voit le jeu en action, et un son retentit à la fin : revenez alors dans Aria (Alt+Tab).",
                IsRisky: true)))
            return;

        _goal = goal;
        _memoryPhase = false;
        _aborted = false;
        _search = null;
        var origin = GpuProfile.Origin ?? await Task.Run(() => GpuProfile.ReadCurrent(t));
        _start = TuningPlan.Start(origin);
        _lastStable = _start;
        if (await ApplyAsync(_start)) await MeasureAsync();
    }

    private async Task MeasureAsync()
    {
        if (await GpuTelemetry.OpenSharedAsync() is not { } gpu) return;
        var step = _search?.Current;
        Title = _search is null ? "MESURE DE DÉPART" : $"{(_memoryPhase ? "MÉMOIRE · " : "")}PALIER {_search.Tested + 1} · {_search.StageName}";
        var watch = _goal == TuningGoal.Silence && !_memoryPhase
            ? "Jugez la fluidité et le bruit."
            : "Surveillez bandes, scintillements ou couleurs bizarres.";
        Instruction = step is null
            ? $"Réglage d'origine en place ({_start.Describe()}). Lancez votre jeu : la mesure de 3 minutes démarre dès qu'Aria le voit en action. Restez ensuite dans la même scène pour les paliers suivants."
            : $"Palier en place : {step.Describe()}. Retournez dans le jeu, même scène : la mesure de 3 minutes démarre dès qu'Aria le voit en action. {watch}";
        Phase = AssistantPhase.Measuring;
        Progress = 0;
        IsWaiting = true;
        GameText = "";
        ProgressText = "EN ATTENTE D'UN JEU · LANCEZ-LE OU REPRENEZ LA PARTIE";

        // 1. Attendre que le jeu tourne (pas de chrono pendant le chargement ou dans le menu)
        string game;
        using (var wait = new CancellationTokenSource())
        {
            _stop = wait;
            try
            {
                game = await PerformanceSession.WaitForGameAsync(gpu, wait.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            finally
            {
                _stop = null;
            }
        }
        if (_aborted) return;
        IsWaiting = false;
        GameText = $"JEU DÉTECTÉ : {game.ToUpperInvariant()}";
        UpdateProgress(TimeSpan.Zero);

        // 2. Mesurer
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
        var isBaseline = _search is null;
        Incidents = report.Findings.Where(f => report.Unstable && f.Severity == Severity.Critical)
            .Select(f => new FindingItem(f.Title, f.Detail, f.Advice, [], f.Severity)).ToList();
        HasIncidents = Incidents.Count > 0;
        Tiles = BuildTiles(report, isBaseline ? null : _baseline);

        if (isBaseline && report.Conclusive && !report.Unstable)
        {
            // Mesure de départ réussie : on enchaîne directement sur le premier palier
            _baseline = report;
            _lastStableReport = report;
            if (tuner() is not { } t) return;
            if (_goal == TuningGoal.Undervolt && TuningPlan.UndervoltCap(report.Stats, t.Limits) is null)
            {
                _ = FinishAsync("Fréquence habituelle en jeu introuvable dans la mesure de départ : impossible de placer le plafond de l'undervolt.");
                return;
            }
            _search = _goal switch
            {
                TuningGoal.Silence => TuningSearch.Silence(_start, t.Limits),
                TuningGoal.Undervolt => TuningSearch.Core(_goal, _start, t.Limits, TuningPlan.UndervoltCap(report.Stats, t.Limits)!.Value),
                _ => TuningSearch.Core(_goal, _start, t.Limits, 0)
            };
            if (_search.Finished)
            {
                _ = FinishAsync("Aucun palier à proposer pour cet objectif avec ce réglage de départ.");
                return;
            }
            _ = NextStepAsync();
            return;
        }

        var silence = _search?.Goal == TuningGoal.Silence;
        QuestionText = silence
            ? "Le jeu vous paraît-il toujours aussi fluide, et la baisse de fréquence acceptable ?"
            : "Pendant ce palier, avez-vous vu des bandes, des scintillements, des couleurs bizarres, ou le jeu a-t-il planté ?";
        YesText = silence ? "OUI, CONTINUER" : "NON, TOUT ÉTAIT NORMAL";
        NoText = silence ? "NON, C'EST TROP" : "OUI, J'AI VU UN PROBLÈME";
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
        if (_search is { Finished: false } search && await ApplyAsync(search.Current)) await MeasureAsync();
    }

    /// <summary>Palier jugé bon : la recherche continue (même pas, ou fin si la borne est atteinte).</summary>
    [RelayCommand]
    private async Task Stable()
    {
        if (_search is null) return;
        _search.Report(true);
        _lastStable = _search.LastStable;
        _lastStableReport = _current;
        await ContinueAsync();
    }

    /// <summary>Artefacts, plantage, perte jugée trop grande : la recherche repart du dernier palier stable, avec un pas plus fin.</summary>
    [RelayCommand]
    private async Task Unstable()
    {
        if (_search is null) return;
        _search.Report(false);
        await ContinueAsync();
    }

    private async Task ContinueAsync()
    {
        if (_search is { Finished: false }) await NextStepAsync();
        else if (await ApplyAsync(_lastStable)) await EndPhaseAsync();
    }

    /// <summary>« Garder ce palier » : terminer ici avec le palier en place.</summary>
    [RelayCommand]
    private Task KeepThis()
    {
        if (_search is not null)
        {
            _lastStable = _search.Current;
            _lastStableReport = _current;
        }
        return FinishAsync(null);
    }

    /// <summary>Refaire la mesure. Un plantage du pilote efface le réglage : le palier est donc réappliqué.</summary>
    [RelayCommand]
    private Task Retry() => _search is null ? MeasureAsync() : NextStepAsync();

    [RelayCommand]
    private async Task TestMemory()
    {
        if (tuner() is not { } t) return;
        _memoryPhase = true;
        _search = TuningSearch.Memory(_lastStable, t.Limits);
        if (_search.Finished) await FinishAsync(null);
        else await NextStepAsync();
    }

    [RelayCommand]
    private Task SkipMemory() => FinishAsync(null);

    private Task EndPhaseAsync()
    {
        if (!_memoryPhase && _goal != TuningGoal.Silence && tuner() is { } t && !TuningSearch.Memory(_lastStable, t.Limits).Finished)
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
                Warnings: ["La tâche planifiée « Aria Cockpit\\Profil GPU » réappliquera ce réglage à chaque ouverture de session, avec les droits administrateur, sans fenêtre."],
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
            var exe = Environment.ProcessPath ?? "Aria-Cockpit.exe";
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
