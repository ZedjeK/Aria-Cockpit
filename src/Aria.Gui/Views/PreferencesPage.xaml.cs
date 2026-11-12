using System.Windows.Controls;
using Aria.Gui.ViewModels;

namespace Aria.Gui.Views;

public partial class PreferencesPage : Page
{
    public PreferencesPage(PreferencesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        // À chaque affichage : l'état réel des tâches de démarrage et la taille du dossier
        Loaded += async (_, _) => await viewModel.LoadAsync();
    }
}
