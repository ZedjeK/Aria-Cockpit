using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Aria.Core.Models;

namespace Aria.Gui.Services;

/// <summary>Couleur néon d'une pastille de diagnostic (définie dans Theme/Futuriste.xaml).</summary>
public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value is Severity severity
            ? severity switch
            {
                Severity.Critical => "SeverityCriticalBrush",
                Severity.Warning => "SeverityWarningBrush",
                Severity.Ok => "SeverityOkBrush",
                _ => "SeverityInfoBrush"
            }
            : "NeonMutedBrush";

        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
