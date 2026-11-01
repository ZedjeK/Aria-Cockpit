namespace Aria.Core.Nvidia;

/// <summary>Un point de la courbe : à <see cref="TemperatureC"/> °C, les ventilateurs tournent à <see cref="Percent"/> %.</summary>
public sealed record FanCurvePoint(int TemperatureC, int Percent);

/// <summary>
/// Courbe de ventilation : vitesse interpolée entre les points. Sous le premier point, la courbe rend la main
/// au pilote, qui garde ainsi l'arrêt des ventilateurs au repos ; au-delà du dernier, la vitesse reste la sienne.
/// </summary>
public sealed record FanCurve(string Name, IReadOnlyList<FanCurvePoint> Points)
{
    public const int MinPoints = 2, MaxPoints = 6;
    public const int LowestTemperatureC = 20, HighestTemperatureC = 95;

    /// <summary>En dessous de cette température, le pilote reprend la main.</summary>
    public int AutoBelowC => Points[0].TemperatureC;

    // Points de départ, à ajuster selon la carte et le boîtier
    public static readonly FanCurve Silent = new("Silencieux", [new(50, 30), new(65, 40), new(75, 55), new(83, 80)]);
    public static readonly FanCurve Balanced = new("Équilibré", [new(45, 35), new(60, 50), new(70, 65), new(80, 85)]);
    public static readonly FanCurve Cooling = new("Refroidissement max", [new(40, 45), new(55, 65), new(65, 80), new(75, 100)]);

    public static IReadOnlyList<FanCurve> Presets { get; } = [Silent, Balanced, Cooling];

    /// <summary>Message d'erreur si la courbe est inutilisable, sinon null.</summary>
    public string? Check()
    {
        if (Points.Count is < MinPoints or > MaxPoints) return $"La courbe doit avoir de {MinPoints} à {MaxPoints} points.";
        if (Points.Any(p => p.TemperatureC is < LowestTemperatureC or > HighestTemperatureC))
            return $"Les températures vont de {LowestTemperatureC} à {HighestTemperatureC} °C.";
        if (Points.Any(p => p.Percent is < 0 or > 100)) return "Les vitesses vont de 0 à 100 %.";
        for (var i = 1; i < Points.Count; i++)
        {
            if (Points[i].TemperatureC <= Points[i - 1].TemperatureC) return "Chaque point doit être plus chaud que le précédent.";
            if (Points[i].Percent < Points[i - 1].Percent) return "La vitesse ne peut pas baisser quand la température monte.";
        }
        return null;
    }

    /// <summary>Vitesse de la courbe à une température, arrondie au pourcent supérieur.</summary>
    public int SpeedAt(double temperatureC)
    {
        if (temperatureC <= Points[0].TemperatureC) return Points[0].Percent;
        for (var i = 1; i < Points.Count; i++)
        {
            var (a, b) = (Points[i - 1], Points[i]);
            if (temperatureC <= b.TemperatureC)
                return (int)Math.Ceiling(a.Percent + (b.Percent - a.Percent) * (temperatureC - a.TemperatureC) / (b.TemperatureC - a.TemperatureC));
        }
        return Points[^1].Percent;
    }
}
