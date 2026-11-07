namespace Aria.Core.Models;

/// <summary>Fabricants dont Aria Cockpit sait lire et régler la limite de charge de la batterie depuis Windows.</summary>
public enum ChargeLimitVendor { Dell, Hp, Asus, Lenovo }

/// <summary>Limite de charge de la batterie lue sur le portable, quel que soit le fabricant.</summary>
/// <param name="Raw">Valeur technique, sauvegardée et réécrite telle quelle par la Restauration.</param>
/// <param name="Display">Valeur lisible.</param>
/// <param name="Protects">Vrai si la batterie ne reste pas chargée à 100 % : charge plafonnée ou mode qui la ménage.</param>
public sealed record ChargeLimitInfo(ChargeLimitVendor Vendor, string Raw, string Display, bool Protects)
{
    /// <summary>
    /// ASUS et Lenovo : interface du pilote du fabricant que ni l'un ni l'autre ne documente (celle qu'utilisent
    /// G-Helper et Lenovo Legion Toolkit). Dell et HP publient la leur.
    /// </summary>
    public bool Experimental => Vendor is ChargeLimitVendor.Asus or ChargeLimitVendor.Lenovo;

    public string VendorName => VendorNames.Of(Vendor);
}

public static class VendorNames
{
    public static string Of(ChargeLimitVendor vendor) => vendor switch
    {
        ChargeLimitVendor.Dell => "Dell",
        ChargeLimitVendor.Hp => "HP",
        ChargeLimitVendor.Asus => "ASUS",
        _ => "Lenovo"
    };
}
