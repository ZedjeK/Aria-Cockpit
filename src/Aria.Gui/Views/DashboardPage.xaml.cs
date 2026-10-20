using System.Windows;
using System.Windows.Controls;
using Aria.Gui.ViewModels;

namespace Aria.Gui.Views;

public partial class DashboardPage : Page
{
    public DashboardPage(DashboardViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        // Télémétrie : lectures seulement pendant que la page est affichée
        Loaded += (_, _) => viewModel.Telemetry.Start();
        Unloaded += (_, _) => viewModel.Telemetry.Stop();
    }

    /// <summary>Un bloc du résumé : descend jusqu'au diagnostic détaillé (titre en haut de l'écran).</summary>
    private void OnSummaryClick(object sender, RoutedEventArgs e) =>
        DiagnosticHeader.BringIntoView(new Rect(0, 0, DiagnosticHeader.ActualWidth, 700));
}
