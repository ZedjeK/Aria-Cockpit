using System.Windows.Controls;
using Aria.Gui.ViewModels;

namespace Aria.Gui.Views;

public partial class GpuTuningPage : Page
{
    public GpuTuningPage(GpuTuningViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        // Lecture de la carte et télémétrie : seulement pendant que la page est affichée
        Loaded += (_, _) => viewModel.OnShown();
        Unloaded += (_, _) => viewModel.OnHidden();
    }
}
