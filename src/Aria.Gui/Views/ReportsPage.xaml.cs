using System.Windows;
using System.Windows.Controls;
using Aria.Gui.ViewModels;

namespace Aria.Gui.Views;

public partial class ReportsPage : Page
{
    private readonly ReportsViewModel _viewModel;

    public ReportsPage(ReportsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>À chaque affichage : les rapports écrits entre-temps (réglages, mises à jour) apparaissent.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e) => _viewModel.Reload();
}
