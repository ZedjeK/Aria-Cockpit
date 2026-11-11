using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Aria.Core.Backup;
using Aria.Core.Nvidia;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Aria.Gui;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        // Lancé par la tâche planifiée à l'ouverture de session : réapplique le réglage GPU, sans fenêtre
        if (e.Args.Contains(StartupTask.Argument, StringComparer.OrdinalIgnoreCase))
        {
            GpuProfile.ApplyAtLogon();
            Shutdown();
            return;
        }

        // Lancé par la tâche planifiée à l'ouverture de session : réapplique la limite de charge (ASUS, Lenovo), sans fenêtre
        if (e.Args.Contains(Aria.Core.Battery.ChargeLimits.Argument, StringComparer.OrdinalIgnoreCase))
        {
            Aria.Core.Battery.ChargeLimits.ReapplyAtLogon();
            Shutdown();
            return;
        }

        // Lancé par la tâche « Démarrer avec Windows » : l'icône seule, la fenêtre reste rangée jusqu'à ce qu'on l'ouvre
        var inTray = e.Args.Contains(Aria.Core.Tray.TrayStartup.Argument, StringComparer.OrdinalIgnoreCase);

        // Déjà ouvert dans cette session : il revient au premier plan, sans seconde fenêtre. Une version plus récente
        // prend la place de l'ancienne (sinon une mise à jour rouvrirait l'ancienne, restée dans la zone de notification).
        if (!Services.SingleInstance.Claim() && (inTray || !TakeOverOrActivate()))
        {
            Shutdown();
            return;
        }

        // Thème « cockpit » : sombre imposé (les effets néon ne rendent rien sur fond clair)
        // et accent orange Blade Runner pour le menu, les boutons et les sélections.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.None, updateAccent: false);
        ApplicationAccentColorManager.Apply((Color)FindResource("NeonOrangeColor"), ApplicationTheme.Dark);

        // Fermer la fenêtre ne suffit plus à arrêter Aria : elle peut rester dans la zone de notification
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var state = new AppState();
        var window = new MainWindow(state);
        // À l'ouverture de session, le pilote graphique peut être encore en cours de chargement : l'icône attend qu'il
        // réponde avant de lire la carte (une lecture trop tôt la ferait passer pour absente jusqu'à la fermeture d'Aria)
        var tray = _tray = new Services.TrayIcon(window.BringToFront, window.OpenPage, () => Quit(window), waitForDriver: inTray);
        window.Tray = tray;
        // L'analyse du matériel part au premier affichage de la fenêtre (MainWindow) : rien à l'ouverture de session
        if (!inTray) window.Show();

        _ = Task.Run(() =>
        {
            try
            {
                if (inTray) WaitForDriver();
                // Ventilateurs laissés en manuel par un arrêt brutal d'Aria : rendus au pilote
                Services.FanCurveService.RecoverAfterCrash();
                // Avec « Démarrer avec Windows » : la courbe qui tournait à l'arrêt d'Aria repart
                var resumed = Services.FanCurveService.ResumeAtStartup();
                Dispatcher.InvokeAsync(() =>
                {
                    tray.DriverReady();
                    if (resumed is { } r) tray.Notify(r.Failed ? "Aria Cockpit : courbe non relancée" : "Aria Cockpit", r.Text);
                });
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
                Dispatcher.InvokeAsync(tray.DriverReady);
            }
        });
        // Ménage du dossier des données, une fois par jour au plus
        _ = Task.Run(() =>
        {
            try
            {
                if (DataRetention.RunDaily() is { } cleaned) ErrorLog.WriteLine(cleaned);
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
            }
        });
        // Tâches de démarrage : copie protégée de l'exe remise à jour, anciennes tâches repointées vers elle
        _ = Task.Run(() =>
        {
            try
            {
                if (Environment.ProcessPath is { } exe && Aria.Core.Settings.LogonExe.Refresh(exe) is { } moved)
                    ErrorLog.WriteLine(moved);
            }
            catch (Exception ex)
            {
                ErrorLog.Write(ex);
            }
        });
    }

    private Services.TrayIcon? _tray;

    /// <summary>
    /// Ouverture de session : attend que le pilote NVIDIA réponde, une minute au plus (comme la tâche du profil GPU).
    /// Sans carte NVIDIA, l'attente va au bout, en arrière-plan, sans rien bloquer.
    /// </summary>
    private static void WaitForDriver()
    {
        for (var attempt = 0; attempt < 12 && GpuTuner.TryOpen() is null; attempt++) Thread.Sleep(5000);
    }

    /// <summary>
    /// Aria tourne déjà dans la session. Vrai si ce lancement, plus récent, a pris sa place et doit continuer ;
    /// faux s'il doit s'arrêter (la fenêtre déjà ouverte est alors revenue au premier plan).
    /// </summary>
    private static bool TakeOverOrActivate()
    {
        var running = Services.SingleInstance.RunningVersion();
        var mine = Services.SingleInstance.OwnVersion;
        switch (Aria.Core.Tray.TrayStartup.OnSecondLaunch(running, mine))
        {
            case Aria.Core.Tray.SecondLaunch.TakeOver:
                if (Services.SingleInstance.TakeOver(Version.Parse(mine!), TimeSpan.FromSeconds(15))) return true;
                Services.SingleInstance.SignalRunning();
                System.Windows.MessageBox.Show(
                    $"Aria Cockpit {running} tourne déjà et n'a pas laissé la place à la version {mine} : une opération y est en cours, " +
                    "ou cette version-là ne sait pas le faire.\n\nQuitte-la (menu de son icône à côté de l'horloge, ou sa fenêtre), puis relance celle-ci.",
                    "Aria Cockpit", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return false;
            case Aria.Core.Tray.SecondLaunch.Refuse:
                Services.SingleInstance.SignalRunning();
                System.Windows.MessageBox.Show(
                    $"Aria Cockpit {running} tourne déjà, et la version que tu viens de lancer ({mine}) est plus ancienne.\n\n" +
                    "Pour revenir à l'ancienne, quitte d'abord celle qui tourne (menu de son icône à côté de l'horloge).",
                    "Aria Cockpit", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
                return false;
            default:
                Services.SingleInstance.SignalRunning();
                return false;
        }
    }

    /// <summary>
    /// « Quitter » du menu de l'icône : arrête Aria pour de bon. Une opération en cours serait coupée au milieu :
    /// on le dit avant.
    /// </summary>
    private static void Quit(MainWindow window)
    {
        var work = Services.BackgroundWork.Running;
        if (work.Count > 0 && System.Windows.MessageBox.Show(
                $"Une opération est en cours : {string.Join(", ", work)}.\n\nQuitter maintenant l'arrête au milieu. Quitter quand même ?",
                "Aria Cockpit", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes)
            return;
        window.Quit();
    }

    private bool _showingError;
    private string? _lastErrorKey;
    private DateTime _lastErrorAt;
    private int _repeats;

    /// <summary>
    /// Toute erreur imprévue est journalisée et affichée, au lieu de fermer l'application.
    /// Une seule fenêtre d'erreur à la fois : une erreur d'affichage peut se répéter à chaque rafraîchissement
    /// de l'écran (cas du 08/10/2026), elle n'est alors notée qu'une fois, avec son nombre de répétitions.
    /// </summary>
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        var key = $"{e.Exception.GetType().FullName}|{e.Exception.Message}";
        var now = DateTime.Now;
        if (key == _lastErrorKey && now - _lastErrorAt < TimeSpan.FromSeconds(30))
        {
            _lastErrorAt = now;
            _repeats++;
            if (_repeats is 10 or 100 or 1000)
                ErrorLog.WriteLine($"Même erreur répétée {_repeats} fois.");
            return;
        }

        FlushRepeats();
        _lastErrorKey = key;
        _lastErrorAt = now;
        var log = ErrorLog.Write(e.Exception);

        if (_showingError) return;
        _showingError = true;
        try
        {
            System.Windows.MessageBox.Show(
                $"Une erreur inattendue s'est produite :\n\n{e.Exception.Message}\n\nDétails enregistrés dans :\n{log}\n\n" +
                "Si l'affichage reste anormal, fermez puis relancez Aria.",
                "Aria Cockpit", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
        }
    }

    private void FlushRepeats()
    {
        if (_repeats > 0 && _repeats is not (10 or 100 or 1000))
            ErrorLog.WriteLine($"Même erreur répétée {_repeats} fois au total.");
        _repeats = 0;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Courbe de ventilation en cours : le pilote reprend la main avant qu'Aria ne se ferme
        try
        {
            Services.FanCurveService.StopForExit();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
        // Icône retirée de la zone de notification (sinon elle y reste jusqu'au passage de la souris)
        _tray?.Dispose();
        FlushRepeats();
        base.OnExit(e);
    }
}

/// <summary>Journal des erreurs, dans %USERPROFILE%\.Aria-Cockpit\erreurs.log.</summary>
internal static class ErrorLog
{
    private static string FilePath => Path.Combine(BackupStore.DataRoot, "erreurs.log");

    public static string Write(Exception exception) => WriteLine($"{exception}\n");

    public static string WriteLine(string text)
    {
        try
        {
            Directory.CreateDirectory(BackupStore.DataRoot);
            File.AppendAllText(FilePath, $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss}]\n{text}\n");
        }
        catch (Exception)
        {
            // Le journal est un bonus : on n'échoue jamais à cause de lui.
        }
        return FilePath;
    }
}
