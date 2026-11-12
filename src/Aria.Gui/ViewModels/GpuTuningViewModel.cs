using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
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

    /// <summary>Carte « Ventilateur » : courbe de ventilation, indépendante des curseurs.</summary>
    public FanCurveViewModel Fans { get; } = new();

    /// <summary>Assistant guidé : pendant qu'il travaille, le réglage manuel est en pause.</summary>
    public TuningAssistantViewModel Assistant { get; }

    public GpuTuningViewModel()
    {
        Assistant = new TuningAssistantViewModel(() => _tuner, () => RefreshAsync(resetSliders: true));
        Assistant.WatchMeasurements();
        Assistant.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(TuningAssistantViewModel.Record) or nameof(TuningAssistantViewModel.DriverChanged))
            {
                OnPropertyChanged(nameof(HasBalanced));
                OnPropertyChanged(nameof(BalancedDriverChanged));
            }
            if (e.PropertyName != nameof(TuningAssistantViewModel.IsActive)) return;
            ApplyCommand.NotifyCanExecuteChanged();
            RevertCommand.NotifyCanExecuteChanged();
            ResetSlidersCommand.NotifyCanExecuteChanged();
            LoadBalancedCommand.NotifyCanExecuteChanged();
            ApplyProfileCommand.NotifyCanExecuteChanged();
            LoadProfileCommand.NotifyCanExecuteChanged();
        };
        Assistant.SaveProfileRequested += async (state, name, note) => await SaveProfileAsync(state, name, note);
    }

    /// <summary>Un résultat d'Équilibre existe pour cette carte : il peut être rechargé dans les curseurs.</summary>
    public bool HasBalanced => Assistant.Record?.ResultState is not null;

    /// <summary>Le pilote NVIDIA a changé depuis Équilibre : son résultat reste chargeable, mais le refaire est conseillé.</summary>
    public bool BalancedDriverChanged => HasBalanced && Assistant.DriverChanged;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(RevertCommand), nameof(ResetSlidersCommand), nameof(LoadBalancedCommand), nameof(ApplyProfileCommand), nameof(LoadProfileCommand), nameof(SaveProfileCommand))] private bool isAvailable;
    [ObservableProperty] private bool capSupported = true;
    [ObservableProperty] private bool powerSupported = true;
    [ObservableProperty] private bool coreOffsetSupported = true;
    [ObservableProperty] private bool memoryOffsetSupported = true;
    [ObservableProperty] private string capNote = "";
    [ObservableProperty] private string powerNote = "";
    [ObservableProperty] private bool isUnavailable;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(ApplyCommand), nameof(RevertCommand), nameof(ResetSlidersCommand), nameof(LoadBalancedCommand), nameof(ApplyProfileCommand), nameof(LoadProfileCommand), nameof(SaveProfileCommand))] private bool isBusy;

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
    /// <summary>Nom du profil enregistré qui est sur la carte (« Mon réglage équilibré ») ; vide si le réglage en place n'en est pas un.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasCurrentProfile))] private string currentProfile = "";
    public bool HasCurrentProfile => CurrentProfile.Length > 0;
    [ObservableProperty] private string originText = "";
    [ObservableProperty] private bool hasOrigin;
    [ObservableProperty] private string message = "";
    [ObservableProperty] private bool hasMessage;
    [ObservableProperty] private bool messageIsError;

    // ---- Garde-fous ----
    [ObservableProperty] private string crashText = "";
    [ObservableProperty] private bool hasCrashes;
    /// <summary>Accès mémoire invalides avec un overclock mémoire actif : le bandeau propose de remettre la mémoire à +0.</summary>
    [ObservableProperty] private string taskText = "";
    [ObservableProperty] private bool hasTaskProblem;
    [ObservableProperty] private string otherGpuText = "";
    [ObservableProperty] private bool hasOtherGpu;

    // ---- Incidents du pilote, commentés ----
    /// <summary>Période affichée et couverte par le rapport d'incidents.</summary>
    public const int IncidentDays = 30;

    public ObservableCollection<IncidentRow> Incidents { get; } = [];
    [ObservableProperty] private bool hasIncidents;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasIncidentNote))] private string incidentNote = "";
    [ObservableProperty] private bool incidentNoteIsError;
    [ObservableProperty] private bool canOpenIncidentReport;
    private string? _incidentReportPath;

    /// <summary>Incidents affichés d'emblée ; les plus anciens se déplient à la demande (le rapport les contient tous).</summary>
    public const int IncidentsShown = 5;
    [ObservableProperty] private bool hasOlderIncidents;
    [ObservableProperty] private string olderIncidentsText = "";

    public bool HasIncidentNote => IncidentNote.Length > 0;

    // ---- Profils enregistrés ----
    public ObservableCollection<GpuProfileRow> Profiles { get; } = [];
    [ObservableProperty] private bool hasProfiles;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))] private string profileName = "";

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
        if (_tuner is null) return;
        await RefreshAsync(resetSliders: Current is null || !HasChanges);
        await Assistant.LoadRecordAsync();
        await LoadProfilesAsync();
        await Fans.LoadAsync();
    }

    public void OnHidden() => Telemetry.Stop();

    private void SetRanges(GpuTuningLimits l)
    {
        // Réglages que la carte accepte : les autres curseurs sont désactivés, avec la raison
        CapSupported = l.SupportsClockLock;
        PowerSupported = l.SupportsPowerLimit;
        CoreOffsetSupported = l.SupportsCoreOffset;
        MemoryOffsetSupported = l.SupportsMemoryOffset;
        CapNote = CapSupported ? "" : $"Indisponible sur votre carte ({GpuCatalog.Describe(l.Architecture)}) : le plafond de fréquence demande une carte Turing (RTX 20, GTX 16) ou plus récente.";
        PowerNote = PowerSupported ? "" : "Verrouillée par le pilote sur cette carte (fréquent sur les portables, qui gèrent leur puissance eux-mêmes).";
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
        var (state, origin, saved, crashes, taskCommand, recent, notes) = await Task.Run(() =>
        {
            var s = GpuProfile.ReadCurrent(tuner);
            var file = GpuProfile.Load();
            var crashList = file is null ? [] : DriverIncidents.Since(file.AppliedAt);
            var command = s.AtStartup ? StartupTask.Command() : null;
            return (s, GpuProfile.OriginFor(tuner), file, crashList, command,
                DriverIncidents.DetailedSince(DateTime.Now.AddDays(-IncidentDays)), IncidentNotes.Load());
        });
        ShowIncidents(recent, notes);

        // Réglage enregistré pour une autre carte (carte changée) : rien n'est réappliqué, on le signale
        HasOtherGpu = saved?.IsForOtherGpu(tuner.Identity) == true;
        OtherGpuText = HasOtherGpu
            ? $"Le réglage enregistré concerne « {saved!.GpuName} », alors que votre carte actuelle est « {tuner.Identity.Name} ». " +
              "Il n'est pas réappliqué au démarrage. Oubliez-le pour repartir de zéro avec cette carte."
            : "";
        if (HasOtherGpu) saved = null;

        Current = state;
        CurrentText = state.Describe();
        MarkInPlace();
        HasOrigin = origin is not null;
        OriginText = origin?.Describe() ?? "";
        if (resetSliders) LoadSliders(state);

        // Incidents du pilote depuis le dernier réglage d'Aria, avec un conseil selon leur nature
        HasCrashes = saved is not null && crashes.Count > 0;
        CrashText = HasCrashes ? DriverIncidents.Advise(crashes, saved!.AppliedAt, state) : "";

        // Tâche de démarrage absente ou ne lançant pas la copie protégée d'Aria (Program Files)
        var exe = Aria.Core.Settings.LogonExe.PathFor(Environment.ProcessPath ?? "Aria-Cockpit.exe");
        var wanted = saved is not null && GpuTuningState.TryParse(saved.Current, out var keep) && keep.AtStartup;
        HasTaskProblem = wanted && (!state.AtStartup || !string.Equals(taskCommand, exe, StringComparison.OrdinalIgnoreCase));
        TaskText = !HasTaskProblem ? ""
            : !state.AtStartup
                ? "Le réglage devait être réappliqué au démarrage, mais la tâche planifiée « Aria Cockpit\\Profil GPU » a disparu."
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
        if (_tuner.Limits.DefaultPowerMilliwatts > 0 && target.PowerLimitMilliwatts > _tuner.Limits.DefaultPowerMilliwatts)
            warnings.Add($"Limite de puissance au-dessus de la valeur d'usine ({target.PowerLimitWatts:0} W au lieu de {_tuner.Limits.DefaultPowerMilliwatts / 1000} W) : " +
                         "vérifiez que votre alimentation a la puissance recommandée et que le câble d'alimentation de la carte (12V-2x6 / 12VHPWR) est enfoncé à fond.");
        if (target.MemoryOffsetMHz > 1000)
            warnings.Add("Mémoire au-delà de +1000 MHz : la GDDR6X corrige ses propres erreurs, une mémoire trop poussée ne plante pas mais devient plus lente.");
        if (target.AtStartup && !Current.AtStartup)
            warnings.Add("« Garder au démarrage » crée la tâche planifiée « Aria Cockpit\\Profil GPU » : Aria Cockpit se lancera en silence, avec les droits administrateur, à chaque ouverture de session pour réappliquer ce réglage.");

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
                Message: OriginText.Length > 0 ? $"La carte retrouve ses valeurs d'usine : {OriginText}." : "La carte retrouve ses valeurs d'usine.",
                Note: "La tâche de démarrage « Aria Cockpit\\Profil GPU » est supprimée si elle existe.")))
            return;
        await RunAsync(() => GpuProfile.RevertToOrigin(_tuner, ExePath()), "Valeurs d'usine remises.");
    }

    /// <summary>Remet les curseurs sur le réglage en place, sans rien appliquer.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void ResetSliders()
    {
        if (Current is { } c) LoadSliders(c);
    }

    /// <summary>
    /// Charge le résultat d'Équilibre dans les curseurs, sans rien appliquer : « Appliquer » le pose ensuite
    /// (confirmation, case « Garder au démarrage » laissée telle quelle, journal).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void LoadBalanced()
    {
        if (Assistant.Record?.ResultState is { } balanced) LoadSliders(balanced with { AtStartup = AtStartup });
    }

    private async Task LoadProfilesAsync()
    {
        if (_tuner is not { } t) return;
        var profiles = await Task.Run(() => GpuProfiles.For(t.Identity));
        Profiles.Clear();
        foreach (var p in profiles)
            Profiles.Add(new GpuProfileRow(p, p.Tuning!.Describe(), p.Details, p.DriverChanged(t.Identity)));
        HasProfiles = Profiles.Count > 0;
        MarkInPlace();
    }

    /// <summary>Repère « EN PLACE » : sur le profil enregistré que la carte a en ce moment, s'il y en a un.</summary>
    private void MarkInPlace()
    {
        foreach (var row in Profiles) row.IsActive = Current is { } card && row.Profile.Tuning is { } tuning && card.SameTuning(tuning);
        CurrentProfile = Profiles.FirstOrDefault(r => r.IsActive)?.Profile.Name ?? "";
    }

    private void ClearNotes()
    {
        foreach (var row in Profiles) row.Say("");
    }

    private bool CanSaveProfile() => IsAvailable && !IsBusy && ProfileName.Trim().Length > 0;

    /// <summary>Enregistre le réglage des curseurs (celui en place si on n'y a pas touché) sous le nom tapé.</summary>
    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfile()
    {
        if (await SaveProfileAsync(Target(), ProfileName.Trim(), "Réglage manuel")) ProfileName = "";
    }

    /// <summary>Enregistre un profil (après confirmation s'il en remplace un) ; vrai s'il est enregistré.</summary>
    private async Task<bool> SaveProfileAsync(GpuTuningState state, string name, string note)
    {
        if (_tuner is not { } t) return false;
        var gpu = t.Identity;
        state = state with { AtStartup = false };
        if (GpuProfiles.Find(gpu, name) is { } old && !await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Remplacer le profil",
                ConfirmText: "Remplacer",
                Message: $"Un profil « {old.Name} » existe déjà pour cette carte.",
                Rows: [new("Réglage", old.Tuning?.Describe(), state.Describe())])))
            return false;
        try
        {
            var profile = new SavedGpuProfile(name, state.ToRaw(), DateTime.Now, gpu.Uuid, gpu.Name, gpu.DriverVersion, note);
            await Task.Run(() => GpuProfiles.Save(profile, gpu));
            Show($"Profil « {name} » enregistré.", error: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Show($"Profil non enregistré : {e.Message}", error: true);
            return false;
        }
        await LoadProfilesAsync();
        return true;
    }

    /// <summary>
    /// « Appliquer » d'un profil enregistré : il est posé sur la carte tel quel, après la confirmation habituelle
    /// (avant → après, avertissements). « Garder au démarrage » reste comme il est.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task ApplyProfile(GpuProfileRow row)
    {
        if (row.Profile.Tuning is not { } tuning || Current is not { } before) return;
        ClearNotes();
        if (before.SameTuning(tuning))
        {
            row.Say("Déjà en place sur la carte.");
            return;
        }
        HasMessage = false;
        MessageIsError = false;
        LoadSliders(tuning with { AtStartup = before.AtStartup });
        await Apply();
        if (Current is { } now && now.SameTuning(tuning)) row.Say("En place sur la carte.");
        else if (MessageIsError) row.Say(Message, error: true);
        else row.Say("Rien n'a été appliqué. Le profil est seulement affiché dans les curseurs.");
    }

    /// <summary>« Modifier » : charge un profil dans les curseurs, sans rien poser (case « Garder au démarrage » laissée telle quelle).</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void LoadProfile(GpuProfileRow row)
    {
        if (row.Profile.Tuning is not { } s) return;
        ClearNotes();
        LoadSliders(s with { AtStartup = AtStartup });
        row.Say("Affiché dans les curseurs : ajuste-les, puis « Appliquer » sous les curseurs. Rien n'est posé pour l'instant.");
    }

    [RelayCommand]
    private async Task DeleteProfile(GpuProfileRow row)
    {
        if (_tuner is not { } t) return;
        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Supprimer le profil",
                ConfirmText: "Supprimer",
                Message: $"« {row.Profile.Name} » : {row.Tuning}.",
                Note: "Le réglage en place sur la carte ne change pas.",
                Icon: "Delete24")))
            return;
        try
        {
            await Task.Run(() => GpuProfiles.Delete(row.Profile, t.Identity));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Show($"Profil non supprimé : {e.Message}", error: true);
        }
        await LoadProfilesAsync();
    }

    /// <summary>Carte changée : oublie le réglage de l'ancienne carte (profil et tâche de démarrage), sans toucher à la nouvelle.</summary>
    [RelayCommand]
    private Task ForgetOtherGpu() => RunAsync(GpuProfile.Forget, "Réglage de l'ancienne carte oublié.");

    /// <summary>Recrée la tâche de démarrage vers cet exe (clic de l'utilisateur).</summary>
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

    // ---------------------------------------------------------------- Incidents du pilote

    /// <summary>
    /// Liste des incidents des 30 derniers jours, les plus récents d'abord. Elle n'est refaite que si les incidents
    /// ont changé : un commentaire en cours de frappe n'est pas effacé par un rafraîchissement de la page.
    /// </summary>
    private void ShowIncidents(IReadOnlyList<DriverIncidentDetail> incidents, IReadOnlyDictionary<string, string> notes)
    {
        var ordered = incidents.OrderByDescending(d => d.Incident.At).ToList();
        if (!ordered.Select(d => d.Incident.At).SequenceEqual(Incidents.Select(r => r.Detail.Incident.At)))
        {
            Incidents.Clear();
            foreach (var detail in ordered)
                Incidents.Add(new IncidentRow(detail, notes.GetValueOrDefault(IncidentNotes.Key(detail.Incident.At), ""), SaveIncidentNote)
                {
                    IsShown = Incidents.Count < IncidentsShown
                });
            var older = Incidents.Count - IncidentsShown;
            HasOlderIncidents = older > 0;
            OlderIncidentsText = older > 1 ? $"VOIR LES {older} PLUS ANCIENS" : "VOIR LE PLUS ANCIEN";
        }
        HasIncidents = Incidents.Count > 0;
    }

    [RelayCommand]
    private void ShowOlderIncidents()
    {
        foreach (var row in Incidents) row.IsShown = true;
        HasOlderIncidents = false;
    }

    /// <summary>Commentaire d'un incident : enregistré dès que le champ perd le focus.</summary>
    private void SaveIncidentNote(IncidentRow row)
    {
        try
        {
            IncidentNotes.Set(row.Detail.Incident.At, row.Note);
            ShowIncident("Commentaire enregistré.", error: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowIncident($"Commentaire non enregistré : {e.Message}", error: true);
        }
    }

    /// <summary>Rapport des incidents, en Markdown : la carte, les réglages, chaque incident avec son commentaire.</summary>
    private async Task<string> BuildIncidentReportAsync()
    {
        var tuner = _tuner!;
        var incidents = Incidents.Select(r => r.Detail).ToList();
        var notes = Incidents.Where(r => r.Note.Trim().Length > 0)
            .ToDictionary(r => IncidentNotes.Key(r.Detail.Incident.At), r => r.Note.Trim());
        var profiles = Profiles.Select(p => (p.Profile.Name, p.Tuning)).ToList();
        var balanced = HasBalanced ? Assistant.RecordText : null;
        var fan = FanCurveService.Running?.Name;
        var current = Current;
        var version = System.Reflection.Assembly.GetEntryAssembly()?
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        return await Task.Run(() =>
        {
            var logPath = Path.Combine(Aria.Core.Backup.BackupStore.DataRoot, "gpu-profile.log");
            string[] log;
            try
            {
                log = File.Exists(logPath) ? File.ReadAllLines(logPath) : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log = [];
            }
            return IncidentReport.Build(new IncidentReportInput(
                DateTime.Now, version, tuner.Identity.Name, tuner.Identity.DriverVersion,
                System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                current, GpuProfile.OriginFor(tuner), GpuProfile.Load()?.AppliedAt,
                balanced, fan, profiles, log, incidents, notes, IncidentDays));
        });
    }

    [RelayCommand]
    private async Task CopyIncidentReport()
    {
        if (_tuner is null) return;
        try
        {
            System.Windows.Clipboard.SetText(await BuildIncidentReportAsync());
            ShowIncident("Rapport copié : colle-le dans une IA, ou dans un message. Il ne contient ni le nom du PC, ni le tien, ni le numéro de la carte.", error: false);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or IOException)
        {
            ShowIncident($"Rapport non copié : {e.Message}", error: true);
        }
    }

    [RelayCommand]
    private async Task SaveIncidentReport()
    {
        if (_tuner is null) return;
        try
        {
            var text = await BuildIncidentReportAsync();
            var folder = Path.Combine(Aria.Core.Backup.BackupStore.DataRoot, "Rapports");
            var path = Path.Combine(folder, $"incidents_{DateTime.Now:yyyy-MM-dd_HHmmss}.md");
            await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(path, text);
            });
            _incidentReportPath = path;
            CanOpenIncidentReport = true;
            ShowIncident($"Rapport enregistré : {path}", error: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowIncident($"Rapport non enregistré : {e.Message}", error: true);
        }
    }

    [RelayCommand]
    private void OpenIncidentReport()
    {
        try
        {
            if (_incidentReportPath is { } path && File.Exists(path)) Shell.OpenFile(path);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowIncident($"Rapport non ouvert : {e.Message}", error: true);
        }
    }

    private void ShowIncident(string text, bool error)
    {
        IncidentNote = text;
        IncidentNoteIsError = error;
    }

    private static string ExePath() => Environment.ProcessPath ?? "Aria-Cockpit.exe";
}

