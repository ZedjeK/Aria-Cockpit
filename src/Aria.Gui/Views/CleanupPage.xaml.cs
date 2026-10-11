using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Aria.Gui.ViewModels;

namespace Aria.Gui.Views;

public partial class CleanupPage : Page
{
    /// <summary>Visible seulement si toutes les valeurs sont vraies.</summary>
    public static readonly IMultiValueConverter BothTrue = new AllTrueToVisibility();

    private readonly CleanupViewModel _viewModel;

    public CleanupPage(CleanupViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>Première ouverture : analyse de l'espace (lecture seule).</summary>
    private async void OnLoaded(object sender, RoutedEventArgs e) => await _viewModel.EnsureScannedAsync();

    private sealed class AllTrueToVisibility : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values.All(v => v is true) ? Visibility.Visible : Visibility.Collapsed;

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
