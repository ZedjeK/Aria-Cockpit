using System.IO;
using System.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Bench;
using Aria.Core.Models;
using Aria.Core.Nvidia;
using Aria.Core.Performance;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

public enum AssistantPhase { Idle, Preflight, Measuring, Review, MemoryPrompt, Done }

/// <summary>
/// Assistant guidé de l'onglet Réglage GPU. Contrôle avant mesure (overclock de NVIDIA App), puis Équilibre en premier :
/// mesure de départ aux valeurs d'usine, puis undervolt (plafond + décalage croissant). Son résultat est enregistré et débloque
/// Économie (plafond plus bas) et Performance (courbe d'usine à puissance maximale + décalage croissant), comparés à la même mesure.
/// Chaque palier est appliqué jusqu'au redémarrage seulement ; rien n'est gardé sans le choix de l'utilisateur.
/// </summary>
public sealed partial class TuningAssistantViewModel(Func<GpuTuner?> tuner, Func<Task> refresh) : ObservableObject
{
    public static readonly TimeSpan MeasureDuration = TimeSpan.FromMinutes(3);
    /// <summary>Validation finale facultative : un réglage peut tenir 3 minutes et lâcher au bout d'un quart d'heure.</summary>
    public static readonly TimeSpan ValidationDuration = TimeSpan.FromMinutes(15);

    /// <summary>Ce que mesure l'étape en cours.</summary>
    private enum Step { Baseline, PerformanceBase, Search }

    private TuningGoal _goal;
    private Step _step;
    /// <summary>Réglage de la carte au lancement de l'assistant : « Tout annuler » y revient.</summary>
    private GpuTuningState _before = new(0, 0, 0, 0, false);
    /// <summary>Point de départ des paliers (origine pour Équilibre, résultat d'Équilibre pour les deux autres).</summary>
    private GpuTuningState _start = new(0, 0, 0, 0, false);
    private GpuTuningState _lastStable = new(0, 0, 0, 0, false);
    /// <summary>Mesure de référence : celle du départ d'Équilibre, commune aux trois modes.</summary>
    private PerformanceStats? _baseline;
    private string? _baselineGame;
    private PerformanceReport? _lastStableReport, _current;
    /// <summary>FPS du banc pour le dernier réglage stable (affichés à la fin).</summary>
    private double? _lastStableFps;
    private TuningSearch? _search;
    private bool _memoryPhase, _aborted, _validating;
    private TimeSpan _duration = MeasureDuration;
    private CancellationTokenSource? _stop;

    /// <summary>Qualité du banc d'essai pour l'assistant : la plus lourde, pour charger la carte à fond.</summary>
    public const string BenchQuality = "ultra";
    /// <summary>Mesures faites avec le banc d'essai (fixé au début du mode).</summary>
    private bool _benchMode;
    private int _benchDefinition = 1080;
    /// <summary>FPS moyens du banc à la mesure de départ, pour comparer chaque palier.</summary>
    private double? _baselineFps;
    private BenchResult? _bench;
    /// <summary>Contrôles de stabilité (calcul vérifié, mémoire vidéo) : désactivés si l'auto-contrôle du début a échoué.</summary>
    private bool _checks;
    /// <summary>Contrôle arrêté avant la fin ou impossible : le palier n'est pas jugé (titre affiché), sinon null.</summary>
    private string? _checksCut;
    /// <summary>Contrôle de stabilité du dernier réglage stable (tuiles de fin).</summary>
    private BenchStability? _lastStableStability;
    /// <summary>Auto-contrôle avant la première mesure : le temps de retrouver les erreurs injectées exprès.</summary>
    public static readonly TimeSpan SelfCheckDuration = TimeSpan.FromSeconds(20);
    /// <summary>Calcul vérifié à pleine puissance après le rendu de chaque palier qui touche au cœur.</summary>
    public static readonly TimeSpan ComputeCheckDuration = TimeSpan.FromSeconds(60);
    /// <summary>Calcul seul (un tiers du temps), puis mémoire vidéo seule, après le rendu de chaque palier mémoire.</summary>
    public static readonly TimeSpan MemoryCheckDuration = TimeSpan.FromSeconds(90);
    /// <summary>Contrôles ajoutés aux 15 minutes de validation (calcul, et mémoire si elle est poussée).</summary>
    public static readonly TimeSpan ValidationCheckDuration = TimeSpan.FromMinutes(2);
    private static string CheckOutput => Path.Combine(Path.GetTempPath(), "Aria-Cockpit", "controle-calcul.json");
    private static string MemoryOutput => Path.Combine(Path.GetTempPath(), "Aria-Cockpit", "controle-memoire.json");
    /// <summary>Journal du dernier palier mémoire stable : le suivant doit aller plus vite que lui.</summary>
    private static string MemoryPrevious => Path.Combine(Path.GetTempPath(), "Aria-Cockpit", "controle-memoire-precedent.json");
    /// <summary>Résultat du palier précédent, rappelé pendant la mesure suivante (verdict automatique).</summary>
    private string _previousStep = "";
    /// <summary>Mode demandé, mis en attente par le contrôle avant mesure.</summary>
    private TuningGoal _pendingGoal;
    /// <summary>Overclock de NVIDIA App illisible : rappelé en tête de la première mesure.</summary>
    private string _nvidiaAppNote = "";
    private static string BenchOutput => Path.Combine(Path.GetTempPath(), "Aria-Cockpit", "banc-assistant.json");

    /// <summary>Aria-Bench.exe est livré à côté d'Aria Cockpit.</summary>
    public bool BenchAvailable { get; } = BenchRunner.IsAvailable;

    /// <summary>Source des mesures : le banc d'essai (par défaut s'il est là) ou le jeu de l'utilisateur.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UseGame))] private bool useBench = BenchRunner.IsAvailable;

