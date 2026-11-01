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

        // Thème « cockpit » : sombre imposé (les effets néon ne rendent rien sur fond clair)
        // et accent orange Blade Runner pour le menu, les boutons et les sélections.
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.None, updateAccent: false);
        ApplicationAccentColorManager.Apply((Color)FindResource("NeonOrangeColor"), ApplicationTheme.Dark);

        var state = new AppState();
        new MainWindow(state).Show();
        _ = state.AnalyzeAsync();
        // Ventilateurs laissés en manuel par un arrêt brutal d'Aria : rendus au pilote
        _ = Task.Run(Services.FanCurveService.RecoverAfterCrash);
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
            Services.FanCurveService.Stop();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(ex);
        }
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
