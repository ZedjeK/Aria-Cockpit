using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Models;
using Aria.Core.Updates;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

/// <summary>État d'une source pendant la recherche (Windows Update, winget, NVIDIA).</summary>
public sealed partial class SourceStatus(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty] private string text = "RECHERCHE…";
    [ObservableProperty] private bool isPending = true;
    [ObservableProperty] private Severity tone = Severity.Info;

    public void Done(string result, Severity resultTone)
    {
        Text = result.ToUpperInvariant();
        Tone = resultTone;
        IsPending = false;
    }
}

/// <summary>Application ouverte affichée dans l'avertissement.</summary>
public sealed record OpenAppRow(string Name, string Processes);

/// <summary>Choix de l'utilisateur face aux applications ouvertes.</summary>
public enum OpenAppsChoice { Close, Skip, Cancel }

/// <summary>Ligne du résultat d'installation.</summary>
public sealed record UpdateResultRow(string Name, string Detail, string Status, Severity Tone, string? Error)
{
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
}

public sealed partial class UpdatesViewModel : ObservableObject
{
    private const string NvidiaAppPath = @"C:\Program Files\NVIDIA Corporation\NVIDIA App\CEF\NVIDIA App.exe";

    private readonly AppState _state;
    private WindowsUpdateService _windowsUpdate = new();
    private WingetService _winget = new();
    private NvidiaDriverCheck? _nvidia;
    private string? _biosSearch;
    private CancellationTokenSource? _stop;

    private const int CountdownSeconds = 20;
    private TaskCompletionSource<OpenAppsChoice>? _openAppsChoice;
    private DispatcherTimer? _countdown;
    private DateTime _countdownStart;

