using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Aria.Gui.ViewModels;

namespace Aria.Gui.Views;

/// <summary>Mise à jour d'Aria Cockpit, intégrée à la fenêtre : apparition en fondu, Échap masque.</summary>
public partial class AppUpdateOverlay : UserControl
{
    public AppUpdateOverlay()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        // Le clavier arrive dans la fenêtre de mise à jour, pas dans la page restée derrière
        Dispatcher.BeginInvoke(() => MoveFocus(new TraversalRequest(FocusNavigationDirection.First)),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Échap masque la fenêtre. Un téléchargement en cours continue : seul « Annuler » l'arrête.</summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not AppUpdateViewModel { IsOpen: true } update) return;
        update.CloseCommand.Execute(null);
        e.Handled = true;
    }
}
