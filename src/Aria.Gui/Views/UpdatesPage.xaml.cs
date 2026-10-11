using System.Windows.Controls;
using Aria.Gui.ViewModels;

namespace Aria.Gui.Views;

public partial class UpdatesPage : Page
{
    public UpdatesPage(UpdatesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