    public bool UseGame
    {
        get => !UseBench;
        set => UseBench = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsActive), nameof(IsPreflight), nameof(IsMeasuring), nameof(IsReview), nameof(IsMemoryPrompt), nameof(IsDone))]
    [NotifyCanExecuteChangedFor(nameof(StartBalancedCommand), nameof(StartEconomyCommand), nameof(StartPerformanceCommand))]
    private AssistantPhase phase;

    /// <summary>Résultat d'Équilibre pour cette carte : sans lui, Économie et Performance restent verrouillés.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnlocked), nameof(IsLocked), nameof(RecordText))]
    [NotifyCanExecuteChangedFor(nameof(StartEconomyCommand), nameof(StartPerformanceCommand))]
    private BalancedRecord? record;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(RecordText))] private bool driverChanged;

    [ObservableProperty] private string title = "";
    [ObservableProperty] private string instruction = "";
    [ObservableProperty] private double progress;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private bool isWaiting;
    [ObservableProperty] private string gameText = "";
    [ObservableProperty] private string headline = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(TileColumns))] private IReadOnlyList<StatTile> tiles = [];
    /// <summary>Une ligne jusqu'à 5 tuiles ; au-delà (contrôles de stabilité), deux lignes équilibrées.</summary>
    public int TileColumns => Math.Max(1, Tiles.Count <= 5 ? Tiles.Count : (Tiles.Count + 1) / 2);
    [ObservableProperty] private IReadOnlyList<FindingItem> incidents = [];
    [ObservableProperty] private bool hasIncidents;
    [ObservableProperty] private bool showQuestion;
    [ObservableProperty] private string questionText = "";
    [ObservableProperty] private string yesText = "";
    [ObservableProperty] private string noText = "";
    [ObservableProperty] private bool showRetry;
    [ObservableProperty] private bool showGoBack;
    [ObservableProperty] private string preflightText = "";
    [ObservableProperty] private bool canOpenNvidiaApp;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReturnToBalancedSecond))] private bool canReturnToBalanced;
    [ObservableProperty] private bool hasVerdict;
    [ObservableProperty] private string verdictTitle = "";
    [ObservableProperty] private string verdictDetails = "";
    /// <summary>L'avis recommande Équilibre : son bouton passe en tête, en bouton principal.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ReturnToBalancedSecond))] private bool returnToBalancedFirst;
    public bool ReturnToBalancedSecond => CanReturnToBalanced && !ReturnToBalancedFirst;
    [ObservableProperty] private string doneText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanValidate))] private bool doneChanged;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanValidate))] private bool validated;

    /// <summary>La validation de 15 minutes est proposée tant que le réglage retenu ne l'a pas passée.</summary>
    public bool CanValidate => DoneChanged && !Validated;

    public bool IsIdle => Phase == AssistantPhase.Idle;
    public bool IsActive => Phase != AssistantPhase.Idle;
    public bool IsPreflight => Phase == AssistantPhase.Preflight;
    public bool IsMeasuring => Phase == AssistantPhase.Measuring;
    public bool IsReview => Phase == AssistantPhase.Review;
    public bool IsMemoryPrompt => Phase == AssistantPhase.MemoryPrompt;
    public bool IsDone => Phase == AssistantPhase.Done;
    public bool IsUnlocked => Record is not null;
    public bool IsLocked => Record is null;

    /// <summary>Résumé du dernier Équilibre, affiché sous les trois modes.</summary>
    public string RecordText => Record is not { } r || r.ResultState is not { } result
        ? "Lancez d'abord Équilibre : sa mesure et son réglage servent de base à Économie et Performance."
        : $"Équilibre du {r.At:dd/MM à HH:mm}{(r.Game is { } g ? $" sur {g}" : "")} : {result.Describe()}." +
          (r.FoundOffset ? "" : " Aucun décalage stable trouvé : plafond seul.") +
          (DriverChanged ? " Le pilote NVIDIA a changé depuis : refaire Équilibre est conseillé." : "");

    /// <summary>Recharge le résultat d'Équilibre de cette carte (au premier affichage de la page).</summary>
    public async Task LoadRecordAsync()
    {
        if (tuner() is not { } t) return;
        Record = await Task.Run(() => BalancedRecord.LoadFor(t.Identity));
        DriverChanged = Record?.DriverChanged(t.Identity) == true;
    }

    [RelayCommand(CanExecute = nameof(CanStartBalanced))] private Task StartBalanced() => Check(TuningGoal.Balanced);
    [RelayCommand(CanExecute = nameof(CanStartDerived))] private Task StartEconomy() => Check(TuningGoal.Economy);
    [RelayCommand(CanExecute = nameof(CanStartDerived))] private Task StartPerformance() => Check(TuningGoal.Performance);

    /// <summary>
    /// Contrôle avant mesure : un overclock de NVIDIA App se superpose aux réglages d'Aria et fausserait chaque palier.
    /// Aria ne peut pas le couper (aucune interface officielle) : elle bloque, explique et laisse l'utilisateur décider.
    /// </summary>
    private async Task Check(TuningGoal goal)
    {
        _pendingGoal = goal;
        var app = await Task.Run(() => NvidiaAppTuning.Read());
        _nvidiaAppNote = app.Unreadable is { } why ? $"Overclock de NVIDIA App : vérification impossible ({why})." : "";
        if (!app.Active)
        {
            if (IsPreflight) Title = "";
            Phase = AssistantPhase.Idle;
            await Begin(goal);
            return;
        }
        SystemSounds.Asterisk.Play();
        Title = "CONTRÔLE AVANT MESURE";
        Headline = "L'overclock de NVIDIA App est actif";
        PreflightText = $"{app.Describe()}. Il se superpose aux réglages d'Aria et fausserait chaque palier. " +
                        "Dans NVIDIA App : Système → Performances → Réglage automatique : Désactivé, puis « Restaurer les valeurs par défaut ». " +
                        "Revenez ensuite ici et vérifiez à nouveau.";
        CanOpenNvidiaApp = NvidiaAppTuning.AppPath is not null;
        Phase = AssistantPhase.Preflight;
    }

    [RelayCommand]
    private void OpenNvidiaApp()
    {
        if (!NvidiaAppTuning.Open()) CanOpenNvidiaApp = false;
    }

    [RelayCommand] private Task RecheckNvidiaApp() => Check(_pendingGoal);

    /// <summary>L'utilisateur garde l'overclock de NVIDIA App : la mesure démarre, avec un rappel en tête.</summary>
    [RelayCommand]
    private Task ContinueDespiteNvidiaApp()
    {
        _nvidiaAppNote = "Overclock de NVIDIA App laissé actif à votre demande : les mesures peuvent être faussées.";
        Title = "";
        Phase = AssistantPhase.Idle;
        return Begin(_pendingGoal);
    }

    /// <summary>Fermer le contrôle : rien n'a encore été appliqué à la carte.</summary>
    [RelayCommand]
    private void DismissPreflight()
    {
        Title = "";
        Phase = AssistantPhase.Idle;
    }

    private bool CanStartDerived() => IsUnlocked && IsIdle && !MeasurementGate.AnalysisRunning;
    private bool CanStartBalanced() => IsIdle && !MeasurementGate.AnalysisRunning;

    /// <summary>Une analyse de Mon PC est en cours : l'assistant attend qu'elle se termine.</summary>
    public bool BlockedByAnalysis => MeasurementGate.AnalysisRunning;

    /// <summary>Abonnement à la porte des mesures (appelé une fois par la page).</summary>
    public void WatchMeasurements()
    {
        MeasurementGate.Changed += () =>
        {
            StartBalancedCommand.NotifyCanExecuteChanged();
            StartEconomyCommand.NotifyCanExecuteChanged();
            StartPerformanceCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(BlockedByAnalysis));
        };
    }

    partial void OnPhaseChanged(AssistantPhase value) => MeasurementGate.AssistantRunning = value != AssistantPhase.Idle;

    private async Task Begin(TuningGoal goal)
    {
        if (tuner() is not { } t) return;
        if (goal != TuningGoal.Balanced && Record?.ResultState is null) return;

        var noLock = !t.Limits.SupportsClockLock;
        var bench = UseBench && BenchAvailable;
        // Économie et Performance reprennent la définition où Équilibre a mesuré (relevée s'il butait sur une limite d'images) :
        // pas de mesure plafonnée à refaire, et les FPS se comparent à ceux du départ
        var benchDefinition = goal != TuningGoal.Balanced && Record?.BenchDefinition is { } measured
            ? measured
            : BenchRunner.DefinitionFor(BenchRunner.PrimaryScreenHeight());
        var (name, what, verdict) = goal switch
        {
            TuningGoal.Balanced when noLock => ("Équilibre",
                $"mesurer votre jeu aux valeurs d'usine, caler la limite de puissance sur la consommation mesurée, puis augmenter le décalage GPU (+100, +30 après le premier problème, puis +15), tester la mémoire et valider le réglage retenu pendant 15 minutes. " +
                $"Votre carte ({GpuCatalog.Describe(t.Limits.Architecture)}) ne permet pas de plafond de fréquence : Équilibre lui donne plus de fréquence pour les mêmes watts, et Économie pourra ensuite échanger ce gain contre des watts",
                "artefacts ou plantage → pas plus fin"),
            TuningGoal.Balanced => ("Équilibre",
                "mesurer votre jeu aux valeurs d'usine, plafonner la carte à sa fréquence haute habituelle, puis augmenter le décalage GPU (+100, +30 après le premier problème, puis +15), " +
                "tester la mémoire et valider le réglage retenu pendant 15 minutes. " +
                "Résultat : les mêmes FPS avec moins de watts, gardé pour débloquer Économie et Performance",
                "artefacts ou plantage → pas plus fin"),
            TuningGoal.Economy when noLock => ("Économie",
                "partir du réglage d'Équilibre et baisser la limite de puissance palier par palier (environ 10 %, puis 5 %, puis 5 W), jusqu'à 60 % de la valeur d'usine. À chaque palier, vous voyez la perte mesurée",
                "perte trop grande → pas plus fin"),
            TuningGoal.Economy => ("Économie",
                $"partir du réglage d'Équilibre et baisser le plafond palier par palier (environ 5 %, puis 2,5 %, puis 15 MHz), 15 % au plus. À chaque palier, vous voyez la perte mesurée",
                "perte trop grande → pas plus fin"),
            _ => ("Performance",
                $"partir de la courbe d'usine, retirer le plafond et monter la limite de puissance au maximum ({t.Limits.MaxPowerMilliwatts / 1000} W), puis augmenter le décalage GPU (+60, +30, puis +15) jusqu'au plus haut palier stable",
                "artefacts ou plantage → pas plus fin")
        };
        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: $"Assistant · {name}",
                ConfirmText: "Commencer",
                Message: $"L'assistant va {what}.",
                Rows:
                [
                    new(goal == TuningGoal.Balanced ? "1. Mesure de départ" : "1. Point de départ",
                        To: goal switch
                        {
                            TuningGoal.Balanced => bench ? "valeurs d'usine, 3 min de banc d'essai" : "valeurs d'usine, 3 min de jeu",
                            TuningGoal.Performance => bench ? "réglage de base, 3 min de banc d'essai et 1 min de calcul vérifié" : "réglage de base, 3 min de jeu",
                            _ => "résultat d'Équilibre"
                        }),
                    new("2. Un palier à la fois", To: bench ? "3 min de banc + 1 min de calcul vérifié, enchaînés automatiquement"
                        : Record?.Game is { } g && goal != TuningGoal.Balanced ? $"3 min chacun, sur {g} de préférence" : "3 min de jeu chacun, même jeu, même scène"),
                    bench && goal != TuningGoal.Economy
                        ? new("3. Verdict automatique", To: "image ou calcul faux, ou plantage → pas plus fin")
                        : new("3. Votre verdict", To: verdict),
                    // Équilibre sert de base aux autres modes : mémoire et validation font partie du parcours
                    .. (goal == TuningGoal.Balanced
                        ? new DialogRow[]
                        {
                            new("4. Mémoire vidéo", To: bench ? "+250 à +1000 MHz, test de la mémoire à chaque palier" : "+250 à +1000 MHz, par paliers"),
                            new("5. Validation", To: bench ? "15 min de banc, puis calcul et mémoire vérifiés" : "15 min de jeu sur le réglage retenu"),
                            new("6. À la fin", To: "vous choisissez de garder ou d'annuler")
                        }
                        : [new("4. À la fin", To: "vous choisissez de garder ou d'annuler")])
                ],
                Warnings: [.. (goal == TuningGoal.Performance && t.Limits.MaxPowerMilliwatts > t.Limits.DefaultPowerMilliwatts
                    ?
                    new[]
                    {
                        "Un palier trop poussé peut faire planter le jeu ou le pilote (écran noir quelques secondes). Chaque palier s'efface au redémarrage.",
                        $"La carte pourra consommer jusqu'à {t.Limits.MaxPowerMilliwatts / 1000} W au lieu de {t.Limits.DefaultPowerMilliwatts / 1000} W : vérifiez que votre alimentation a la puissance recommandée et que le câble d'alimentation de la carte (12V-2x6 / 12VHPWR) est enfoncé à fond."
                    }
                    : ["Un palier trop poussé peut faire planter le jeu ou le pilote (écran noir quelques secondes). Le matériel ne risque rien, et chaque palier s'efface au redémarrage."]),
                    .. (BenchRunner.OnBattery()
                        ? new[] { "Le PC est sur batterie : la carte y est bridée et les mesures seraient faussées. Branchez-le sur secteur avant de commencer." }
                        : [])],
                Note: bench
                    ? $"Le banc d'essai Aria s'ouvre en plein écran à chaque mesure (Ultra, {BenchLabel(benchDefinition)}) et se ferme seul ; suit un contrôle de calcul dans une petite fenêtre. " +
                      "Un auto-contrôle de 20 secondes vérifie d'abord que ces contrôles voient bien les erreurs. " +
                      "Laissez le PC travailler, sur secteur pour un portable, sans IA locale ni jeu ouverts. Échap arrête la mesure en cours." +
                      (goal == TuningGoal.Balanced ? " Équilibre complet dure environ 1 h à 1 h 30." : "")
                    : "Chaque mesure démarre dès qu'Aria voit le jeu en action, et un son retentit à la fin : revenez alors dans Aria (Alt+Tab).",
                IsRisky: true)))
            return;

        _goal = goal;
        _benchMode = bench;
        _benchDefinition = benchDefinition;
        _previousStep = "";
        _memoryPhase = false;
        _aborted = false;
        _validating = false;
        Validated = false;
        _search = null;
        _before = (await Task.Run(() => GpuProfile.ReadCurrent(t))) with { AtStartup = false };
        _previousStep = string.Join("\n", new[] { PowerNote(t), _nvidiaAppNote }.Where(n => n.Length > 0));
        _checks = false;
        _checksCut = null;
        _lastStableStability = null;
        DeleteMemoryPrevious();

        if (goal == TuningGoal.Balanced)
        {
            // Mesure de départ aux valeurs d'usine, même si un autre outil a changé la limite de puissance
            _start = TuningPlan.Start(GpuProfile.Factory(t.Limits, _before.PowerLimitMilliwatts));
            _baseline = null;
            _baselineGame = null;
            _baselineFps = null;
            _step = Step.Baseline;
            _lastStable = _start;
            if (await ApplyAsync(_start) && await PrepareChecksAsync()) await MeasureAsync();
            return;
        }

        var balanced = Record!.ResultState!;
        _baseline = Record.Baseline;
        _baselineGame = Record.Game;
        // FPS de départ comparables seulement si Équilibre a mesuré avec le banc, à la même définition
        _baselineFps = _benchMode && Record.BenchDefinition == _benchDefinition ? Record.BenchFps : null;
        _lastStableReport = null;
        _lastStableFps = null;
        if (goal == TuningGoal.Economy)
        {
            _start = balanced;
            _lastStable = balanced;
            _search = TuningSearch.Economy(balanced, t.Limits);
            if (_search.Finished) await FinishAsync("Le plafond d'Équilibre est déjà au plus bas proposé.");
            else if (await ApplyAsync(_start) && await PrepareChecksAsync()) await NextStepAsync();
            return;
        }

        // Performance : on mesure d'abord le réglage de base (puissance au maximum, sans plafond)
        _start = TuningSearch.PerformanceStart(balanced, t.Limits);
        _lastStable = _start;
        _step = Step.PerformanceBase;
        if (await ApplyAsync(_start) && await PrepareChecksAsync()) await MeasureAsync();
    }

    /// <summary>
    /// Limite de puissance changée par un autre outil (NVIDIA App, Afterburner) : Aria pose ses propres valeurs pendant les mesures,
    /// mais l'outil reposera la sienne au démarrage. Vide si la limite est celle d'usine (à 2 % près) ou si c'est Aria qui l'a réglée.
    /// </summary>
    private string PowerNote(GpuTuner t)
    {
        var factory = GpuProfile.Factory(t.Limits, _before.PowerLimitMilliwatts).PowerLimitMilliwatts;
        if (!t.Limits.SupportsPowerLimit || GpuProfile.OriginFor(t) is not null
            || Math.Abs((double)_before.PowerLimitMilliwatts - factory) < factory * 0.02) return "";
        return $"Limite de puissance trouvée à {_before.PowerLimitWatts:0} W au lieu de {factory / 1000} W d'usine, réglée par un autre outil (NVIDIA App, Afterburner…) : " +
               $"Aria pose ses propres valeurs pendant les mesures. Remettez cet outil par défaut, sinon il reposera {_before.PowerLimitWatts:0} W au prochain démarrage.";
    }

    private async Task MeasureAsync()
    {
        if (await GpuTelemetry.OpenSharedAsync() is not { } gpu) return;
        var step = _validating ? null : _search?.Current;
        _duration = _validating ? ValidationDuration : MeasureDuration;
        Title = _validating ? "VALIDATION · 15 MINUTES"
            : _search is not null
            ? $"{(_memoryPhase ? "MÉMOIRE · " : "")}PALIER {_search.Tested + 1} · {_search.StageName}"
            : _step == Step.Baseline ? "MESURE DE DÉPART" : "PERFORMANCE · RÉGLAGE DE BASE";
        if (_benchMode)
        {
            await MeasureWithBenchAsync(gpu, step);
            return;
        }
        var watch = _goal == TuningGoal.Economy && !_memoryPhase
            ? "Jugez la fluidité : la perte mesurée s'affichera à la fin."
            : "Surveillez bandes, scintillements ou couleurs bizarres.";
        var sameGame = _baselineGame is { } g ? $" ({g} de préférence)" : "";
        // Rappels du départ (limite d'un autre outil, NVIDIA App) : affichés une fois, en tête de la première mesure
        var notes = _previousStep.Length > 0 ? _previousStep + "\n" : "";
        _previousStep = "";
        Instruction = notes + (_validating
            ? $"Réglage retenu en place ({_lastStable.Describe()}). Jouez normalement pendant 15 minutes, idéalement une scène exigeante : c'est la vraie épreuve de stabilité."
            : step is not null
            ? $"Palier en place : {step.Describe()}. Retournez dans le jeu{sameGame}, même scène : la mesure de 3 minutes démarre dès qu'Aria le voit en action. {watch}"
            : _step == Step.Baseline
                ? $"Valeurs d'usine en place ({_start.Describe()}). Lancez votre jeu : la mesure de 3 minutes démarre dès qu'Aria le voit en action. Restez ensuite dans la même scène pour les paliers suivants."
                : $"Réglage de base en place ({_start.Describe()}). Retournez dans le jeu{sameGame} : la mesure de 3 minutes démarre dès qu'Aria le voit en action. {watch}");
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
            report = await PerformanceSession.RunAsync(gpu, _duration, new Progress<TimeSpan>(UpdateProgress), _stop.Token);
        }
        finally
        {
            _stop.Dispose();
            _stop = null;
        }
        if (_aborted) return;
        SystemSounds.Asterisk.Play();
        _current = report;
        _bench = null;
        Review(report, game, null);
    }

    /// <summary>
    /// Mesure avec le banc d'essai : Aria-Bench tourne en plein écran (Ultra, définition de l'écran) pendant que la
    /// télémétrie habituelle enregistre la carte. Le banc vérifie aussi ses images : une erreur de calcul trahit l'instabilité.
    /// </summary>
    private async Task MeasureWithBenchAsync(GpuTelemetry gpu, GpuTuningState? step)
    {
        var setting = _validating ? $"Réglage retenu en place ({_lastStable.Describe()})"
            : step is not null ? $"Palier en place : {step.Describe()}"
            : $"{(_step == Step.Baseline ? "Valeurs d'usine" : "Réglage de base")} en place ({_start.Describe()})";
        var minutes = _duration.TotalMinutes >= 2 ? $"{_duration.TotalMinutes:0} minutes" : $"{_duration.TotalSeconds:0} secondes";
        _checksCut = null;
        Instruction = (_previousStep.Length > 0 ? _previousStep + "\n" : "") +
                      $"{setting}. Le banc d'essai Aria tourne en plein écran pendant {minutes}, puis se ferme seul : laissez le PC travailler. Échap l'arrête.";
        Phase = AssistantPhase.Measuring;
        Progress = 0;
        IsWaiting = false;
        GameText = $"BANC D'ESSAI · ULTRA · {BenchLabel(_benchDefinition).ToUpperInvariant()}";
        ProgressText = "DÉMARRAGE DU BANC D'ESSAI";

        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        PerformanceReport? report = null;
        BenchResult bench;
        try
        {
            var running = BenchRunner.RunAsync(BenchQuality, _benchDefinition, _duration + BenchRunner.Tail, BenchOutput, token);
            try
            {
                // La télémétrie démarre quand le banc dessine déjà ses images mesurées
                await Task.Delay(BenchRunner.LeadTime, token);
                report = await PerformanceSession.RunAsync(gpu, _duration, new Progress<TimeSpan>(UpdateProgress), token);
            }
            catch (OperationCanceledException)
            {
                // Arrêt pendant le démarrage : pas de télémétrie
            }
            bench = await running;
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            bench = BenchResult.Missing($"Le banc d'essai n'a pas pu démarrer : {e.Message}");
        }
        finally
        {
            _stop.Dispose();
            _stop = null;
        }
        if (_aborted) return;

        _bench = bench;
        _current = report;
        // FPS bridés par une limite (V-Sync, limite du pilote) : on charge davantage la carte et on refait la mesure (sans contrôle)
        if (bench.Pass?.Plafond is not null && !bench.Unstable && BenchRunner.NextDefinition(_benchDefinition) is { } next)
        {
            _benchDefinition = next;
            _baselineFps = null;
            _previousStep = $"FPS plafonnés par une limite d'images : mesure refaite en {BenchLabel(next)} pour charger la carte à fond.";
            await Retry();
            return;
        }
        // Le rendu a tenu : calcul vérifié à pleine puissance, ou mémoire vidéo pour un palier mémoire
        if (report is not null && !bench.Unstable)
        {
            bench = await RunChecksAsync(bench, setting);
            if (_aborted) return;
            _bench = bench;
        }
        if (report is null)
        {
            SystemSounds.Asterisk.Play();
            Tiles = [];
            Incidents = [];
            HasIncidents = false;
            Headline = bench.Erreurs.FirstOrDefault() ?? "Mesure arrêtée avant d'avoir commencé";
            ShowQuestion = false;
            ShowRetry = true;
            ShowGoBack = false;
            Phase = AssistantPhase.Review;
            return;
        }
        Review(report, $"le banc d'essai Aria (Ultra {BenchLabel(_benchDefinition)})", bench);
    }

    public static string BenchLabel(int definition) => definition == 2160 ? "4K" : $"{definition}p";

    /// <summary>
    /// Contrôles prévus après le rendu : aucun pour la mesure de départ (valeurs d'usine), la mémoire vidéo pour un palier
    /// mémoire, le calcul vérifié pour tout palier qui touche au cœur et pour le réglage de base de Performance, les deux
    /// pour la validation (la mémoire seulement si elle est poussée).
    /// </summary>
    private List<(BenchCheck Check, TimeSpan Duration)> PlannedChecks()
    {
        if (!_checks) return [];
        if (_validating)
            return _lastStable.MemoryOffsetMHz > 0
                ? [(BenchCheck.Compute, ValidationCheckDuration), (BenchCheck.Memory, ValidationCheckDuration)]
                : [(BenchCheck.Compute, ValidationCheckDuration)];
        if (_search is not null) return [_memoryPhase ? (BenchCheck.Memory, MemoryCheckDuration) : (BenchCheck.Compute, ComputeCheckDuration)];
        return _step == Step.PerformanceBase ? [(BenchCheck.Compute, ComputeCheckDuration)] : [];
    }

    /// <summary>Lance les contrôles prévus après le rendu et les réunit au résultat du banc ; s'arrête au premier échec.</summary>
    private async Task<BenchResult> RunChecksAsync(BenchResult render, string setting)
    {
        var results = new List<BenchResult>();
        foreach (var (check, duration) in PlannedChecks())
        {
            Instruction = check == BenchCheck.Memory
                ? $"{setting}. Contrôle de la mémoire vidéo dans une petite fenêtre ({duration.TotalSeconds:0} s) : calcul à pleine puissance, " +
                  "puis toute la mémoire libre écrite et relue, avec sa bande passante. Laissez le PC travailler."
                : $"{setting}. Contrôle du calcul dans une petite fenêtre ({duration.TotalSeconds:0} s) : la carte à pleine puissance, " +
                  "chaque résultat vérifié, comme sous une IA locale. Laissez le PC travailler.";
            var previous = check == BenchCheck.Memory && !_validating && File.Exists(MemoryPrevious) ? MemoryPrevious : null;
            var result = await CheckAsync(check, duration, check == BenchCheck.Memory ? "MÉMOIRE VIDÉO" : "CALCUL VÉRIFIÉ", previous);
            results.Add(result);
            if (_aborted || !result.Completed || result.Unstable) break;
        }
        // Contrôle arrêté (Échap, « Arrêter la mesure ») ou impossible : le palier ne sera pas jugé
        _checksCut = results.FirstOrDefault(r => !r.Completed && !r.Unstable) switch
        {
            null => null,
            { Interrupted: true } => "Contrôle de stabilité arrêté avant la fin : palier non jugé",
            var failed => $"Contrôle de stabilité impossible ({failed.Erreurs.FirstOrDefault() ?? failed.Statut}) : palier non jugé"
        };
        return results.Count == 0 ? render : BenchResult.Merge(render, [.. results]);
    }

    /// <summary>Un contrôle d'Aria-Bench dans sa petite fenêtre, avec la barre de progression ; « Arrêter la mesure » le coupe.</summary>
    private async Task<BenchResult> CheckAsync(BenchCheck check, TimeSpan duration, string label, string? previous = null)
    {
        Phase = AssistantPhase.Measuring;
        Progress = 0;
        IsWaiting = false;
        GameText = "CONTRÔLE DE STABILITÉ";
        _stop = new CancellationTokenSource();
        try
        {
            var running = BenchRunner.RunCheckAsync(check, duration, check == BenchCheck.Memory ? MemoryOutput : CheckOutput, previous, _stop.Token);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!running.IsCompleted)
            {
                var left = duration - watch.Elapsed;
                Progress = Math.Clamp(watch.Elapsed / duration, 0, 1);
                ProgressText = $"{label} · {(left < TimeSpan.Zero ? TimeSpan.Zero : left):m\\:ss} RESTANTES";
                await Task.WhenAny(running, Task.Delay(500));
            }
            return await running;
        }
        catch (Exception e) when (e is FileNotFoundException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return BenchResult.Missing($"le contrôle n'a pas pu démarrer : {e.Message}");
        }
        finally
        {
            _stop.Dispose();
            _stop = null;
        }
    }

    /// <summary>
    /// Auto-contrôle (20 s) avant la première mesure, carte au réglage de départ : des erreurs sont injectées exprès et le
    /// test doit toutes les retrouver. Sinon ses verdicts ne valent rien sur cette machine : l'assistant se fie alors au seul
    /// rendu, et le dit. Renvoie faux si l'assistant a été abandonné entre-temps.
    /// </summary>
    private async Task<bool> PrepareChecksAsync()
    {
        _checks = false;
        if (!_benchMode) return true;
        Title = "AUTO-CONTRÔLE";
        Instruction = "Avant les mesures, Aria vérifie ses contrôles de stabilité : des erreurs de calcul et de mémoire sont injectées exprès, " +
                      "le test doit toutes les retrouver (20 secondes, dans une petite fenêtre).";
        var result = await CheckAsync(BenchCheck.SelfCheck, SelfCheckDuration, "AUTO-CONTRÔLE DES TESTS");
        if (_aborted) return false;
        _checks = result.SelfCheckPassed;
        if (!_checks)
        {
            var why = result.Interrupted ? "auto-contrôle arrêté"
                : result.Stabilite?.Raisons?.FirstOrDefault() ?? result.Erreurs.FirstOrDefault() ?? "résultat illisible";
            _previousStep = string.Join("\n", new[] { _previousStep, $"Contrôles de calcul et de mémoire indisponibles ({why}) : verdict sur le seul rendu 3D." }
                .Where(n => n.Length > 0));
        }
        return true;
    }

    private static void DeleteMemoryPrevious()
    {
        try { File.Delete(MemoryPrevious); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* le palier suivant ne sera pas comparé, sans gravité */ }
    }

    /// <summary>Ce qui a lâché dans un palier instable, pour le rappel affiché au palier suivant.</summary>
    private static string UnstableReason(BenchResult bench) =>
        bench.StabilityFailed && bench.Stabilite is { } s ? StabilityTitle(s).ToLowerInvariant()
        : bench.Pass is { ErreursCalcul: > 0 } ? "erreurs de calcul" : "plantage";

    private static string StabilityTitle(BenchStability s) =>
        s.EcartsCalcul > 0 ? "Calculs faux à pleine puissance" : s.MotsFaux > 0 ? "Mémoire vidéo relue fausse" : "Le pilote a lâché pendant le contrôle";

    /// <summary>« , images et calculs vérifiés » : ce que le palier a passé.</summary>
    private static string CheckedNote(BenchResult bench) => bench.Stabilite switch
    {
        { Passes: > 0 } => ", images, calculs et mémoire vérifiés",
        { ExecutionsCalcul: > 0 } => ", images et calculs vérifiés",
        _ => ", images vérifiées"
    };

    private void UpdateProgress(TimeSpan elapsed)
    {
        Progress = Math.Clamp(elapsed / _duration, 0, 1);
        var left = _duration - elapsed;
        ProgressText = $"MESURE EN COURS · {(left < TimeSpan.Zero ? TimeSpan.Zero : left):m\\:ss} RESTANTES";
    }

    private void Review(PerformanceReport report, string game, BenchResult? bench)
    {
        // Instable : plantage noté par Windows, ou (banc d'essai) erreur de calcul / pilote qui lâche pendant le rendu
        var unstable = report.Unstable || bench?.Unstable == true;
        var incidents = report.Findings.Where(f => report.Unstable && f.Severity == Severity.Critical)
            .Select(f => new FindingItem(f.Title, f.Detail, f.Advice, [], f.Severity)).ToList();
        if (bench?.StabilityFailed == true && bench.Stabilite is { } failed)
            incidents.Insert(0, new FindingItem(StabilityTitle(failed), string.Join(" ", failed.Raisons ?? []),
                "Le rendu 3D tenait, mais pas le contrôle de stabilité, plus proche d'une IA locale : l'assistant passe à un palier plus prudent.",
                [], Severity.Critical));
        if (bench?.Pass is { ErreursCalcul: > 0 } bad)
            incidents.Insert(0, new FindingItem("Erreurs de calcul",
                $"{bad.ErreursCalcul} image(s) de contrôle sur {bad.Verifications} différaient de la référence (jusqu'à {bad.PixelsDifferents:N0} pixels).",
                "La carte calcule faux avec ce réglage, même sans planter : l'assistant passe à un palier plus prudent.", [], Severity.Critical));
        if (bench?.DriverFailure == true)
            incidents.Insert(0, new FindingItem("Le pilote a lâché pendant le banc d'essai", bench.Erreurs.FirstOrDefault() ?? "",
                "Réglage instable : l'assistant passe à un palier plus prudent.", [], Severity.Critical));
        Incidents = incidents;
        HasIncidents = Incidents.Count > 0;
        var isReference = _search is null && _step == Step.Baseline && !_validating;
        var fps = bench?.Pass?.FpsMoyen;
        Tiles = BuildTiles(report, isReference ? null : _baseline, fps, isReference ? null : _baselineFps, bench?.Stabilite);
        var automatic = bench is not null && bench.Pass is not null;
        if (_checksCut is { } cut && !unstable)
        {
            ShowInconclusive(cut);
            return;
        }

        if (_validating && automatic)
        {
            // Banc d'essai : verdict automatique (Stable termine, Unstable recule d'un cran)
            if (unstable) _ = Unstable();
            else if (report.Conclusive) _ = Stable();
            else ShowInconclusive();
            return;
        }
        if (_validating)
        {
            Headline = report.Unstable ? "Plantage détecté pendant la validation"
                : !report.Conclusive ? "Validation non concluante : la carte n'a pas assez travaillé"
                : "Validation terminée";
            QuestionText = "Pendant ces 15 minutes, avez-vous vu des bandes, des scintillements, des couleurs bizarres, ou le jeu a-t-il planté ?";
            YesText = "NON, TOUT ÉTAIT NORMAL";
            NoText = "OUI, J'AI VU UN PROBLÈME";
            ShowQuestion = report.Conclusive && !report.Unstable;
            ShowRetry = !report.Conclusive || report.Unstable;
            ShowGoBack = report.Unstable;
            Phase = AssistantPhase.Review;
            return;
        }

        if (_search is null && report.Conclusive && !unstable && tuner() is { } t)
        {
            if (_step == Step.Baseline)
            {
                // Mesure de départ d'Équilibre réussie : elle devient la référence, puis premier palier
                _baseline = report.Stats;
                _baselineFps = fps;
                _baselineGame = bench is not null ? game : report.Incidents?.Game ?? game;
                _lastStableReport = report;
                _lastStableFps = fps;
                // Plafond si la carte le permet (Turing et plus récentes), sinon limite de puissance, sinon décalage seul
                if (TuningPlan.BalancedCap(report.Stats, t.Limits) is { } cap)
                    _search = TuningSearch.Balanced(_start, t.Limits, cap);
                else if (!t.Limits.SupportsClockLock && TuningPlan.BalancedPower(report.Stats, t.Limits) is { } power)
                    _search = TuningSearch.BalancedByPower(_start, t.Limits, power);
                else if (!t.Limits.SupportsClockLock && t.Limits.SupportsCoreOffset)
                    _search = TuningSearch.Balanced(_start, t.Limits, 0);
                else
                {
                    _ = FinishAsync(t.Limits.SupportsClockLock
                        ? "Fréquence de la carte en jeu introuvable dans la mesure de départ : impossible de placer le plafond."
                        : "Cette carte ne permet ni plafond, ni limite de puissance, ni décalage réglables : Équilibre n'a rien à proposer.");
                    return;
                }
            }
            else if (automatic)
            {
                // Réglage de base de Performance : images vérifiées par le banc, pas besoin de demander
                _previousStep = $"Réglage de base stable{CheckedNote(bench!)}{FpsNote(fps)}.";
                _ = Stable();
                return;
            }
            else
            {
                // Réglage de base de Performance : pas de plantage, mais on demande s'il y a eu des artefacts
                Headline = "Réglage de base mesuré";
                QuestionText = "Pendant cette mesure, avez-vous vu des bandes, des scintillements, des couleurs bizarres, ou le jeu a-t-il planté ?";
                YesText = "NON, TOUT ÉTAIT NORMAL";
                NoText = "OUI, J'AI VU UN PROBLÈME";
                ShowQuestion = true;
                ShowRetry = false;
                ShowGoBack = false;
                Phase = AssistantPhase.Review;
                return;
            }
            if (_search.Finished)
            {
                _lastStable = _search.LastStable;
                _ = EndPhaseAsync();
                return;
            }
            _ = NextStepAsync();
            return;
        }

        var economy = _search?.Goal == TuningGoal.Economy;
        if (automatic && _search is not null && !economy)
        {
            // Banc d'essai : erreur de calcul ou plantage → pas plus fin ; sinon le palier tient
            var tested = _search.Current.Describe();
            if (unstable)
            {
                _previousStep = $"Palier précédent ({tested}) : instable ({UnstableReason(bench!)}), pas plus fin.";
                _ = Unstable();
                return;
            }
            if (_memoryPhase && bench!.NotFaster)
            {
                // Plus de fréquence sans plus de bande passante : la GDDR6X corrige des erreurs au lieu d'accélérer
                var speed = bench.Stabilite?.BandePassanteLectureMedianeGoS is { } read ? $", {read:0} Go/s" : "";
                _previousStep = $"Palier précédent ({tested}) : pas plus rapide que le précédent{speed}, la mémoire corrige au lieu d'accélérer.";
                _ = Unstable();
                return;
            }
            if (report.Conclusive)
            {
                _previousStep = $"Palier précédent ({tested}) : stable{CheckedNote(bench!)}{FpsNote(fps)}.";
                _ = Stable();
                return;
            }
        }
        if (automatic && _search is null && _step == Step.PerformanceBase && unstable)
        {
            // Réglage de base de Performance instable : Performance s'arrête et revient au réglage d'Équilibre
            _ = Unstable();
            return;
        }
        if (automatic && _search is null && _step == Step.Baseline && bench!.CalculationErrors)
        {
            // Erreurs de calcul dès les valeurs d'usine : inutile de chercher un undervolt
            SystemSounds.Asterisk.Play();
            Headline = "Erreurs de calcul dès les valeurs d'usine";
            ShowQuestion = false;
            ShowRetry = true;
            ShowGoBack = false;
            Phase = AssistantPhase.Review;
            return;
        }
        if (bench is not null) SystemSounds.Asterisk.Play();
        QuestionText = economy
            ? $"{Loss(report)} Le jeu vous paraît-il toujours assez fluide pour continuer à baisser ?"
            : "Pendant ce palier, avez-vous vu des bandes, des scintillements, des couleurs bizarres, ou le jeu a-t-il planté ?";
        YesText = economy ? "OUI, CONTINUER" : "NON, TOUT ÉTAIT NORMAL";
        NoText = economy ? "NON, C'EST TROP" : "OUI, J'AI VU UN PROBLÈME";
        Headline = unstable
            ? bench?.StabilityFailed == true && bench.Stabilite is { } s ? $"{StabilityTitle(s)} pendant ce palier"
                : bench?.CalculationErrors == true ? "Erreurs de calcul pendant ce palier" : "Plantage détecté pendant ce palier"
            : !report.Conclusive ? "Mesure non concluante : la carte n'a pas assez travaillé"
            : _search is null && _step == Step.PerformanceBase ? "Réglage de base instable"
            : report.Headline;
        ShowQuestion = _search is not null && report.Conclusive && !unstable;
        ShowRetry = !report.Conclusive || unstable;
        ShowGoBack = _search is not null && (unstable || !report.Conclusive);
        Phase = AssistantPhase.Review;
    }

    /// <summary>Mesure trop faible pour juger (carte peu chargée) ou contrôle arrêté : on propose de refaire.</summary>
    private void ShowInconclusive(string headline = "Mesure non concluante : la carte n'a pas assez travaillé")
    {
        SystemSounds.Asterisk.Play();
        Headline = headline;
        ShowQuestion = false;
        ShowRetry = true;
        ShowGoBack = false;
        Phase = AssistantPhase.Review;
    }

    /// <summary>« , 142 FPS (-1 %) » d'après la mesure de départ.</summary>
    private string FpsNote(double? fps) =>
        fps is not { } f ? "" : _baselineFps is { } b && b > 0 ? $", {f:0} FPS ({(f - b) / b * 100:+0;-0;0} %)" : $", {f:0} FPS";

    /// <summary>« Fréquence -4 %, -45 W, -6 °C par rapport au départ. » (Économie)</summary>
    private string Loss(PerformanceReport report)
    {
        if (_baseline is not { } b || !report.Conclusive) return "";
        var s = report.Stats;
        var clock = b.AvgClock > 0 ? (s.AvgClock - b.AvgClock) / b.AvgClock * 100 : 0;
        var parts = new List<string> { $"Fréquence {clock:+0;-0;0} %" };
        if (_bench?.Pass?.FpsMoyen is { } fps && _baselineFps is { } baseFps && baseFps > 0)
            parts.Insert(0, $"FPS {(fps - baseFps) / baseFps * 100:+0;-0;0} %");
        if (s.AvgPower is { } p && b.AvgPower is { } bp) parts.Add(TuningPlan.Delta(p, bp, "W"));
        parts.Add(TuningPlan.Delta(s.MaxTemp, b.MaxTemp, "°C"));
        return string.Join(", ", parts) + " par rapport au départ.";
    }

    private async Task NextStepAsync()
    {
        if (_search is { Finished: false } search && await ApplyAsync(search.Current)) await MeasureAsync();
    }

    /// <summary>Palier jugé bon : la recherche continue (même pas, ou fin si la borne est atteinte).</summary>
    [RelayCommand]
    private async Task Stable()
    {
        if (_validating)
        {
            _validating = false;
            Validated = true;
            if (_current is { Conclusive: true } validation)
            {
                // 15 minutes sur le réglage retenu : mesure plus fiable que celle du dernier palier (pris avant le recul de sécurité)
                _lastStableReport = validation;
                _lastStableFps = _benchMode ? _bench?.Pass?.FpsMoyen : null;
                _lastStableStability = _benchMode ? _bench?.Stabilite : null;
            }
            SaveBalancedRecord();
            await FinishAsync($"Validé : 15 minutes sans plantage ni artefact. Réglage retenu : {_lastStable.Describe()}.");
            return;
        }
        if (_search is null && _step == Step.PerformanceBase && tuner() is { } t)
        {
            // Réglage de base stable : la recherche du décalage part de lui
            _lastStableReport = _current;
            _lastStableFps = _bench?.Pass?.FpsMoyen;
            _lastStableStability = _bench?.Stabilite;
            _search = TuningSearch.Performance(Record!.ResultState!, t.Limits);
            if (_search.Finished) await EndPhaseAsync();
            else await NextStepAsync();
            return;
        }
        if (_search is null) return;
        _search.Report(true);
        _lastStable = _search.LastStable;
        _lastStableReport = _current;
        _lastStableFps = _bench?.Pass?.FpsMoyen;
        _lastStableStability = _bench?.Stabilite;
        // Palier mémoire tenu : le suivant devra aller plus vite que lui
        if (_memoryPhase && _bench?.Stabilite is { Passes: > 0 })
        {
            try { File.Copy(MemoryOutput, MemoryPrevious, overwrite: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* le palier suivant ne sera pas comparé, sans gravité */ }
        }
        await ContinueAsync();
    }

    /// <summary>Artefacts, plantage, perte jugée trop grande : la recherche repart du dernier palier stable, avec un pas plus fin.</summary>
    [RelayCommand]
    private async Task Unstable()
    {
        if (_validating)
        {
            await BackOffAfterValidationAsync();
            return;
        }
        if (_search is null && _step == Step.PerformanceBase && Record?.ResultState is { } balanced)
        {
            _lastStable = balanced;
            _lastStableReport = null;
            _lastStableFps = null;
            _lastStableStability = null;
            await FinishAsync("La courbe d'usine ne tient pas sans plafond à pleine puissance : Performance n'a rien à proposer (vérifiez le refroidissement et l'alimentation de la carte). La carte revient au réglage d'Équilibre.");
            return;
        }
        if (_search is null) return;
        _search.Report(false);
        await ContinueAsync();
    }

    private async Task ContinueAsync()
    {
        if (_search is { Finished: false })
        {
            await NextStepAsync();
            return;
        }
        // Fin de la recherche : le résultat gardé recule d'un cran s'il touchait un palier instable
        if (_search is not null) _lastStable = _search.Safe;
        if (await ApplyAsync(_lastStable)) await EndPhaseAsync();
    }

    /// <summary>« Garder ce palier » : terminer ici avec le palier en place.</summary>
    [RelayCommand]
    private Task KeepThis()
    {
        if (_search is not null)
        {
            _lastStable = _search.Current;
            _lastStableReport = _current;
            _lastStableFps = _bench?.Pass?.FpsMoyen;
            _lastStableStability = _bench?.Stabilite;
        }
        if (!_memoryPhase) SaveBalancedRecord();
        return FinishAsync(null);
    }

    /// <summary>Validation finale de 15 minutes sur le réglage retenu.</summary>
    [RelayCommand]
    private async Task Validate()
    {
        _validating = true;
        if (await ApplyAsync(_lastStable)) await MeasureAsync();
    }

    /// <summary>
    /// La validation a échoué : on recule d'un cran (15 MHz de décalage en moins, ou un cran de mémoire si c'est elle qui a
    /// relu faux ; pour Économie, retour au réglage d'Équilibre), puis l'utilisateur peut relancer la validation.
    /// </summary>
    private async Task BackOffAfterValidationAsync()
    {
        _validating = false;
        var previous = _lastStable;
        if (_goal == TuningGoal.Economy && Record?.ResultState is { } balanced)
            _lastStable = balanced;
        else if (_bench?.Stabilite is { MotsFaux: > 0, EcartsCalcul: 0 } && _lastStable.MemoryOffsetMHz >= TuningPlan.MemoryStep)
            _lastStable = _lastStable with { MemoryOffsetMHz = _lastStable.MemoryOffsetMHz - TuningPlan.MemoryStep };
        else if (_lastStable.CoreOffsetMHz - TuningPlan.ClockBin >= _start.CoreOffsetMHz)
            _lastStable = _lastStable with { CoreOffsetMHz = _lastStable.CoreOffsetMHz - TuningPlan.ClockBin };
        else
            _lastStable = _start;
        if (_goal == TuningGoal.Balanced) SaveBalancedRecord();
        _lastStableReport = null;
        _lastStableFps = null;
        _lastStableStability = null;
        await FinishAsync(previous.SameTuning(_lastStable)
            ? "Problème pendant la validation, et plus de marge pour reculer : la carte revient à son réglage de départ."
            : $"Problème pendant la validation : réglage reculé à {_lastStable.Describe()}. Relancez la validation pour en être sûr.");
    }

    /// <summary>Refaire la mesure. Un plantage du pilote efface le réglage : le palier est donc réappliqué.</summary>
    [RelayCommand]
    private async Task Retry()
    {
        if (_validating)
        {
            if (await ApplyAsync(_lastStable)) await MeasureAsync();
            return;
        }
        if (_search is not null) await NextStepAsync();
        else if (await ApplyAsync(_start)) await MeasureAsync();
    }

    [RelayCommand]
    private async Task TestMemory()
    {
        if (tuner() is not { } t) return;
        _memoryPhase = true;
        DeleteMemoryPrevious();
        _search = TuningSearch.Memory(_lastStable, t.Limits);
        if (_search.Finished) await EndAsync();
        else await NextStepAsync();
    }

    [RelayCommand]
    private Task SkipMemory() => FinishAsync(null);

    private Task EndPhaseAsync()
    {
        // Le réglage GPU d'Équilibre est enregistré avant la mémoire : c'est lui qui sert de base aux autres modes
        if (!_memoryPhase) SaveBalancedRecord();
        if (!_memoryPhase && _goal != TuningGoal.Economy && tuner() is { } t && !TuningSearch.Memory(_lastStable, t.Limits).Finished)
        {
            // Équilibre fait toujours le parcours complet : la mémoire s'enchaîne sans question
            if (_goal == TuningGoal.Balanced) return TestMemory();
            Title = "MÉMOIRE VIDÉO (FACULTATIF)";
            Instruction = $"Réglage stable retenu : {_lastStable.Describe()}. Tester aussi la mémoire, par paliers de +250 MHz jusqu'à +1000 MHz ? " +
                          "Gain modeste ; une mémoire trop poussée peut afficher des artefacts ou devenir plus lente sans planter. " +
                          (_checks
                              ? "Chaque palier passe aussi un test de la mémoire vidéo (erreurs et bande passante), et le résultat garde un cran de marge. "
                              : "Le résultat garde un cran de marge. ") +
                          "Une IA locale (LLM) ou un rendu de calcul sollicitent la mémoire bien plus qu'un jeu : si vous en utilisez, " +
                          "vérifiez-les avec ce réglage avant de le garder au démarrage.";
            Phase = AssistantPhase.MemoryPrompt;
            return Task.CompletedTask;
        }
        return EndAsync();
    }

    /// <summary>Fin des paliers : Équilibre enchaîne la validation de 15 minutes, les autres modes passent au choix final.</summary>
    private Task EndAsync() => _goal == TuningGoal.Balanced && !Validated ? Validate() : FinishAsync(null);

    /// <summary>Équilibre terminé : son réglage et sa mesure de départ débloquent Économie et Performance.</summary>
    private void SaveBalancedRecord()
    {
        if (_goal != TuningGoal.Balanced || _baseline is null || tuner() is not { } t) return;
        var record = new BalancedRecord(DateTime.Now, t.Identity.Name, t.Identity.Uuid, t.Identity.DriverVersion, _baselineGame,
            _start.ToRaw(), (_lastStable with { AtStartup = false }).ToRaw(), _lastStable.CoreOffsetMHz > _start.CoreOffsetMHz, _baseline,
            _baselineFps, _benchMode && _baselineFps is not null ? _benchDefinition : null,
            _lastStableReport?.Stats, _benchMode && _baselineFps is not null ? _lastStableFps : null, Validated);
        try
        {
            record.Save();
            Record = record;
            DriverChanged = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ErrorLog.WriteLine($"Résultat d'Équilibre non enregistré : {e.Message}");
        }
    }

    private async Task FinishAsync(string? note)
    {
        await ApplyAsync(_lastStable);
        var changed = !_lastStable.SameTuning(_before);
        DoneChanged = changed;
        Title = "TERMINÉ";
        var unlocked = _goal == TuningGoal.Balanced && Record is not null ? " Économie et Performance sont maintenant disponibles." : "";
        DoneText = (note
                    ?? (changed
                        ? $"Meilleur réglage stable : {_lastStable.Describe()}. Il est en place jusqu'au prochain redémarrage."
                        : "Aucun palier n'a tenu : la carte est revenue à son réglage de départ.")) + unlocked;
        Tiles = changed && _lastStableReport is { } r ? BuildTiles(r, _baseline, _lastStableFps, _baselineFps, _lastStableStability) : [];
        // Économie ou Performance : retour possible au réglage d'Équilibre s'il n'est pas déjà celui en place
        CanReturnToBalanced = _goal != TuningGoal.Balanced && Record?.ResultState is { } balanced && !_lastStable.SameTuning(balanced);
        // Performance : avis d'Aria sur le gain par rapport à Équilibre (FPS du banc seulement s'ils sont à la même définition)
        var verdict = _goal == TuningGoal.Performance && changed && _lastStableReport is { } perf
            ? PerformanceVerdict.Judge(perf.Stats, _lastStableFps, Record?.ResultStats,
                Record?.BenchDefinition == _benchDefinition ? Record?.ResultFps : null)
            : null;
        HasVerdict = verdict is not null;
        VerdictTitle = verdict?.Title ?? "";
        VerdictDetails = verdict is null ? "" : string.Join("\n", verdict.Details);
        ReturnToBalancedFirst = CanReturnToBalanced && verdict?.RecommendsBalanced == true;
        if (_benchMode) SystemSounds.Asterisk.Play();
        Phase = AssistantPhase.Done;
    }

    /// <summary>
    /// Revenir à Équilibre (fin d'Économie ou de Performance) : pose toujours le résultat d'Équilibre, même si l'état
    /// d'avant l'assistant était autre (redémarrage entre les deux), jusqu'au redémarrage.
    /// </summary>
    [RelayCommand]
    private async Task ReturnToBalanced()
    {
        if (Record?.ResultState is not { } balanced) return;
        if (await ApplyAsync(balanced with { AtStartup = false })) Phase = AssistantPhase.Idle;
    }

    [RelayCommand]
    private async Task KeepAtStartup()
    {
        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Garder au démarrage",
                ConfirmText: "Garder",
                Message: $"Réglage gardé : {_lastStable.Describe()}.",
                Warnings: Validated
                    ? ["La tâche planifiée « Aria Cockpit\\Profil GPU » réappliquera ce réglage à chaque ouverture de session, avec les droits administrateur, sans fenêtre."]
                    : ["La tâche planifiée « Aria Cockpit\\Profil GPU » réappliquera ce réglage à chaque ouverture de session, avec les droits administrateur, sans fenêtre.",
                       "Ce réglage n'a pas passé la validation de 15 minutes : le garder au démarrage sans l'avoir validé est plus risqué."],
                Note: "Revenir d'origine (onglet Réglage GPU ou Restauration) supprime la tâche.")))
            return;
        if (await ApplyAsync(_lastStable with { AtStartup = true })) Phase = AssistantPhase.Idle;
    }

    [RelayCommand]
    private void KeepUntilReboot() => Phase = AssistantPhase.Idle;

    /// <summary>Tout annuler : retour au réglage d'avant l'assistant (le résultat d'Équilibre déjà enregistré reste acquis).</summary>
    [RelayCommand]
    private async Task CancelAll()
    {
        _aborted = true;
        _validating = false;
        _stop?.Cancel();
        await ApplyAsync(_before);
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
    private static IReadOnlyList<StatTile> BuildTiles(PerformanceReport r, PerformanceStats? b, double? fps, double? baselineFps,
        BenchStability? stability = null)
    {
        var s = r.Stats;
        // Non concluante : le temps de travail de la carte, pour savoir si la mesure a tourné ou si la carte n'était pas vue chargée
        if (!r.Conclusive)
            return [new("SECONDES ACTIVES", $"{s.ActiveSeconds}", "s", $"sur {s.Seconds} s mesurées, {PerformanceAnalyzer.MinActiveSeconds} s requises")];
        string Before(Func<PerformanceStats, double?> pick, string unit) =>
            b is null || pick(b) is not { } before || pick(s) is not { } now ? "" : $"départ {before:0} {unit} ({TuningPlan.Delta(now, before, unit)})";
        var tiles = new List<StatTile>();
        if (fps is { } f)
            tiles.Add(new("FPS MOY. (BANC)", $"{f:0}", "FPS", baselineFps is { } bf ? $"départ {bf:0} FPS ({TuningPlan.Delta(f, bf, "FPS")})" : ""));
        tiles.AddRange(
        [
            new("FRÉQUENCE MOY.", $"{s.AvgClock:0}", "MHz", Before(x => x.AvgClock, "MHz")),
            new("CONSO. MOY.", s.AvgPower is { } p ? $"{p:0}" : "–", "W", Before(x => x.AvgPower, "W")),
            new("TEMPÉRATURE MAX", $"{s.MaxTemp}", "°C", Before(x => x.MaxTemp, "°C")),
            new("CHARGE GPU MOY.", $"{s.AvgLoad:0}", "%", Before(x => x.AvgLoad, "%"))
        ]);
        // Contrôles de stabilité : ce que le rendu ne voit pas (calcul intensif, mémoire vidéo)
        if (stability is { ExecutionsCalcul: > 0 })
            tiles.Add(new("CALCUL VÉRIFIÉ", $"{stability.ExecutionsCalcul}", "exéc.",
                stability.EcartsCalcul == 0 ? "0 écart à pleine puissance" : $"{stability.EcartsCalcul} écart(s)"));
        if (stability is { Passes: > 0, BandePassanteLectureMedianeGoS: { } bandwidth })
            tiles.Add(new("MÉMOIRE VIDÉO", $"{bandwidth:0}", "Go/s",
                stability.MotsFaux == 0 ? $"{stability.Passes} passes, 0 erreur" : $"{stability.MotsFaux} mot(s) faux"));
        return tiles;
    }
}
