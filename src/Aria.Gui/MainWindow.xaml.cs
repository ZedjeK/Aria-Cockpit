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
    private readonly ViewModels.AppUpdateViewModel _updater = ViewModels.AppUpdateViewModel.Shared;

    public MainWindow(AppState state)
    {
        DataContext = state;
        _pages = new PageProvider(state);
        // Réglages du fond animé, lus une fois : la page Paramètres les met à jour ensuite
        BackdropService.Set(Aria.Core.Settings.AppPreferencesStore.Load().Background);
        InitializeComponent();
        UpdateOverlay.DataContext = _updater;
        _updater.RelaunchRequested += OnRelaunch;
        // Une recherche, au lancement ou depuis Paramètres, vient de trouver une version : le bandeau la signale
        _updater.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ViewModels.AppUpdateViewModel.Available)) ShowUpdateBanner();
        };
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
        if (e.Cancel || _quitting || Tray is not { } tray) return;

        // Ce que fait la croix se choisit dans les Paramètres ; par défaut : ranger si quelque chose est actif
        var preferences = Aria.Core.Settings.AppPreferencesStore.Load();
        var state = tray.Current();
        switch (state.OnClose(preferences.Close))
        {
            case Aria.Core.Tray.CloseAction.Quit:
                return;
            case Aria.Core.Tray.CloseAction.AskThenQuit:
                // « Toujours quitter » ne coupe pas une opération au milieu sans le dire
                if (System.Windows.MessageBox.Show(
                        $"Une opération est en cours : {string.Join(", ", state.Work)}.\n\nQuitter maintenant l'arrête au milieu. Quitter quand même ?",
                        "Aria Cockpit", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning,
                        System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes)
                    e.Cancel = true;
                return;
        }
        e.Cancel = true;
        Hide();
        if (!preferences.NotifyOnHide) return;
        var reason = state.KeepReason() ?? "Tu as choisi de toujours la ranger dans son icône.";
        tray.Notify("Aria Cockpit continue en arrière-plan",
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

    /// <summary>
    /// Au lancement : dit ce qui vient d'être installé si Aria a été relancée par une mise à jour, puis cherche une
    /// version plus récente sur GitHub, sauf si la recherche au lancement est décochée dans Paramètres. Rien n'est
    /// téléchargé.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        if (await Task.Run(AppUpdateInstaller.TakeDoneMarker) is { } done)
        {
            UpdateLabel.Text = "MISE À JOUR FAITE";
            UpdateText.Text = $"Aria Cockpit {done.To.ToString(3)} est en place. La {done.From.ToString(3)} est gardée : tu peux y revenir depuis Paramètres.";
            UpdateButton.Visibility = Visibility.Collapsed;
            UpdateBanner.Visibility = Visibility.Visible;
        }
        var preferences = await Task.Run(Aria.Core.Settings.AppPreferencesStore.Load);
        if (preferences.CheckUpdates) await _updater.CheckAsync();
    }

    private void ShowUpdateBanner()
    {
        if (_updater.Available is not { } update) return;
        UpdateLabel.Text = "MISE À JOUR DISPONIBLE";
        UpdateText.Text = $"Aria Cockpit {update.Latest.ToString(3)} est disponible. Tu as la {_updater.CheckedVersionText}. Rien n'est téléchargé sans ton accord.";
        UpdateButton.Visibility = Visibility.Visible;
        UpdateBanner.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Les fichiers d'une autre version viennent d'être mis en place (mise à jour, ou retour à la version gardée) :
    /// lance l'exe et arrête cet exemplaire. La place d'exemplaire unique est rendue d'abord, pour que la version
    /// lancée la prenne même si elle n'est pas plus récente.
    /// </summary>
    private void OnRelaunch(string exe)
    {
        // La courbe de ventilation est rendue au pilote avant le lancement, et non à la fermeture : sinon l'arrêt de
        // cet exemplaire pourrait couper la courbe que la version lancée vient de relancer.
        try
        {
            FanCurveService.StopForExit();
        }
        catch (Exception e)
        {
            ErrorLog.Write(e);
        }
        SingleInstance.Release();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or System.IO.IOException)
        {
            SingleInstance.Claim();
            System.Windows.MessageBox.Show(
                $"Les fichiers sont en place, mais Aria Cockpit n'a pas pu se relancer : {e.Message}\n\nQuitte-la, puis relance-la à la main.",
                "Aria Cockpit", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }
        Quit();
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

    /// <summary>« Voir » du bandeau : la fenêtre de mise à jour, par-dessus la page. Rien n'est téléchargé à l'ouverture.</summary>
    private void OnOpenUpdate(object sender, RoutedEventArgs e)
    {
        _updater.OpenAvailableCommand.Execute(null);
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
