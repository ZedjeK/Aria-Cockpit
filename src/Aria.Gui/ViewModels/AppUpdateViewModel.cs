using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Updates;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

/// <summary>
/// Mise à jour d'Aria Cockpit : cherche la dernière version publiée, la propose, la télécharge sur demande dans le
/// cache du dossier des données, contrôle son empreinte, puis l'installe à la place de la version en cours, qui est
/// gardée de côté. Rien n'est téléchargé ni installé sans un clic. Un seul exemplaire, partagé par le bandeau de la
/// fenêtre, la fenêtre de mise à jour et la page Paramètres.
/// </summary>
public sealed partial class AppUpdateViewModel : ObservableObject
{
    public static AppUpdateViewModel Shared { get; } = new();

    /// <summary>
    /// Essai à la main : « --version-simulee 0.25.0 » fait comme si cette version était installée, pour dérouler la
    /// mise à jour sur la dernière release publiée sans attendre la suivante. Le fichier proposé reste celui de la
    /// release, contrôlé comme toujours ; la version mise de côté porte bien le vrai numéro.
    /// </summary>
    public const string SimulatedVersionArgument = "--version-simulee";

    private const string WorkLabel = "téléchargement de la mise à jour";
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private AppUpdate? _update;
    private AppUpdateAsset? _asset;
    private Version? _installed;
    private CancellationTokenSource? _cancel;

    public enum Step
    {
        Offer,
        Downloading,
        Ready,
        Mismatch,
        Failed,
        Installing,
        InstallFailed
    }

    /// <summary>Version réelle de cet exe ; null si elle est illisible.</summary>
    public static Version? RealVersion { get; } =
        SingleInstance.OwnVersion is { } text && AppUpdateChecker.TryParseVersion(text, out var version) ? version : null;

    /// <summary>La nouvelle version est en place : la fenêtre principale lance cet exe et arrête celle-ci.</summary>
    public event Action<string>? RelaunchRequested;

    // ---------------------------------------------------------------- Recherche

    /// <summary>Version plus récente trouvée par la dernière recherche ; null s'il n'y en a pas.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(AvailableLabel))] private AppUpdate? available;
    [ObservableProperty] private bool isChecking;
    [ObservableProperty] private string lastCheckText = "pas encore faite depuis ce lancement";

    public bool HasUpdate => Available is not null;
    public string AvailableLabel => Available is { } update ? $"VOIR LA {update.Latest.ToString(3)}" : "";
    public string InstalledVersionText => RealVersion?.ToString(3) ?? "?";

    /// <summary>Version comparée à celle de GitHub lors de la dernière recherche (la vraie, sauf argument d'essai).</summary>
    public string CheckedVersionText => _installed?.ToString(3) ?? InstalledVersionText;

    /// <summary>Version tenue pour installée : la vraie, ou celle de l'argument d'essai.</summary>
    private static Version? InstalledOrSimulated()
    {
        var args = Environment.GetCommandLineArgs();
        var at = Array.FindIndex(args, a => string.Equals(a, SimulatedVersionArgument, StringComparison.OrdinalIgnoreCase));
        return at >= 0 && at + 1 < args.Length && AppUpdateChecker.TryParseVersion(args[at + 1], out var simulated)
            ? simulated
            : RealVersion;
    }

