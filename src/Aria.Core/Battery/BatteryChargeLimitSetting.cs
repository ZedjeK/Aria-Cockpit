using Aria.Core.Models;
using Aria.Core.Settings;

namespace Aria.Core.Battery;

/// <summary>
/// Limite de charge de la batterie (portables Dell, HP, ASUS, Lenovo grand public) : une batterie lithium-ion qui reste
/// pleine, surtout au chaud, perd sa capacité bien plus vite. Le cas typique : un portable de jeu branché en permanence.
/// Cochée d'office pour Dell et HP (interfaces publiées par le fabricant), au choix pour ASUS et Lenovo (expérimental).
/// </summary>
public sealed class BatteryChargeLimitSetting(string exePath, ChargeLimitVendor? vendor) : SettingBase
{
    public const string SettingId = "battery.chargelimit";

    public override string Id => SettingId;
    public override string DisplayName => "Limite de charge de la batterie";
    public override SettingCategory Category => SettingCategory.Battery;
    public override bool HiddenWhenNotApplicable => true;
    public override bool SelectedByDefault => vendor is ChargeLimitVendor.Dell or ChargeLimitVendor.Hp;

    /// <summary>Interface non documentée par le fabricant (ASUS, Lenovo) : affichée comme expérimentale.</summary>
    public bool Experimental => vendor is ChargeLimitVendor.Asus or ChargeLimitVendor.Lenovo;

    public override string? WhyNotApplicable(HardwareProfile hw) =>
        hw.ChargeLimit is null || vendor is null
            ? "Ce portable n'expose pas sa limite de charge à Windows"
            : Control().Blocker();

    public override SettingValue ReadCurrent(HardwareProfile hw) => Value(Read());

    public override SettingTarget ComputeTarget(HardwareProfile hw)
    {
        var current = Read();
        if (current.Protects) return new(Value(current), "Déjà réglée pour ménager la batterie.");

        var reason = vendor switch
        {
            ChargeLimitVendor.Dell => "La charge s'arrête à 80 % et ne reprend que sous 50 %, par le BIOS Dell.",
            ChargeLimitVendor.Hp => "Mode « Battery Health Manager » du BIOS HP qui plafonne la charge.",
            ChargeLimitVendor.Asus => "Arrêt à 80 %, par le pilote ASUS (méthode de G-Helper, non documentée par ASUS) ; " +
                                      "réappliqué à chaque ouverture de session, le BIOS remettant 100 % au démarrage.",
            _ => "Mode conservation de Lenovo, par le pilote de Lenovo Vantage (méthode de Lenovo Legion Toolkit, " +
                 "non documentée par Lenovo) ; plafond fixé par Lenovo selon le modèle."
        };
        return new(Value(Control().Recommended()),
            "Une batterie qui reste pleine s'use bien plus vite, surtout sur un portable souvent branché. " + reason);
    }

    public override void Apply(HardwareProfile hw, SettingValue value)
    {
        var control = Control();
        var current = control.Read() ?? throw new InvalidOperationException("Limite de charge illisible.");
        control.Write(current, value.Raw);
        // ASUS ne relit pas la limite : la valeur demandée fait foi
        var applied = control.CanReadBack
            ? control.Read() ?? throw new InvalidOperationException("Limite de charge illisible après écriture.")
            : control.InfoFor(value.Raw);
        ChargeLimits.Remember(control, applied, exePath);
    }

    private ChargeLimitInfo Read() =>
        Control().Read() ?? throw new InvalidOperationException("Limite de charge illisible.");

    private IChargeLimitControl Control() =>
        ChargeLimits.Control(vendor ?? throw new InvalidOperationException("Fabricant non pris en charge."));

    private static SettingValue Value(ChargeLimitInfo info) => new(info.Raw, info.Display);
}