    // ---- États de la page ----
    [ObservableProperty] private bool hasSearched;
    [ObservableProperty] private string subtitle = "Windows Update, applications et pilote NVIDIA : rien n'est installé sans votre accord.";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(InstallCommand))]
    private bool isSearching;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SearchCommand), nameof(InstallCommand), nameof(StopCommand))]
    private bool isInstalling;

    [ObservableProperty] private bool showResults;

    public ObservableCollection<SourceStatus> Sources { get; } = [];

    // ---- Liste ----
    [ObservableProperty] private IReadOnlyList<CheckGroup> groups = [];
    [ObservableProperty] private bool hasItems;
    [ObservableProperty] private bool hasNoItems;
    [ObservableProperty] private IReadOnlyList<SimpleRow> skipped = [];
    [ObservableProperty] private bool hasSkipped;
    [ObservableProperty] private string skippedHeader = "";
    [ObservableProperty] private string errors = "";
    [ObservableProperty] private bool hasErrors;

    // ---- Sélection ----
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private int selectedCount;

    [ObservableProperty] private string selectionText = "";
    [ObservableProperty] private string selectionDetail = "";
    [ObservableProperty] private string installText = "Installer";

    // ---- Cartes pilote NVIDIA et BIOS ----
    [ObservableProperty] private bool showNvidia;
    [ObservableProperty] private string nvidiaMain = "";
    [ObservableProperty] private string nvidiaDetail = "";
    [ObservableProperty] private Severity nvidiaTone = Severity.Info;
    [ObservableProperty] private bool nvidiaCanDownload;
    [ObservableProperty] private string nvidiaDownloadText = "Télécharger";
    [ObservableProperty] private bool hasNvidiaApp;
    [ObservableProperty] private bool showBios;
    [ObservableProperty] private string biosMain = "";
    [ObservableProperty] private string biosDetail = "";

    // ---- Applications ouvertes ----
    [ObservableProperty] private bool showOpenApps;
    [ObservableProperty] private IReadOnlyList<OpenAppRow> openApps = [];
    [ObservableProperty] private string countdownText = "";
    [ObservableProperty] private double countdownFraction = 1;

    // ---- Installation en cours ----
    [ObservableProperty] private string progressDetail = "";
    [ObservableProperty] private double itemPercent;
    [ObservableProperty] private bool itemIndeterminate = true;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string progressCounter = "";
    [ObservableProperty] private double progressValue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool stopRequested;

    // ---- Résultat ----
    [ObservableProperty] private IReadOnlyList<UpdateResultRow> results = [];
    [ObservableProperty] private int installedCount;
    [ObservableProperty] private int failedCount;
    [ObservableProperty] private int otherCount;
    [ObservableProperty] private bool needsReboot;
    [ObservableProperty] private IReadOnlyList<string> resultNotes = [];
    [ObservableProperty] private string? reportPath;

    public UpdatesViewModel(AppState state) => _state = state;

    private IEnumerable<CheckItem> AllItems => Groups.SelectMany(g => g.Items);

    // ================================================================ Recherche

    private bool CanSearch() => !IsSearching && !IsInstalling;

    [RelayCommand(CanExecute = nameof(CanSearch))]
    private async Task Search()
    {
        IsSearching = true;
        ShowResults = false;
        Sources.Clear();

        var hw = _state.Analysis?.Hardware;
        var wuStatus = new SourceStatus("Windows Update");
        var appStatus = new SourceStatus("Applications (winget)");
        Sources.Add(wuStatus);
        Sources.Add(appStatus);
        SourceStatus? nvidiaStatus = null;
        if (hw?.HasNvidiaGpu == true)
        {
            nvidiaStatus = new SourceStatus("Pilote NVIDIA");
            Sources.Add(nvidiaStatus);
        }

        // Nouvelles instances : les objets Windows Update de la recherche servent ensuite à l'installation
        var windowsUpdate = _windowsUpdate = new WindowsUpdateService();
        var winget = _winget = new WingetService();

        try
        {
            var wuTask = Track(Task.Run(windowsUpdate.Search), wuStatus);
            var appTask = Track(Task.Run(winget.Search), appStatus);
            var nvidiaTask = hw is not null && nvidiaStatus is not null
                ? TrackNvidia(hw, nvidiaStatus)
                : Task.FromResult<NvidiaDriverCheck?>(null);

            await Task.WhenAll(wuTask, appTask, nvidiaTask);
            ShowSearch(wuTask.Result, appTask.Result, nvidiaTask.Result);
        }
        catch (Exception e)
        {
            Errors = $"Recherche interrompue : {e.Message} (détails : {ErrorLog.Write(e)})";
            HasErrors = true;
        }
        finally
        {
            IsSearching = false;
            HasSearched = true;
            Subtitle = $"Recherche effectuée à {DateTime.Now:HH:mm} · rien n'est installé sans votre accord.";
        }
    }

    private static async Task<ProviderResult> Track(Task<ProviderResult> search, SourceStatus status)
    {
        var result = await search;
        if (result.Error is not null) status.Done("erreur", Severity.Critical);
        else status.Done(
            result.Items.Count switch { 0 => "à jour", 1 => "1 mise à jour", var n => $"{n} mises à jour" },
            result.Items.Count > 0 ? Severity.Warning : Severity.Ok);
        return result;
    }

    private static async Task<NvidiaDriverCheck?> TrackNvidia(HardwareProfile hw, SourceStatus status)
    {
        var check = await NvidiaDriverService.CheckAsync(hw);
        if (check is null) status.Done("aucune carte", Severity.Info);
        else if (check.Error is not null) status.Done("vérification impossible", Severity.Info);
        else if (check.UpdateAvailable) status.Done($"{check.LatestVersion} disponible", Severity.Warning);
        else status.Done("à jour", Severity.Ok);
        return check;
    }

    private void ShowSearch(ProviderResult windowsUpdate, ProviderResult apps, NvidiaDriverCheck? nvidia)
    {
        // Liste à cocher : ce qui est coché d'office en premier
        var groups = new List<CheckGroup>();
        if (windowsUpdate.Items.Count > 0)
            groups.Add(new CheckGroup("Windows Update",
                windowsUpdate.Items.OrderByDescending(i => i.SelectedByDefault).Select(ToCheckItem)));
        if (apps.Items.Count > 0)
            groups.Add(new CheckGroup("Applications",
                apps.Items.OrderByDescending(i => i.SelectedByDefault).ThenBy(i => i.Title, StringComparer.CurrentCultureIgnoreCase).Select(ToCheckItem)));
        Groups = groups;
        foreach (var item in AllItems)
            item.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(CheckItem.IsSelected)) UpdateSelection(); };

        HasItems = groups.Count > 0;
        HasNoItems = !HasItems && windowsUpdate.Error is null && apps.Error is null;

        Skipped = apps.Skipped.Concat(windowsUpdate.Skipped).Select(s => new SimpleRow(s.Title, s.Reason)).ToList();
        HasSkipped = Skipped.Count > 0;
        SkippedHeader = $"Non proposées ({Skipped.Count})";

        var errors = new List<string>();
        if (windowsUpdate.Error is not null) errors.Add($"Windows Update : {windowsUpdate.Error}");
        if (apps.Error is not null) errors.Add($"Applications : {apps.Error}");
        Errors = string.Join("\n", errors);
        HasErrors = errors.Count > 0;

        ShowNvidiaCard(nvidia);
        ShowBiosCard();
        UpdateSelection();
    }

    private static CheckItem ToCheckItem(UpdateItem item)
    {
        var f = item.Flags;
        var badges = new List<Badge>();
        if (f.HasFlag(UpdateFlags.Security)) badges.Add(new Badge("SÉCURITÉ", BadgeKind.Good));
        if (f.HasFlag(UpdateFlags.Definitions)) badges.Add(new Badge("ANTIVIRUS", BadgeKind.Good));
        if (f.HasFlag(UpdateFlags.Major)) badges.Add(new Badge("MAJEURE", BadgeKind.Caution));
        if (f.HasFlag(UpdateFlags.Driver)) badges.Add(new Badge("PILOTE", BadgeKind.Caution));
        if (f.HasFlag(UpdateFlags.Optional)) badges.Add(new Badge("FACULTATIVE", BadgeKind.Info));
        if (f.HasFlag(UpdateFlags.UnknownVersion)) badges.Add(new Badge("VERSION INCONNUE", BadgeKind.Info));
        if (f.HasFlag(UpdateFlags.Reboot)) badges.Add(new Badge("REDÉMARRAGE", BadgeKind.Reboot));

        // Applications : « ancienne → nouvelle version » ; Windows Update : taille du téléchargement
        var (from, to) = item.Source == UpdateSource.Application
            ? (item.CurrentVersion ?? "inconnue", item.NewVersion ?? "")
            : ("", item.SizeBytes is > 0 ? Size(item.SizeBytes.Value) : "");

        return new CheckItem(item.Title, from, to, item.Reason, item.SelectedByDefault, item, badges);
    }

    private void ShowNvidiaCard(NvidiaDriverCheck? check)
    {
        _nvidia = check;
        ShowNvidia = check is not null;
        HasNvidiaApp = File.Exists(NvidiaAppPath);
        NvidiaCanDownload = false;
        if (check is null) return;

        var installed = check.InstalledVersion ?? "?";
        var date = check.LatestDate is { } d ? $" du {d:dd/MM/yyyy}" : "";

        if (check.Error is not null)
        {
            NvidiaMain = $"Pilote {installed}";
            NvidiaDetail = check.Error;
            NvidiaTone = Severity.Info;
        }
        else if (check.UpdateAvailable)
        {
            NvidiaMain = $"{installed} → {check.LatestVersion}";
            NvidiaDetail = $"Nouveau pilote Game Ready{date}. Téléchargez-le puis lancez l'installateur NVIDIA " +
                           "(option « Installation express ») : l'écran peut clignoter pendant l'installation.";
            NvidiaTone = Severity.Warning;
            NvidiaCanDownload = check.DownloadUrl is not null;
            NvidiaDownloadText = check.FileSize is { } size ? $"Télécharger ({size.Replace("MB", "Mo")})" : "Télécharger";
        }
        else
        {
            NvidiaMain = $"{installed} · à jour";
            NvidiaDetail = $"Dernier pilote Game Ready : {check.LatestVersion}{date}.";
            NvidiaTone = Severity.Ok;
        }
    }

    private void ShowBiosCard()
    {
        if (_state.Analysis is not { } analysis)
        {
            ShowBios = false;
            return;
        }

        var board = analysis.Hardware.Board;
        var brand = analysis.Bios.IsGeneric ? board.Manufacturer : analysis.Bios.Brand.Brand;
        ShowBios = true;
        BiosMain = board.BiosDate is { } date ? $"BIOS {board.BiosVersion} du {date:dd/MM/yyyy}" : $"BIOS {board.BiosVersion}";
        BiosDetail = $"{brand} {board.Product}. Aria ne peut pas savoir s'il existe une version plus récente : " +
                     $"comparez sur la page support du fabricant, puis mettez à jour avec {analysis.Bios.Brand.UpdateTool}.";
        _biosSearch = $"https://www.google.com/search?q={Uri.EscapeDataString($"{brand} {board.Product} BIOS support")}";
    }

    private void UpdateSelection()
    {
        var selected = AllItems.Where(i => i.IsSelected).Select(i => (UpdateItem)i.Payload).ToList();
        SelectedCount = selected.Count;
        SelectionText = selected.Count switch
        {
            0 => "Aucune mise à jour sélectionnée",
            1 => "1 mise à jour sélectionnée",
            var n => $"{n} mises à jour sélectionnées"
        };

        var size = selected.Sum(i => i.SizeBytes ?? 0);
        var reboot = selected.Any(i => i.Flags.HasFlag(UpdateFlags.Reboot));
        SelectionDetail = (size > 0 ? $"Windows Update : environ {Size(size)} à télécharger · " : "") +
                          (reboot ? "redémarrage nécessaire · " : "") +
                          "point de restauration créé avant l'installation.";
        InstallText = $"Installer la sélection ({selected.Count})";
    }

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var item in AllItems) item.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var item in AllItems) item.IsSelected = false;
    }

    // ================================================================ Installation

    private bool CanInstall() => SelectedCount > 0 && !IsInstalling && !IsSearching;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task Install()
    {
        var chosen = AllItems.Where(i => i.IsSelected).Select(i => (UpdateItem)i.Payload).ToList();
        var total = AllItems.Count();

        var rows = chosen.Select(i => new DialogRow(i.Title,
            i.Source == UpdateSource.WindowsUpdate ? null : i.CurrentVersion,
            i.NewVersion)).ToList();

        var warnings = new List<string>
        {
            "Fermez les applications à mettre à jour : une application ouverte peut bloquer sa mise à jour.",
            "Aria ne peut pas désinstaller une mise à jour : un point de restauration Windows est créé juste avant."
        };
        if (chosen.Any(i => i.Flags.HasFlag(UpdateFlags.Driver)))
            warnings.Add("Vous avez coché un pilote Windows Update : il peut remplacer un pilote plus récent.");
        if (chosen.Any(i => i.Flags.HasFlag(UpdateFlags.Major)))
            warnings.Add("Vous avez coché une nouvelle version de Windows : prévoyez du temps et plusieurs redémarrages.");

        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Installer les mises à jour",
                ConfirmText: $"Installer ({chosen.Count})",
                Rows: rows,
                Warnings: warnings)))
            return;

        // Applications à mettre à jour actuellement ouvertes : avertissement avec compte à rebours
        IReadOnlyList<RunningApp> running = [];
        try
        {
            var toCheck = chosen;
            running = await Task.Run(() => RunningApps.Find(toCheck));
        }
        catch (Exception e)
        {
            ErrorLog.Write(e); // la détection est un confort : sans elle, winget signalera l'application ouverte
        }

        if (running.Count > 0)
        {
            switch (await AskToCloseAsync(running))
            {
                case OpenAppsChoice.Cancel:
                    return;
                case OpenAppsChoice.Skip:
                    var skipped = running.Select(r => r.Item).ToHashSet();
                    chosen = chosen.Where(i => !skipped.Contains(i)).ToList();
                    running = [];
                    if (chosen.Count == 0) return;
                    break;
            }
        }

        IsInstalling = true;
        StopRequested = false;
        ProgressValue = 0;
        ProgressCounter = "PRÉPARATION";
        ProgressText = "";
        _stop = new CancellationTokenSource();

        ProgressDetail = "";
        ItemIndeterminate = true;
        var progress = new Progress<InstallStep>(step =>
        {
            ProgressText = step.Text;
            ProgressDetail = step.Detail ?? "";
            ItemIndeterminate = step.Fraction is null;
            ItemPercent = (step.Fraction ?? 0) * 100;

            // Barre générale : éléments terminés + avancement de l'élément en cours
            var fraction = step.Done is > 0 && step.Done < step.Total ? step.Fraction ?? 0 : 0;
            ProgressValue = step.Total == 0 ? 0 : Math.Min(100, 100.0 * (step.Done + fraction) / step.Total);

            var count = step.Total - 1;
            ProgressCounter = step.Done == 0 ? "PRÉPARATION" : step.Done > count ? "TERMINÉ" : $"{step.Done} / {count}";
        });

        try
        {
            var (windowsUpdate, winget, stop, toInstall, toClose) = (_windowsUpdate, _winget, _stop.Token, chosen, running);
            var outcome = await Task.Run(() => UpdateInstaller.Install(toInstall, windowsUpdate, winget, toClose, progress, stop));
            ShowOutcome(outcome, total - chosen.Count);
        }
        catch (Exception e)
        {
            Results = [];
            ResultNotes = [$"Erreur inattendue : {e.Message}", $"Détails : {ErrorLog.Write(e)}"];
            ShowResults = true;
        }
        finally
        {
            IsInstalling = false;
            _stop.Dispose();
            _stop = null;
        }
    }

    // ================================================================ Applications ouvertes

    /// <summary>Affiche l'avertissement et attend le choix ; à zéro, les applications sont fermées.</summary>
    private Task<OpenAppsChoice> AskToCloseAsync(IReadOnlyList<RunningApp> running)
    {
        OpenApps = running.Select(r => new OpenAppRow(r.Item.Title, string.Join(", ", r.ProcessNames))).ToList();
        _openAppsChoice = new TaskCompletionSource<OpenAppsChoice>();
        _countdownStart = DateTime.UtcNow;
        UpdateCountdown();
        ShowOpenApps = true;

        _countdown = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _countdown.Tick += (_, _) =>
        {
            if (UpdateCountdown() <= 0) ResolveOpenApps(OpenAppsChoice.Close);
        };
        _countdown.Start();
        return _openAppsChoice.Task;
    }

    private double UpdateCountdown()
    {
        var remaining = Math.Max(0, CountdownSeconds - (DateTime.UtcNow - _countdownStart).TotalSeconds);
        CountdownFraction = remaining / CountdownSeconds;
        CountdownText = Math.Ceiling(remaining).ToString("0");
        return remaining;
    }

    private void ResolveOpenApps(OpenAppsChoice choice)
    {
        _countdown?.Stop();
        _countdown = null;
        ShowOpenApps = false;
        _openAppsChoice?.TrySetResult(choice);
    }

    [RelayCommand]
    private void CloseAppsNow() => ResolveOpenApps(OpenAppsChoice.Close);

    [RelayCommand]
    private void SkipOpenApps() => ResolveOpenApps(OpenAppsChoice.Skip);

    [RelayCommand]
    private void CancelOpenApps() => ResolveOpenApps(OpenAppsChoice.Cancel);

    private bool CanStop() => IsInstalling && !StopRequested;

    /// <summary>L'élément en cours se termine (l'interrompre pourrait abîmer l'installation), les suivants sont annulés.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        StopRequested = true;
        _stop?.Cancel();
    }

    private void ShowOutcome(InstallOutcome outcome, int notSelected)
    {
        Results = outcome.Results.Select(r => new UpdateResultRow(
            r.Item.Title,
            r.Item.Source == UpdateSource.Application ? $"{r.Item.CurrentVersion ?? "inconnue"} → {r.Item.NewVersion}" : "",
            r.Status switch
            {
                InstallStatus.Installed => r.RebootRequired ? "INSTALLÉ · AU REDÉMARRAGE" : "INSTALLÉ",
                InstallStatus.UpToDate => "DÉJÀ À JOUR",
                InstallStatus.Canceled => "ANNULÉ",
                _ => "ÉCHEC"
            },
            r.Status switch
            {
                InstallStatus.Installed => Severity.Ok,
                InstallStatus.Failed => Severity.Critical,
                _ => Severity.Info
            },
            r.Error)).ToList();

        InstalledCount = outcome.Results.Count(r => r.Status == InstallStatus.Installed);
        FailedCount = outcome.Results.Count(r => r.Status == InstallStatus.Failed);
        OtherCount = outcome.Results.Count(r => r.Status is InstallStatus.UpToDate or InstallStatus.Canceled) + notSelected;
        NeedsReboot = outcome.Results.Any(r => r.RebootRequired);

        var notes = new List<string> { outcome.RestorePointMessage };
        if (outcome.ClosedApps is { Count: > 0 } closed) notes.AddRange(closed);
        try
        {
            ReportPath = UpdateReport.Save(outcome, notSelected, _nvidia);
            notes.Add($"Rapport : {ReportPath}");
        }
        catch (Exception e)
        {
            ReportPath = null;
            notes.Add($"Rapport non enregistré : {e.Message}");
        }
        ResultNotes = notes;
        ShowResults = true;
    }

    // ================================================================ Boutons divers

    [RelayCommand]
    private void DownloadNvidia()
    {
        if (_nvidia?.DownloadUrl is { } url) Shell.OpenUnelevated(url);
    }

    [RelayCommand]
    private void OpenNvidiaDetails()
    {
        if (_nvidia?.DetailsUrl is { } url) Shell.OpenUnelevated(url);
    }

    [RelayCommand]
    private void OpenNvidiaApp()
    {
        if (File.Exists(NvidiaAppPath)) Shell.OpenUnelevated(NvidiaAppPath);
    }

    [RelayCommand]
    private void SearchBios()
    {
        if (_biosSearch is { } url) Shell.OpenUnelevated(url);
    }

    [RelayCommand]
    private void OpenReport()
    {
        if (ReportPath is { } path && File.Exists(path)) Shell.OpenFile(path);
    }

    [RelayCommand]
    private async Task Restart()
    {
        if (await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Redémarrer",
                ConfirmText: "Redémarrer",
                Message: "Le PC va redémarrer dans 10 secondes pour terminer l'installation.",
                Warnings: ["Enregistrez votre travail avant de continuer."],
                IsRisky: true)))
            RestartService.Restart(intoBios: false);
    }

    /// <summary>Retour à la liste, avec une nouvelle recherche pour afficher ce qui reste.</summary>
    [RelayCommand]
    private async Task Finish()
    {
        ShowResults = false;
        await Search();
    }

    private static string Size(long bytes) =>
        bytes >= 1_000_000_000 ? $"{bytes / 1e9:0.#} Go" : $"{Math.Max(1, bytes / 1e6):0} Mo";
}
