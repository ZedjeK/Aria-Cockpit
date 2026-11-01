namespace Aria.Core.Nvidia;

public enum FanMode { Auto, Manual }

/// <summary>Ce qu'il faut envoyer aux ventilateurs : rendre la main au pilote, ou imposer une vitesse.</summary>
public readonly record struct FanCommand(FanMode Mode, uint Percent)
{
    public static FanCommand Auto => new(FanMode.Auto, 0);
    public static FanCommand Manual(uint percent) => new(FanMode.Manual, percent);
}

/// <summary>
/// Décide, à chaque relevé de température, de la commande des ventilateurs. Sans accès à la carte : testable.
/// Les sécurités ne dépendent pas de la courbe : plein régime à <see cref="FullSpeedC"/> °C, et retour au pilote
/// si la température est illisible.
/// </summary>
public sealed class FanCurveController
{
    /// <summary>Au-delà, plein régime quelle que soit la courbe.</summary>
    public const int FullSpeedC = 85;

    /// <summary>Écart de température avant de ralentir ou de rendre la main, pour éviter le va-et-vient.</summary>
    public const int HysteresisC = 3;

    /// <summary>Baisse maximale par relevé (un par seconde) : les ventilateurs ralentissent en douceur.</summary>
    public const uint MaxDecreasePerTick = 2;

    private readonly FanCurve _curve;
    private readonly uint _min, _max;

    public FanMode Mode { get; private set; } = FanMode.Auto;

    /// <summary>Dernière vitesse imposée (0 en mode automatique).</summary>
    public uint Percent { get; private set; }

    public FanCurveController(FanCurve curve, uint minPercent, uint maxPercent)
    {
        if (curve.Check() is { } error) throw new ArgumentException(error, nameof(curve));
        if (minPercent > maxPercent || maxPercent > 100) throw new ArgumentOutOfRangeException(nameof(maxPercent));
        _curve = curve;
        _min = minPercent;
        _max = maxPercent;
    }

    /// <summary>Repart du mode automatique (les ventilateurs viennent d'être rendus au pilote).</summary>
    public void Reset()
    {
        Mode = FanMode.Auto;
        Percent = 0;
    }

    /// <summary>Commande à envoyer pour ce relevé, ou null si rien ne change.</summary>
    public FanCommand? Next(uint? temperatureC)
    {
        // Température illisible : le pilote, lui, la voit toujours
        if (temperatureC is not { } t) return Mode == FanMode.Auto ? null : SwitchToAuto();
        if (t >= FullSpeedC) return Impose(_max);

        if (Mode == FanMode.Auto)
            return t < _curve.AutoBelowC ? null : Impose(Clamp(_curve.SpeedAt(t)));

        if (t < _curve.AutoBelowC - HysteresisC) return SwitchToAuto();

        var target = Clamp(_curve.SpeedAt(t));
        if (target < Percent)
        {
            // Ralentir seulement si la courbe le demande encore quelques degrés plus haut, et par petits pas
            var floor = Clamp(_curve.SpeedAt(t + HysteresisC));
            if (floor >= Percent) return null;
            target = Math.Max(floor, Percent > MaxDecreasePerTick ? Percent - MaxDecreasePerTick : 0);
        }
        return Impose(target);
    }

    private uint Clamp(int percent) => Math.Clamp((uint)Math.Max(percent, 0), _min, _max);

    /// <summary>Impose une vitesse ; null si c'est déjà celle des ventilateurs.</summary>
    private FanCommand? Impose(uint percent)
    {
        if (Mode == FanMode.Manual && Percent == percent) return null;
        Mode = FanMode.Manual;
        Percent = percent;
        return FanCommand.Manual(percent);
    }

    private FanCommand SwitchToAuto()
    {
        Reset();
        return FanCommand.Auto;
    }
}
