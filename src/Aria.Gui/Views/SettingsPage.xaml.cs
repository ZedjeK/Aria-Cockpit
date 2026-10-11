using System.Windows.Controls;
using Aria.Gui.ViewModels;

namespace Aria.Gui.Views;

public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
