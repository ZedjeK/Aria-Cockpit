using System.Windows.Controls;
using Aria.Gui.ViewModels;

namespace Aria.Gui.Views;

public partial class UpdatesPage : Page
{
    public UpdatesPage(UpdatesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        // Historique de Windows Update relu à chaque affichage de la page (lecture locale, rapide)
        Loaded += (_, _) =>
        {
            if (viewModel.LoadHistoryCommand.CanExecute(null)) viewModel.LoadHistoryCommand.Execute(null);
        };
    }
}
