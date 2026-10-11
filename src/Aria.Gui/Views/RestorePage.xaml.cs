using System.Windows;
using System.Windows.Controls;
using Aria.Gui.ViewModels;

namespace Aria.Gui.Views;

public partial class RestorePage : Page
{
    private readonly RestoreViewModel _viewModel;

    public RestorePage(RestoreViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>À chaque affichage : la liste reflète les sauvegardes créées entre-temps (page Réglages).</summary>
    private void OnLoaded(object sender, RoutedEventArgs e) => _viewModel.Reload();
}
