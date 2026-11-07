namespace Aria.Core.Battery;

/// <summary>
/// Où régler la limite de charge quand Aria Cockpit ne peut pas le faire lui-même : logiciel du fabricant ou BIOS.
/// Acer et MSI n'ont pas d'interface utilisable proprement depuis Windows (MSI : écriture directe dans le contrôleur
/// embarqué par un pilote noyau que Windows signale comme dangereux) ; les ThinkPad passent par Lenovo Vantage.
/// </summary>
public static class ChargeLimitAdvice
{
    public static string For(string manufacturer, string model)
    {
        var m = manufacturer.Trim();
        return ChargeLimits.VendorOf(m) switch
        {
            Models.ChargeLimitVendor.Dell =>
                "Dell Power Manager ou Dell Optimizer, sinon dans le BIOS (F2) : « Primary Battery Charge Configuration », " +
                "mode Custom, reprise à 50 % et arrêt à 80 %.",
            Models.ChargeLimitVendor.Hp =>
                "Dans le BIOS (F10) : « Battery Health Manager » → « Maximize My Battery Health » ; " +
                "sur certains modèles, « Battery Care Function ».",
            Models.ChargeLimitVendor.Asus =>
                "MyASUS ou Armoury Crate : « Battery Health Charging ». Aria peut le faire lui-même quand les pilotes ASUS " +
                "(ATKACPI) sont installés.",
            Models.ChargeLimitVendor.Lenovo when model.Contains("ThinkPad", StringComparison.OrdinalIgnoreCase) =>
                "Lenovo Vantage : seuils de charge personnalisés de la batterie.",
            Models.ChargeLimitVendor.Lenovo =>
                "Lenovo Vantage : mode conservation. Aria peut le faire lui-même quand le pilote Lenovo Energy Management " +
                "est installé.",
            _ when m.Contains("Acer", StringComparison.OrdinalIgnoreCase) =>
                "Acer Care Center, AcerSense ou NitroSense selon le modèle : « Battery Charge Limit » (80 %).",
            _ when m.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) || m.Equals("MSI", StringComparison.OrdinalIgnoreCase) =>
                "MSI Center : « Battery Master » (plafond à 60 ou 80 %).",
            _ when m.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) =>
                "Application Surface : mode de charge « Limiter à 80 % ».",
            _ => "Logiciel du fabricant ou BIOS : option de limite de charge (souvent 80 %), si le modèle en a une."
        };
    }
}
