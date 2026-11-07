using Aria.Core.Battery;
using Aria.Core.Models;

namespace Aria.Core.Diagnostics.Checks;

/// <summary>
/// Limite de charge de la batterie des portables : une batterie qui reste pleine s'use plus vite. Réglée par Aria sur
/// Dell, HP, ASUS et Lenovo grand public ; ailleurs, le diagnostic dit où la régler.
/// </summary>
public sealed class BatteryChargeCheck : IDiagnosticCheck
{
    internal const string Why = "Une batterie lithium-ion qui reste chargée à 100 %, surtout au chaud, perd sa capacité bien " +
                                "plus vite. C'est le cas d'un portable de jeu branché en permanence.";

    public string Name => "limite de charge de la batterie";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var hw = ctx.Hardware;
        if (hw.IsLaptop && hw.Machine.HasBattery)
            yield return Evaluate(hw.ChargeLimit, hw.Machine.Manufacturer, hw.Machine.Model);
    }

    internal static DiagnosticFinding Evaluate(ChargeLimitInfo? limit, string manufacturer, string model)
    {
        if (limit is null)
            return new("battery.chargelimit", "Limite de charge de la batterie : à régler chez le fabricant", Severity.Info,
                $"{Why} Ce portable n'expose pas sa limite de charge à Aria Cockpit.",
                ChargeLimitAdvice.For(manufacturer, model));

        if (limit.Protects)
            return new("battery.chargelimit", "Limite de charge de la batterie active", Severity.Ok,
                $"{limit.Display} ({limit.VendorName}).");

        return new("battery.chargelimit", "Batterie chargée jusqu'à 100 %", Severity.Info,
            $"Mode de charge : {LowerFirst(limit.Display)} ({limit.VendorName}). {Why}",
            "Réglages → Batterie : limite de charge" + (limit.Experimental ? " (expérimental sur ce fabricant)." : "."));
    }

    /// <summary>« Gérée par HP » → « gérée par HP » : seule la première lettre change (les noms de marque restent).</summary>
    public static string LowerFirst(string text) => text.Length == 0 ? text : char.ToLowerInvariant(text[0]) + text[1..];
}
