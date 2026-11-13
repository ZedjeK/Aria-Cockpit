using System.IO;
using System.Windows;
using System.Windows.Input;
using Aria.Core.Install;
using Aria.Core.Updates;

namespace Aria.Gui.Views;

/// <summary>Ce que l'utilisateur a choisi dans la fenêtre d'installation.</summary>
public enum InstallPromptResult
{
    /// <summary>Fenêtre fermée sans choix : Aria ne se lance pas.</summary>
    Closed,
    /// <summary>Les fichiers sont en place (<see cref="InstallPromptWindow.Outcome"/>) : à relancer depuis le dossier du programme.</summary>
    Installed,
    /// <summary>« Lancer sans installer » : Aria se lance depuis ici, comme un exe portable.</summary>
    RunHere
}

/// <summary>
/// Proposition d'installation, au lancement d'un exe qui n'est pas dans son dossier. L'installation se fait ici, avec
/// un message d'attente : la copie des deux exes prend une ou deux secondes. En cas d'échec, l'erreur s'affiche et
/// « Lancer sans installer » reste possible.
/// </summary>
public partial class InstallPromptWindow : Window
{
    private readonly string _exe;
    private bool _busy;

    public InstallPromptResult Result { get; private set; } = InstallPromptResult.Closed;
    public SelfInstallResult? Outcome { get; private set; }

    /// <summary>« Ne plus me proposer l'installation » était coché en choisissant « Lancer sans installer ».</summary>
    public bool DontAskAgain { get; private set; }

    public InstallPromptWindow(string exe, string? version)
    {
        _exe = exe;
        InitializeComponent();
        TitleText.Text = "ARIA COCKPIT" + (version is null ? "" : " " + version);
        DestinationText.Text = SelfInstall.Folder;

        if (SelfInstall.InstalledVersion() is { } installed)
        {
            InstalledText.Text = $"La version {installed.ToString(3)} est déjà installée ici : elle sera remplacée, et gardée de côté.";
            InstalledText.Visibility = Visibility.Visible;
        }
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(exe))!, AppUpdateInstaller.BenchName)))
            BenchText.Visibility = Visibility.Visible;

        // Case décochée d'office : un raccourci ne se crée qu'à la demande. Un raccourci déjà là est mis à jour, jamais caché.
        var existing = DesktopShortcut.TargetOf(DesktopShortcut.DefaultPath);
        if (existing is not null)
        {
            ShortcutBox.Content = "Mettre à jour le raccourci du Bureau";
            ShortcutHelp.Text = $"Un raccourci « {DesktopShortcut.FileName} » existe déjà et pointe vers {existing}. Coché, il pointera vers l'exe installé.";
        }
        else
        {
            ShortcutHelp.Text = "Sur ton Bureau, vers l'exe installé. Il ne change pas d'une version à l'autre.";
        }
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Bouton relâché avant le début du déplacement
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        SetBusy(true, "Copie en cours…");
        var shortcut = ShortcutBox.IsChecked == true;
        var outcome = await Task.Run(() => SelfInstall.Install(_exe, shortcut));
        if (!outcome.Done)
        {
            SetBusy(false, outcome.Error ?? "L'installation a échoué.", error: true);
            return;
        }
        Outcome = outcome;
        Result = InstallPromptResult.Installed;
        DialogResult = true;
    }

    private void OnRunHere(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        DontAskAgain = DontAskBox.IsChecked == true;
        Result = InstallPromptResult.RunHere;
        DialogResult = false;
    }

    private void SetBusy(bool busy, string text, bool error = false)
    {
        _busy = busy;
        InstallButton.IsEnabled = !busy;
        RunButton.IsEnabled = !busy;
        ShortcutBox.IsEnabled = !busy;
        DontAskBox.IsEnabled = !busy;
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "NeonRedBrush" : "NeonTealBrush");
        StatusText.Visibility = Visibility.Visible;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // Pas de fermeture au milieu de la copie : les fichiers resteraient à moitié écrits
        if (_busy) e.Cancel = true;
        base.OnClosing(e);
    }
}
