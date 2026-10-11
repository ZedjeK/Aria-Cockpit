using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Aria.Gui.ViewModels;

namespace Aria.Gui.Effects;

/// <summary>Couleur du score : rouge (bas), orange (moyen), vert (bon).</summary>
public sealed class ScoreLevelToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ScoreLevel.Low => "NeonRedBrush",
            ScoreLevel.High => "NeonCyanBrush",
            _ => "NeonOrangeBrush"
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