    /// <summary>
    /// Demande à GitHub la dernière version publiée. Une seule requête ; rien n'est téléchargé. La recherche ne
    /// distingue pas « à jour » de « GitHub injoignable » : dans les deux cas, aucune version n'est proposée.
    /// </summary>
    public async Task CheckAsync()
    {
        if (IsChecking) return;
        if (InstalledOrSimulated() is not { } installed)
        {
            LastCheckText = "impossible : la version de cet exe est illisible";
            return;
        }
        IsChecking = true;
        try
        {
            var update = await AppUpdateChecker.CheckAsync(installed);
            _installed = installed;
            Available = update;
            LastCheckText = update is null
                ? $"à {DateTime.Now:HH:mm} · aucune version plus récente trouvée"
                : $"à {DateTime.Now:HH:mm} · {update.Latest.ToString(3)} disponible";
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private Task CheckNowAsync() => CheckAsync();

    /// <summary>Ouvre la fenêtre de mise à jour sur la version trouvée.</summary>
    [RelayCommand]
    private void OpenAvailable()
    {
        if (Available is { } update && _installed is { } installed) Open(update, installed);
    }

    // ---------------------------------------------------------------- Fenêtre de mise à jour

    [ObservableProperty] private bool isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffer), nameof(IsDownloading), nameof(IsReady), nameof(IsMismatch), nameof(IsFailed),
        nameof(IsInstalling), nameof(IsInstallFailed))]
    private Step current = Step.Offer;

    public bool IsOffer => Current == Step.Offer;
    public bool IsDownloading => Current == Step.Downloading;
    public bool IsReady => Current == Step.Ready;
    public bool IsMismatch => Current == Step.Mismatch;
    public bool IsFailed => Current == Step.Failed;
    public bool IsInstalling => Current == Step.Installing;
    public bool IsInstallFailed => Current == Step.InstallFailed;

    [ObservableProperty] private string installedVersion = "";
    [ObservableProperty] private string latestVersion = "";
    [ObservableProperty] private bool hasNotes;
    [ObservableProperty] private string fileText = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CannotDownload))] private bool canDownload;
    [ObservableProperty] private double progress;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string expectedHash = "";
    [ObservableProperty] private string actualHash = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasActualHash))] private bool hashKnown;
    [ObservableProperty] private string errorText = "";
    [ObservableProperty] private string readyFile = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CannotInstall))] private bool canInstall;
    [ObservableProperty] private string installBlockedText = "";
    [ObservableProperty] private string keepText = "";

    public bool HasActualHash => HashKnown;
    public bool CannotDownload => !CanDownload;
    public bool CannotInstall => !CanInstall;

    /// <summary>Nouveautés reprises des notes de la release.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    public string SourceText => "github.com/ZedjeK/Aria-Cockpit";

    /// <summary>Ouvre la fenêtre sur la version proposée. Un téléchargement déjà lancé ou terminé reste où il en est.</summary>
    public void Open(AppUpdate update, Version installed)
    {
        if (!ReferenceEquals(update, _update))
        {
            _update = update;
            InstalledVersion = installed.ToString(3);
            LatestVersion = update.Latest.ToString(3);
            Notes.Clear();
            foreach (var note in update.Notes) Notes.Add(note);
            HasNotes = Notes.Count > 0;
            _asset = AppUpdatePackage.CurrentVariant() is { } variant ? AppUpdatePackage.Choose(update, variant) : null;
            CanDownload = _asset is not null;
            FileText = _asset is { } asset ? $"{asset.Name} · {Megabytes(asset.Size)}" : "";
            ExpectedHash = _asset?.Sha256 ?? "";
            KeepText = $@"Sauvegardes\{AppUpdateInstaller.VersionsFolderName}\{InstalledVersionText}\";
            Current = Step.Offer;
            _ = FindExistingAsync();
        }
        IsOpen = true;
    }

    /// <summary>Fichier déjà téléchargé et toujours conforme (lancement précédent) : inutile de le reprendre.</summary>
    private async Task FindExistingAsync()
    {
        if (_asset is not { } asset) return;
        var file = await Task.Run(() => AppUpdatePackage.AlreadyDownloaded(asset));
        if (file is null || Current != Step.Offer || !ReferenceEquals(asset, _asset)) return;
        ShowReady(file, asset.Sha256);
    }

    [RelayCommand]
    private async Task DownloadAsync()
    {
        if (_asset is not { } asset || _cancel is not null) return;
        using var cancel = _cancel = new CancellationTokenSource();
        Progress = 0;
        ProgressText = $"0 Mo sur {Megabytes(asset.Size)}";
        Current = Step.Downloading;
        BackgroundWork.Set(WorkLabel, true);
        DownloadResult result;
        try
        {
            // Progress<T> rappelle sur le fil de l'interface
            var progress = new Progress<long>(received =>
            {
                Progress = Math.Min(100, received * 100.0 / asset.Size);
                ProgressText = $"{Megabytes(received)} sur {Megabytes(asset.Size)}";
            });
            result = await Task.Run(() => AppUpdatePackage.DownloadAsync(asset, progress, cancel.Token));
        }
        finally
        {
            BackgroundWork.Set(WorkLabel, false);
            _cancel = null;
        }

        switch (result.Outcome)
        {
            case DownloadOutcome.Ready:
                ShowReady(result.File!, result.Actual!);
                break;
            case DownloadOutcome.Mismatch:
                HashKnown = result.Actual is not null;
                ActualHash = result.Actual ?? "";
                ErrorText = result.Error ?? "";
                Current = Step.Mismatch;
                break;
            case DownloadOutcome.Cancelled:
                Current = Step.Offer;
                return;
            default:
                ErrorText = result.Error ?? "Erreur inconnue.";
                Current = Step.Failed;
                break;
        }
        // La fenêtre avait pu être masquée pendant le téléchargement : elle revient dire le résultat
        IsOpen = true;
    }

    private void ShowReady(string file, string hash)
    {
        ReadyFile = file;
        ActualHash = hash;
        HashKnown = true;
        var blocked = AppUpdateInstaller.WhyNotInstallable(Environment.ProcessPath);
        CanInstall = blocked is null && RealVersion is not null;
        InstallBlockedText = blocked ?? (RealVersion is null ? "La version de cet exe est illisible." : "");
        Current = Step.Ready;
    }

    /// <summary>
    /// Met la version en cours de côté, remplace l'exe et demande la relance. Refusé tant qu'une opération tourne :
    /// la relance la couperait au milieu.
    /// </summary>
    [RelayCommand]
    private async Task InstallAsync()
    {
        if (_asset is not { } asset || _update is not { } update || RealVersion is not { } real) return;
        if (BusyText() is { } busy)
        {
            ErrorText = busy;
            Current = Step.InstallFailed;
            return;
        }
        Current = Step.Installing;
        var exe = Environment.ProcessPath;
        var result = await Task.Run(() => AppUpdateInstaller.Install(asset, update.Latest, real, exe));
        if (!result.Done)
        {
            ErrorText = result.Error ?? "Erreur inconnue.";
            Current = Step.InstallFailed;
            return;
        }
        RelaunchRequested?.Invoke(exe!);
    }

    private static string? BusyText() =>
        BackgroundWork.Running is { Count: > 0 } work
            ? $"Une opération est en cours : {string.Join(", ", work)}. Attends qu'elle finisse, puis recommence : la relance la couperait au milieu."
            : null;

    /// <summary>Arrête le téléchargement ; le fichier partiel est supprimé.</summary>
    [RelayCommand]
    private void Cancel() => _cancel?.Cancel();

    /// <summary>Masque la fenêtre. Un téléchargement en cours continue.</summary>
    [RelayCommand]
    private void Close() => IsOpen = false;

    /// <summary>Ouvre le dossier du cache dans l'Explorateur, le fichier sélectionné.</summary>
    [RelayCommand]
    private void ShowFile()
    {
        if (ReadyFile.Length > 0) Shell.ShowInFolder(ReadyFile);
    }

    [RelayCommand]
    private void OpenPage()
    {
        if (_update is not null) Shell.OpenUnelevated(_update.PageUrl);
    }

    // ---------------------------------------------------------------- Version gardée (page Paramètres)

    [ObservableProperty] private bool hasKept;
    [ObservableProperty] private string keptText = "aucune pour l'instant";
    [ObservableProperty] private string restoreLabel = "REVENIR À LA VERSION GARDÉE";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasRestoreNote))] private string restoreNote = "";

    public bool HasRestoreNote => RestoreNote.Length > 0;

    /// <summary>Relit la version gardée dans Sauvegardes\versions.</summary>
    public async Task RefreshKeptAsync()
    {
        var kept = await Task.Run(AppUpdateInstaller.KeptVersion);
        HasKept = kept is not null;
        KeptText = kept is null
            ? "aucune pour l'instant"
            : $@"{kept.ToString(3)} · Sauvegardes\{AppUpdateInstaller.VersionsFolderName}\{kept.ToString(3)}\";
        RestoreLabel = kept is null ? "REVENIR À LA VERSION GARDÉE" : $"REVENIR À LA {kept.ToString(3)}";
    }

    /// <summary>Remet la version gardée à la place de celle-ci, après confirmation, puis demande la relance.</summary>
    [RelayCommand]
    private async Task RestoreAsync()
    {
        RestoreNote = "";
        if (RealVersion is not { } real || await Task.Run(AppUpdateInstaller.KeptVersion) is not { } kept) return;
        if (AppUpdateInstaller.WhyNotInstallable(Environment.ProcessPath) is { } blocked)
        {
            RestoreNote = blocked;
            return;
        }
        if (BusyText() is { } busy)
        {
            RestoreNote = busy;
            return;
        }
        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: $"Revenir à la {kept.ToString(3)}",
                ConfirmText: $"Revenir à la {kept.ToString(3)}",
                Message: "Aria Cockpit remet la version gardée à la place de celle-ci, puis se relance.",
                Rows: [new DialogRow("Version d'Aria Cockpit", real.ToString(3), kept.ToString(3))],
                Warnings: ["La relance arrête la courbe de ventilation : le pilote NVIDIA reprend la main."],
                Note: $"La {real.ToString(3)} est gardée à son tour dans Sauvegardes\\{AppUpdateInstaller.VersionsFolderName}. Tes réglages et tes sauvegardes ne bougent pas.",
                IsRisky: true)))
            return;

        var exe = Environment.ProcessPath;
        var result = await Task.Run(() => AppUpdateInstaller.Restore(real, exe));
        if (!result.Done)
        {
            RestoreNote = result.Error ?? "Erreur inconnue.";
            return;
        }
        RelaunchRequested?.Invoke(exe!);
    }

    /// <summary>« 109 Mo », « 4,8 Mo » : mégaoctets décimaux, comme sur la page de la release.</summary>
    internal static string Megabytes(long bytes)
    {
        var mb = bytes / 1_000_000.0;
        return (mb >= 10 ? mb.ToString("0", French) : mb.ToString("0.0", French)) + " Mo";
    }
}