/// <summary>
/// Un incident du pilote dans la carte « Incidents du pilote » : quand, de quelle nature, ses lignes brutes, et ce que
/// l'utilisateur faisait à ce moment-là (le journal de Windows ne le dit pas).
/// </summary>
public sealed partial class IncidentRow(DriverIncidentDetail detail, string note, Action<IncidentRow> save) : ObservableObject
{
    private static readonly System.Globalization.CultureInfo French = System.Globalization.CultureInfo.GetCultureInfo("fr-FR");

    public DriverIncidentDetail Detail { get; } = detail;

    /// <summary>« mercredi 11/11 à 21:46 ».</summary>
    public string When { get; } = detail.Incident.At.ToString("dddd dd/MM 'à' HH:mm", French);

    /// <summary>« erreur du pilote · 2 lignes ».</summary>
    public string What { get; } =
        $"{DriverIncidents.Describe(detail.Incident.Kind)} · {detail.Lines.Count} ligne{(detail.Lines.Count > 1 ? "s" : "")}";

    /// <summary>Lignes du journal Système, résumées.</summary>
    public IReadOnlyList<string> Lines { get; } =
        detail.Lines.Select(l => $"{l.At:HH:mm:ss}  {l.Provider} {l.Id}  {IncidentReport.Compact(l.Data)}").ToList();

    /// <summary>Affiché dans la liste (les plus anciens sont repliés tant qu'on ne les demande pas).</summary>
    [ObservableProperty] private bool isShown = true;

    /// <summary>Ce que l'utilisateur faisait ; enregistré quand le champ perd le focus.</summary>
    [ObservableProperty] private string note = note;

    partial void OnNoteChanged(string value) => save(this);
}

/// <summary>Ligne de la carte MES PROFILS : le profil, s'il est sur la carte, et le résultat du dernier clic sur cette ligne.</summary>
/// <param name="driverChanged">Le pilote a changé depuis l'enregistrement : le profil reste applicable, mais à revérifier.</param>
public sealed partial class GpuProfileRow(SavedGpuProfile profile, string tuning, string details, bool driverChanged) : ObservableObject
{
    public SavedGpuProfile Profile { get; } = profile;
    public string Tuning { get; } = tuning;
    public string Details { get; } = details;
    public bool DriverChanged { get; } = driverChanged;

    /// <summary>C'est ce réglage que la carte a en ce moment.</summary>
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
