using System.Globalization;
using System.Windows.Data;

namespace Aria.Gui.Effects;

/// <summary>Nombre de colonnes de cartes selon la largeur disponible : 2, 3, ou 4 sur grand écran.</summary>
public sealed class WidthToColumnsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is not double width || width <= 0 ? 3
        : width < 1000 ? 2
        : width < 1300 ? 3
        : 4;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
