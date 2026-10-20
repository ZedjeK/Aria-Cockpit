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
        Loaded += (_, _) =>
        {
            ShowPage(typeof(DashboardPage));
            _ = CheckForUpdateAsync();
        };
    }

    private static string CurrentVersionText =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    /// <summary>Au lancement : une version plus récente sur GitHub ? Si oui, bandeau discret. Rien n'est téléchargé.</summary>
    private async Task CheckForUpdateAsync()
    {
        if (!AppUpdateChecker.TryParseVersion(CurrentVersionText, out var current)) return;
        _update = await AppUpdateChecker.CheckAsync(current);
        if (_update is null) return;
        UpdateText.Text = $"Aria {_update.Latest} est disponible (version installée : {current}). Rien n'est installé automatiquement.";
        UpdateBanner.Visibility = Visibility.Visible;
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
    /// explicitement, comme Réglages avec sa barre d'action fixée en bas) reçoit la hauteur de la fenêtre ;
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
