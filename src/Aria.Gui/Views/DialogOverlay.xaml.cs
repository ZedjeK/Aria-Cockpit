using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using Aria.Gui.Services;
using Wpf.Ui.Controls;

namespace Aria.Gui.Views;

/// <summary>Confirmation intégrée : apparition animée, Entrée = bouton actif, Échap = annuler.</summary>
public partial class DialogOverlay : UserControl
{
    public DialogOverlay()
    {
        InitializeComponent();
        IsVisibleChanged += OnVisibleChanged;
    }

    private DialogService? Dialog => DataContext as DialogService;

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;

        var risky = Dialog?.IsRisky == true;
        HeaderIcon.Symbol = Enum.TryParse<SymbolRegular>(Dialog?.Icon, out var symbol) ? symbol
            : risky ? SymbolRegular.Power24 : SymbolRegular.ShieldCheckmark24;

        // Apparition : fondu + léger zoom
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration));
        PanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });
        PanelScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, duration) { EasingFunction = ease });

        // Bouton actif par défaut : « Annuler » pour une action lourde (redémarrage), sinon la confirmation
        Dispatcher.BeginInvoke(() => (risky ? CancelButton : ConfirmButton).Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Dialog is not { IsOpen: true } dialog) return;

        if (e.Key == Key.Escape)
        {
            dialog.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            // Entrée active le bouton qui a le focus (Annuler ou Confirmer)
            if (CancelButton.IsKeyboardFocused) dialog.CancelCommand.Execute(null);
            else dialog.ConfirmCommand.Execute(null);
            e.Handled = true;
        }
    }
}
