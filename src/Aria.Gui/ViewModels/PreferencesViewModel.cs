using System.IO;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Backup;
using Aria.Core.Install;
using Aria.Core.Nvidia;
using Aria.Core.Settings;
using Aria.Core.Tray;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

/// <summary>
/// Page Paramètres (roue crantée) : ce qui règle l'application elle-même, à part des réglages du PC (page
/// Optimisation). Démarrage avec Windows, comportement de la croix, dossier des données et durées de conservation.
/// Le démarrage et la croix prennent effet tout de suite ; les durées, sur « Enregistrer ».
/// </summary>
public sealed partial class PreferencesViewModel : ObservableObject
{
    private AppPreferences _saved = AppPreferences.Default;
    private bool _loading;

    // ---- Démarrage ----
    [ObservableProperty] private bool startWithWindows;
    [ObservableProperty] private bool startupKnown;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasStartupNote))] private string startupNote = "";
    [ObservableProperty] private bool startupNoteIsError;
    [ObservableProperty] private string gpuStartupText = "";
    [ObservableProperty] private string fanResumeText = "";
    [ObservableProperty] private bool isBusy;

    public bool HasStartupNote => StartupNote.Length > 0;
    public string StartupTaskName => TrayStartup.TaskName;
    public string ProtectedFolder => LogonExe.Folder;

    // ---- Fenêtre et icône ----
    [ObservableProperty] private bool closeAuto = true;
    [ObservableProperty] private bool closeTray;
    [ObservableProperty] private bool closeQuit;
    [ObservableProperty] private bool notifyOnHide = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasWindowNote))] private string windowNote = "";
    public bool HasWindowNote => WindowNote.Length > 0;

    // ---- Fond animé ----
    [ObservableProperty] private bool backgroundActive = true;
    [ObservableProperty] private int backgroundSpeedIndex = 1;
    [ObservableProperty] private int backgroundMinimum = BackgroundSettings.Default.Minimum;
    [ObservableProperty] private int backgroundMaximum = BackgroundSettings.Default.Maximum;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasBackgroundNote))] private string backgroundNote = "";
    [ObservableProperty] private bool backgroundNoteIsError;
    public bool HasBackgroundNote => BackgroundNote.Length > 0;
    public IReadOnlyList<int> BackgroundCounts { get; } =
        Enumerable.Range(BackgroundSettings.MinPoints, BackgroundSettings.MaxPoints - BackgroundSettings.MinPoints + 1).ToArray();

    // ---- Mises à jour d'Aria Cockpit ----
    [ObservableProperty] private bool checkUpdates = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasUpdatesNote))] private string updatesNote = "";
    [ObservableProperty] private bool updatesNoteIsError;
    public bool HasUpdatesNote => UpdatesNote.Length > 0;

    /// <summary>Recherche, version trouvée et version gardée : le même exemplaire que le bandeau de la fenêtre.</summary>
    public AppUpdateViewModel Updates => AppUpdateViewModel.Shared;

    partial void OnCheckUpdatesChanged(bool value)
    {
        if (_loading) return;
        if (!TrySave(_saved with { CheckUpdates = value }, out var error))
        {
            UpdatesNote = $"Réglage non enregistré : {error}";
            UpdatesNoteIsError = true;
            return;
        }
        UpdatesNoteIsError = false;
        UpdatesNote = value
            ? "Aria cherchera une nouvelle version à son prochain lancement."
            : "Aria ne contactera plus GitHub au lancement. La recherche se fait avec le bouton ci-dessous.";
    }

    // ---- Installation ----
    [ObservableProperty] private bool offerInstall = true;
    [ObservableProperty] private string installStateText = "";
    [ObservableProperty] private bool isInstalled;
    [ObservableProperty] private string shortcutText = "";
    [ObservableProperty] private bool canCreateShortcut;
    [ObservableProperty] private string createShortcutLabel = "CRÉER LE RACCOURCI";
    [ObservableProperty] private bool canRemoveShortcut;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasInstallNote))] private string installNote = "";
    [ObservableProperty] private bool installNoteIsError;
    public bool HasInstallNote => InstallNote.Length > 0;
    public string ProgramFolder => SelfInstall.Folder;

    // ---- Données ----
    public string DataPath => BackupStore.DataRoot;
    [ObservableProperty] private string dataSizeText = "calcul…";
    [ObservableProperty] private string reportsMonths = "6";
    [ObservableProperty] private string backupsMonths = "12";
    [ObservableProperty] private string keepBackups = "10";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasDataNote))] private string dataNote = "";
    [ObservableProperty] private bool dataNoteIsError;
    public bool HasDataNote => DataNote.Length > 0;

    public string RangeText =>
        $"De {AppPreferences.MinMonths} à {AppPreferences.MaxMonths} mois ; de {AppPreferences.MinKeep} à {AppPreferences.MaxKeep} sauvegardes toujours gardées.";

    // ---- À propos ----
    public string VersionText { get; } =
        "Aria Cockpit " + (Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?");

    public PreferencesViewModel() =>
        // Bascule faite depuis le menu de l'icône pendant que la page est ouverte : la case suit
        AutostartService.Changed += () => Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (AutostartService.Enabled is not { } enabled || IsBusy) return;
            _loading = true;
            StartWithWindows = enabled;
            _loading = false;
        });

    /// <summary>Page affichée : paramètres enregistrés, état des tâches de démarrage, taille du dossier.</summary>
    public async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _saved = await Task.Run(AppPreferencesStore.Load);
            CloseAuto = _saved.Close == CloseBehavior.Auto;
            CloseTray = _saved.Close == CloseBehavior.AlwaysTray;
            CloseQuit = _saved.Close == CloseBehavior.AlwaysQuit;
            NotifyOnHide = _saved.NotifyOnHide;
            CheckUpdates = _saved.CheckUpdates;
            OfferInstall = _saved.OfferInstall;
            ShowBackground(_saved.Background);
            ShowRetention(_saved);

            StartWithWindows = AutostartService.Enabled ?? await AutostartService.RefreshAsync();
            StartupKnown = true;
        }
        finally
        {
            _loading = false;
        }

        var (gpuTask, curve) = await Task.Run(() => (StartupTask.Exists(), FanCurveStore.Load()));
        GpuStartupText = gpuTask
            ? "Ton profil GPU est réappliqué à chaque ouverture de session, par sa propre tâche (« Garder au démarrage », page Réglage GPU)."
            : "Aucun profil GPU n'est réappliqué à l'ouverture de session. Pour en garder un : page Réglage GPU, « Garder au démarrage ».";
        FanResumeText = curve is { Resume: true }
            ? $"Courbe à relancer au prochain lancement : « {curve.Name} »."
            : "Aucune courbe à relancer : le pilote NVIDIA a la main, ou aucune courbe n'a tourné depuis cette version.";
        await Updates.RefreshKeptAsync();
        await RefreshInstallAsync();
        await MeasureAsync();
    }

    private void ShowRetention(AppPreferences preferences)
    {
        ReportsMonths = preferences.ReportsMonths.ToString();
        BackupsMonths = preferences.BackupsMonths.ToString();
        KeepBackups = preferences.KeepBackups.ToString();
    }

    private async Task MeasureAsync()
    {
        var info = await Task.Run(DataRetention.Measure);
        DataSizeText = $"{Size(info.Bytes)} · {info.Reports} rapport{(info.Reports > 1 ? "s" : "")} · {info.Backups} sauvegarde{(info.Backups > 1 ? "s" : "")}";
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024 * 1024):0.0} Go",
        >= 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} Mo",
        _ => $"{Math.Max(1, bytes / 1024)} Ko"
    };

    // ---------------------------------------------------------------- Démarrage

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (!_loading) _ = SetStartupAsync(value);
    }

    private async Task SetStartupAsync(bool enabled)
    {
        IsBusy = true;
        try
        {
            await Task.Run(() => AutostartService.Set(enabled));
            ShowStartup(enabled
                ? "Aria Cockpit démarrera avec Windows, rangée dans son icône."
                : "Aria Cockpit ne démarre plus avec Windows. La tâche planifiée est supprimée.", error: false);
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowStartup(e.Message, error: true);
            // La tâche n'a pas changé : la case revient à l'état réel
            _loading = true;
            StartWithWindows = !enabled;
            _loading = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ShowStartup(string text, bool error)
    {
        StartupNote = text;
        StartupNoteIsError = error;
    }

    // ---------------------------------------------------------------- Fenêtre et icône

    partial void OnCloseAutoChanged(bool value) => SaveWindow(value);
    partial void OnCloseTrayChanged(bool value) => SaveWindow(value);
    partial void OnCloseQuitChanged(bool value) => SaveWindow(value);
    partial void OnNotifyOnHideChanged(bool value) => SaveWindow(changed: true);

    /// <summary>Un choix vient d'être coché (un bouton radio décoché ne déclenche rien) : enregistré tout de suite.</summary>
    private void SaveWindow(bool changed)
    {
        if (_loading || !changed) return;
        var close = CloseQuit ? CloseBehavior.AlwaysQuit : CloseTray ? CloseBehavior.AlwaysTray : CloseBehavior.Auto;
        if (!TrySave(_saved with { Close = close, NotifyOnHide = NotifyOnHide }, out var error))
        {
            WindowNote = $"Choix non enregistré : {error}";
            return;
        }
        WindowNote = close switch
        {
            CloseBehavior.AlwaysQuit => "Enregistré. La croix arrête Aria : une courbe de ventilation en cours rend alors la main au pilote NVIDIA.",
            CloseBehavior.AlwaysTray => "Enregistré. La croix range toujours Aria dans son icône ; « Quitter » est dans le menu de l'icône.",
            _ => "Enregistré."
        };
    }

    // ---------------------------------------------------------------- Fond animé

    partial void OnBackgroundActiveChanged(bool value) => SaveBackground();
    partial void OnBackgroundSpeedIndexChanged(int value) => SaveBackground();
    partial void OnBackgroundMinimumChanged(int value) => SaveBackground();
    partial void OnBackgroundMaximumChanged(int value) => SaveBackground();

    private void ShowBackground(BackgroundSettings background)
    {
        BackgroundActive = background.Active;
        BackgroundSpeedIndex = (int)background.Speed;
        BackgroundMinimum = background.Minimum;
        BackgroundMaximum = background.Maximum;
    }

    /// <summary>Un réglage du fond vient de changer : enregistré et appliqué tout de suite, sans bouton.</summary>
    private void SaveBackground()
    {
        if (_loading) return;
        var speed = Enum.IsDefined((BackgroundSpeed)BackgroundSpeedIndex) ? (BackgroundSpeed)BackgroundSpeedIndex : BackgroundSpeed.Normal;
        var wanted = new BackgroundSettings(BackgroundActive, speed, BackgroundMinimum, BackgroundMaximum).Sanitized();
        if (!TrySave(_saved with { Background = wanted }, out var error))
        {
            BackgroundNote = $"Réglage non enregistré : {error}";
            BackgroundNoteIsError = true;
            return;
        }
        BackdropService.Set(wanted);
        BackgroundNoteIsError = false;
        BackgroundNote = wanted.Active
            ? $"Enregistré. De {wanted.Minimum} à {wanted.Maximum} points par passage."
            : "Enregistré. Le fond animé est coupé.";
        // Bornes saisies à l'envers : les listes reprennent l'ordre enregistré
        if (wanted.Minimum != BackgroundMinimum || wanted.Maximum != BackgroundMaximum)
        {
            _loading = true;
            ShowBackground(wanted);
            _loading = false;
        }
    }

    private bool TrySave(AppPreferences preferences, out string error)
    {
        try
        {
            AppPreferencesStore.Save(preferences);
            _saved = preferences.Sanitized();
            error = "";
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
    }

    // ---------------------------------------------------------------- Installation

    /// <summary>Où tourne cette Aria, ce qui est installé, et l'état du raccourci du Bureau.</summary>
    private async Task RefreshInstallAsync()
    {
        var self = Environment.ProcessPath;
        var (place, installed, target) = await Task.Run(() => (SelfInstall.PlaceOf(self), SelfInstall.InstalledVersion(),
            DesktopShortcut.TargetOf(DesktopShortcut.DefaultPath)));

        IsInstalled = installed is not null;
        var where = installed is null ? "Rien n'est installé dans le dossier du programme." : $"La version {installed.ToString(3)} est installée dans le dossier du programme.";
        InstallStateText = place switch
        {
            InstallPlace.Home => $"Cette Aria tourne depuis son dossier d'installation. {where}",
            InstallPlace.ProtectedCopy => $"Cette Aria a été lancée par le démarrage automatique, depuis sa copie de Program Files. {where}",
            _ => $"Cette Aria tourne depuis « {self} ». {where}" + (installed is null ? " Au prochain lancement de cet exe, Aria proposera de s'installer." : "")
        };

        var ours = target is not null && string.Equals(Path.GetFullPath(target), Path.GetFullPath(SelfInstall.HomeExe), StringComparison.OrdinalIgnoreCase);
        CanRemoveShortcut = ours;
        CanCreateShortcut = installed is not null && !ours;
        CreateShortcutLabel = target is null ? "CRÉER LE RACCOURCI" : "METTRE À JOUR LE RACCOURCI";
        ShortcutText = target is null ? "Aucun raccourci « Aria Cockpit » sur ton Bureau."
            : ours ? "Le raccourci du Bureau pointe vers l'exe installé."
            : $"Un raccourci « {DesktopShortcut.FileName} » existe sur ton Bureau, mais il pointe vers {target}, pas vers l'exe installé.";
        if (installed is null && target is null) ShortcutText += " Il se crée depuis la fenêtre d'installation, ou ici une fois Aria installée.";
    }

    partial void OnOfferInstallChanged(bool value)
    {
        if (_loading) return;
        if (!TrySave(_saved with { OfferInstall = value }, out var error))
        {
            ShowInstall($"Réglage non enregistré : {error}", error: true);
            return;
        }
        ShowInstall(value
            ? "Aria proposera de s'installer quand l'exe sera lancé hors de son dossier."
            : "Aria ne proposera plus de s'installer : l'exe se lance tel quel, partout où il est.", error: false);
    }

    [RelayCommand]
    private async Task CreateShortcut()
    {
        try
        {
            await Task.Run(() => DesktopShortcut.Create(DesktopShortcut.DefaultPath, SelfInstall.HomeExe));
            ShowInstall("Raccourci créé sur ton Bureau, vers l'exe installé.", error: false);
        }
        catch (IOException e)
        {
            ShowInstall(e.Message, error: true);
        }
        await RefreshInstallAsync();
    }

    [RelayCommand]
    private async Task RemoveShortcut()
    {
        if (!await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Retirer le raccourci",
                ConfirmText: "Retirer",
                Message: $"Le raccourci « {DesktopShortcut.FileName} » de ton Bureau est supprimé. Aria elle-même n'est pas touchée.",
                Icon: "Delete24")))
            return;
        try
        {
            var removed = await Task.Run(() => DesktopShortcut.Remove(DesktopShortcut.DefaultPath, SelfInstall.HomeExe));
            ShowInstall(removed ? "Raccourci retiré." : "Ce raccourci ne pointe plus vers l'exe installé : il n'a pas été touché.", error: !removed);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowInstall($"Raccourci non retiré : {e.Message}", error: true);
        }
        await RefreshInstallAsync();
    }

    [RelayCommand]
    private void OpenProgramFolder()
    {
        try
        {
            if (Directory.Exists(SelfInstall.Folder)) Shell.OpenUnelevated(SelfInstall.Folder);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowInstall($"Dossier non ouvert : {e.Message}", error: true);
        }
    }

    private void ShowInstall(string text, bool error)
    {
        InstallNote = text;
        InstallNoteIsError = error;
    }

    // ---------------------------------------------------------------- Données

    [RelayCommand]
    private void OpenDataFolder()
    {
        try
        {
            Shell.OpenUnelevated(DataPath);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            ShowData($"Dossier non ouvert : {e.Message}", error: true);
        }
    }

    /// <summary>Durées saisies, ou null avec la raison si l'une d'elles n'est pas un nombre admis.</summary>
    private RetentionSettings? ReadRetention(out string error)
    {
        error = "";
        if (!int.TryParse(ReportsMonths.Trim(), out var reports) || reports is < AppPreferences.MinMonths or > AppPreferences.MaxMonths)
            error = $"Rapports : un nombre de mois entre {AppPreferences.MinMonths} et {AppPreferences.MaxMonths}.";
        else if (!int.TryParse(BackupsMonths.Trim(), out var backups) || backups is < AppPreferences.MinMonths or > AppPreferences.MaxMonths)
            error = $"Sauvegardes : un nombre de mois entre {AppPreferences.MinMonths} et {AppPreferences.MaxMonths}.";
        else if (!int.TryParse(KeepBackups.Trim(), out var keep) || keep is < AppPreferences.MinKeep or > AppPreferences.MaxKeep)
            error = $"Sauvegardes toujours gardées : un nombre entre {AppPreferences.MinKeep} et {AppPreferences.MaxKeep}.";
        else return new RetentionSettings(reports, backups, keep);
        return null;
    }

    /// <summary>
    /// Enregistre les durées. Rien n'est supprimé ici : le ménage quotidien les appliquera. Si elles feraient
    /// disparaître des fichiers, on dit combien avant d'enregistrer.
    /// </summary>
    [RelayCommand]
    private async Task SaveRetention()
    {
        if (ReadRetention(out var error) is not { } wanted)
        {
            ShowData(error, error: true);
            return;
        }
        var doomed = await Task.Run(() => DataRetention.Preview(wanted));
        if (!doomed.Nothing && !await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Durées de conservation",
                ConfirmText: "Enregistrer",
                Message: $"Avec ces durées, le prochain ménage supprimera {Doomed(doomed)}.",
                Warnings: ["Suppression définitive : une sauvegarde supprimée ne peut plus être restaurée."],
                Note: "Rien n'est supprimé tout de suite : le ménage a lieu au plus une fois par jour, au lancement d'Aria, ou avec « Faire le ménage maintenant ».",
                IsRisky: true)))
            return;

        var preferences = _saved with { ReportsMonths = wanted.ReportsMonths, BackupsMonths = wanted.BackupsMonths, KeepBackups = wanted.KeepBackups };
        if (!TrySave(preferences, out var failure))
        {
            ShowData($"Durées non enregistrées : {failure}", error: true);
            return;
        }
        ShowRetention(_saved);
        ShowData(doomed.Nothing
            ? "Durées enregistrées. Aucun fichier n'est concerné pour l'instant."
            : $"Durées enregistrées. Le prochain ménage supprimera {Doomed(doomed)}.", error: false);
    }

    /// <summary>Ménage immédiat, avec les durées enregistrées (pas celles en cours de saisie).</summary>
    [RelayCommand]
    private async Task CleanNow()
    {
        var settings = _saved.Retention;
        var doomed = await Task.Run(() => DataRetention.Preview(settings));
        if (!doomed.Nothing && !await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Faire le ménage maintenant",
                ConfirmText: "Supprimer",
                Message: $"Le ménage supprimera {Doomed(doomed)}, d'après les durées enregistrées " +
                         $"(rapports {settings.ReportsMonths} mois, sauvegardes {settings.BackupsMonths} mois, {settings.KeepBackups} sauvegardes toujours gardées).",
                Warnings: ["Suppression définitive : une sauvegarde supprimée ne peut plus être restaurée."],
                IsRisky: true,
                Icon: "Delete24")))
            return;
        try
        {
            var done = await Task.Run(() => DataRetention.RunNow(settings));
            ShowData(done ?? "Ménage fait : rien à supprimer.", error: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowData($"Ménage interrompu : {e.Message}", error: true);
        }
        await MeasureAsync();
    }

    private static string Doomed(RetentionPreview p)
    {
        var parts = new List<string>();
        if (p.Reports > 0) parts.Add($"{p.Reports} rapport{(p.Reports > 1 ? "s" : "")}");
        if (p.Backups > 0) parts.Add($"{p.Backups} sauvegarde{(p.Backups > 1 ? "s" : "")}");
        return string.Join(" et ", parts);
    }

    private void ShowData(string text, bool error)
    {
        DataNote = text;
        DataNoteIsError = error;
    }
}
