using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Updates;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

/// <summary>
/// Fenêtre « Mise à jour d'Aria Cockpit » : propose la nouvelle version, la télécharge sur demande dans le cache du
/// dossier des données et contrôle son empreinte. Rien n'est téléchargé avant le clic, et rien n'est installé ici :
/// une fois le fichier contrôlé, Aria ouvre le dossier où il est rangé.
/// </summary>
public sealed partial class AppUpdateViewModel : ObservableObject
{
    private const string WorkLabel = "téléchargement de la mise à jour";
    private static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

    private AppUpdate? _update;
    private AppUpdateAsset? _asset;
    private CancellationTokenSource? _cancel;

    public enum Step
    {
        Offer,
        Downloading,
        Ready,
        Mismatch,
        Failed
    }

    [ObservableProperty] private bool isOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffer), nameof(IsDownloading), nameof(IsReady), nameof(IsMismatch), nameof(IsFailed))]
    private Step current = Step.Offer;

    public bool IsOffer => Current == Step.Offer;
    public bool IsDownloading => Current == Step.Downloading;
    public bool IsReady => Current == Step.Ready;
    public bool IsMismatch => Current == Step.Mismatch;
    public bool IsFailed => Current == Step.Failed;

    [ObservableProperty] private string installedVersion = "";
    [ObservableProperty] private string latestVersion = "";
    [ObservableProperty] private bool hasNotes;
    [ObservableProperty] private string fileText = "";
    [ObservableProperty] private bool canDownload;
    [ObservableProperty] private double progress;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string expectedHash = "";
    [ObservableProperty] private string actualHash = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasActualHash))] private bool hashKnown;
    [ObservableProperty] private string errorText = "";
    [ObservableProperty] private string readyFile = "";

    public bool HasActualHash => HashKnown;
    public bool CannotDownload => !CanDownload;

    /// <summary>Nouveautés reprises des notes de la release.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    public string SourceText => "github.com/ZedjeK/Aria-Cockpit";

    partial void OnCanDownloadChanged(bool value) => OnPropertyChanged(nameof(CannotDownload));

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
        Current = Step.Ready;
    }

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

    /// <summary>« 109 Mo », « 4,8 Mo » : mégaoctets décimaux, comme sur la page de la release.</summary>
    internal static string Megabytes(long bytes)
    {
        var mb = bytes / 1_000_000.0;
        return (mb >= 10 ? mb.ToString("0", French) : mb.ToString("0.0", French)) + " Mo";
    }
}
