using Aria.Core.Models;
using Aria.Core.Settings;

namespace Aria.Core.Windows;

/// <summary>"Améliorer la précision du pointeur" : accélération de la souris.</summary>
public sealed class MouseAccelerationSetting : SettingBase
{
    public override string Id => "windows.mouseacceleration";
    public override string DisplayName => "Accélération de la souris";
    public override SettingCategory Category => SettingCategory.Windows;

    /// <summary>Valeur brute "seuil1,seuil2,accélération" (les 3 valeurs de Windows, pour une restauration exacte).</summary>
    public override SettingValue ReadCurrent(HardwareProfile hw)
    {
        var values = new int[3];
        if (!NativeSpi.SystemParametersInfo(NativeSpi.GetMouse, 0, values, 0)) NativeSpi.ThrowLastError("Lecture de la souris");
        return Value(values);
    }

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value([0, 0, 0]), "Le curseur se déplace toujours de la même distance pour un même geste : visée plus régulière.");

    /// <summary>On compare l'effet (accélération oui/non), pas les seuils.</summary>
    public override bool IsAtTarget(SettingValue current, SettingValue target) =>
        IsAccelerated(current) == IsAccelerated(target);

    public override void Apply(HardwareProfile hw, SettingValue value)
    {
        var values = value.Raw.Split(',').Select(int.Parse).ToArray();
        if (!NativeSpi.SystemParametersInfo(NativeSpi.SetMouse, 0, values, NativeSpi.SaveAndNotify))
            NativeSpi.ThrowLastError("Réglage de la souris");
    }

    private static bool IsAccelerated(SettingValue value) => value.Raw.Split(',').LastOrDefault() is { } a && a != "0";

    private static SettingValue Value(int[] v) =>
        new(string.Join(",", v), v[2] != 0 ? "Activée" : "Désactivée");
}

/// <summary>Effets d'animation de Windows : préférence de confort, décochée par défaut.</summary>
public sealed class AnimationsSetting : SettingBase
{
    public override string Id => "windows.animations";
    public override string DisplayName => "Animations de Windows";
    public override SettingCategory Category => SettingCategory.Windows;
    public override bool SelectedByDefault => false;

    public override SettingValue ReadCurrent(HardwareProfile hw)
    {
        var enabled = 0;
        if (!NativeSpi.SystemParametersInfo(NativeSpi.GetClientAreaAnimation, 0, ref enabled, 0))
            NativeSpi.ThrowLastError("Lecture des animations");
        return Value(enabled != 0);
    }

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(false), "Option de confort : interface plus vive à l'œil, sans gain réel en jeu.");

    public override void Apply(HardwareProfile hw, SettingValue value)
    {
        if (!NativeSpi.SystemParametersInfo(NativeSpi.SetClientAreaAnimation, 0, (IntPtr)(value.Raw == "1" ? 1 : 0), NativeSpi.SaveAndNotify))
            NativeSpi.ThrowLastError("Réglage des animations");
    }

    private static SettingValue Value(bool enabled) => new(enabled ? "1" : "0", enabled ? "Activées" : "Désactivées");
}
