using System.Reflection;
using System.Windows;
using System.Windows.Input;
using Aria.Core.Updates;
using Aria.Gui.Services;
using Aria.Gui.Views;
using Wpf.Ui.Controls;

namespace Aria.Gui;

public partial class MainWindow : FluentWindow
{
    private readonly PageProvider _pages;
    private AppUpdate? _update;

    public MainWindow(AppState state)
    {
        DataContext = state;
        _pages = new PageProvider(state);
        InitializeComponent();
        // Pas d'historique « Précédent » : la barre du haut est la seule navigation
        PageHost.Navigated += (_, _) =>
        {
            while (PageHost.CanGoBack) PageHost.RemoveBackEntry();
            PageScroller.ScrollToTop();
        };
        VersionText.Text = "v" + CurrentVersionText;
        SingleInstance.Listen(this, BringToFront, OnHandOff);
        Loaded += (_, _) =>
        {
            ShowCheckedPage();
            _ = CheckForUpdateAsync();
            // Premier affichage de la fenêtre : au lancement, ou plus tard si Aria a démarré rangée avec Windows
            _ = state.AnalyzeAsync();
        };
        // Fenêtre rangée dans la zone de notification : la page est retirée, comme quand on change d'onglet, pour que
        // sa télémétrie et ses animations s'arrêtent. Elle revient telle quelle à la réouverture.
        IsVisibleChanged += (_, e) =>
        {
            if (!IsLoaded) return;
            if ((bool)e.NewValue) ShowCheckedPage();
            else PageHost.Content = null;
        };
    }

    /// <summary>Affiche la page de l'onglet coché : Mon PC, ou celle demandée depuis l'icône avant le premier affichage.</summary>
    private void ShowCheckedPage() =>
        ShowPage(Tabs.Children.OfType<System.Windows.Controls.RadioButton>().FirstOrDefault(t => t.IsChecked == true)?.Tag as Type
                 ?? typeof(DashboardPage));

    /// <summary>Icône de la zone de notification : son état décide si fermer la fenêtre range Aria ou l'arrête.</summary>
    public TrayIcon? Tray { get; set; }

    private bool _quitting;

    /// <summary>
    /// Une version plus récente d'Aria Cockpit vient d'être lancée et demande la place : celle-ci s'arrête (la
    /// nouvelle relance la courbe de ventilation si « Démarrer avec Windows » est activé). Elle ne cède pas au milieu
    /// d'une opération, ni à une version qui n'est pas plus récente.
    /// </summary>
    private void OnHandOff(Version launched)
    {
        if (!Version.TryParse(SingleInstance.OwnVersion, out var mine) || launched <= mine) return;
        if (BackgroundWork.Running.Count > 0) return;
        Quit();
    }

    /// <summary>Arrête Aria pour de bon (« Quitter » du menu de l'icône), quoi qu'elle ait d'actif.</summary>
    public void Quit()
    {
        _quitting = true;
        Close();
    }

    /// <summary>
    /// Fermer la fenêtre range Aria dans la zone de notification tant qu'elle a quelque chose d'actif sur la machine
    /// (profil GPU en place, courbe de ventilation, opération en cours) ; sinon elle s'arrête, comme avant.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _quitting || Tray?.Current().KeepReason() is not { } reason) return;
        e.Cancel = true;
        Hide();
        Tray.Notify("Aria Cockpit continue en arrière-plan",
            $"{reason} Pour la rouvrir ou la quitter : son icône, à côté de l'horloge.");
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    /// <summary>Depuis le menu de l'icône : rouvre la fenêtre sur une page, onglet coché.</summary>
    public void OpenPage(Type page)
    {
        BringToFront();
        if (Tabs.Children.OfType<System.Windows.Controls.RadioButton>().FirstOrDefault(t => t.Tag as Type == page) is not { } tab) return;
        // Déjà coché : la page est affichée, ou le sera au premier affichage de la fenêtre
        if (tab.IsChecked != true) tab.IsChecked = true;
    }

    private static string CurrentVersionText =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    /// <summary>Au lancement : une version plus récente sur GitHub ? Si oui, bandeau discret. Rien n'est téléchargé.</summary>
    private async Task CheckForUpdateAsync()
    {
        if (!AppUpdateChecker.TryParseVersion(CurrentVersionText, out var current)) return;
        _update = await AppUpdateChecker.CheckAsync(current);
        if (_update is null) return;
        UpdateText.Text = $"Aria Cockpit {_update.Latest} est disponible (version installée : {current}). Rien n'est installé automatiquement.";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Aria Cockpit relancé alors qu'il est déjà ouvert, ou clic sur son icône : la fenêtre existante revient au
    /// premier plan, même rangée dans la zone de notification.
    /// </summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        // Passage au premier plan garanti même si Windows refuse Activate seul (fenêtre d'un autre processus devant)
        Topmost = true;
        Topmost = false;
    }

    private void OnOpenUpdate(object sender, RoutedEventArgs e)
    {
        if (_update is not null) Shell.OpenUnelevated(_update.PageUrl);
    }

    private void OnCloseUpdate(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    /// <summary>Un onglet de la barre du haut vient d'être choisi : affiche sa page.</summary>
    private void OnTabChecked(object sender, RoutedEventArgs e)
    {
        // Pendant le chargement, la page d'accueil est ouverte par Loaded
        if (!IsLoaded || e.OriginalSource is not System.Windows.Controls.RadioButton { Tag: Type page }) return;
        ShowPage(page);
    }

    /// <summary>
    /// Affiche une page. Une page qui gère elle-même son défilement (ScrollViewer.CanContentScroll posé
    /// explicitement, comme Optimisation avec sa barre d'action fixée en bas) reçoit la hauteur de la fenêtre ;
    /// les autres défilent dans le défilement commun.
    /// </summary>
    private void ShowPage(Type pageType)
    {
        if (_pages.GetPage(pageType) is not { } page) return;
        var scrollsItself = page is DependencyObject d && d.ReadLocalValue(System.Windows.Controls.ScrollViewer.CanContentScrollProperty) is bool;
        PageScroller.VerticalScrollBarVisibility = scrollsItself
            ? System.Windows.Controls.ScrollBarVisibility.Disabled
            : System.Windows.Controls.ScrollBarVisibility.Auto;
        PageHost.Navigate(page);
    }

    /// <summary>La barre du haut sert aussi de barre de titre : glisser déplace la fenêtre, double-clic l'agrandit.</summary>
    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Bouton relâché avant le début du déplacement : rien à faire
        }
    }
}
