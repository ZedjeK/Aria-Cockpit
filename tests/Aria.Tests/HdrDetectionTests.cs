using Aria.Core.Inventory;

namespace Aria.Tests;

/// <summary>
/// Bits de DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO(_2) d'après wingdi.h : « couleur avancée » = HDR ou gamme étendue (WCG).
/// </summary>
public class HdrDetectionTests
{
    private const int Sdr = 0, Wcg = 1, Hdr = 2;

    [Fact]
    public void TowerScreenIsHdrAndActive()
    {
        // MSI MAG342CQR relevé sur la tour : 0xF3 (couleur avancée active, HDR et WCG pris en charge et choisis), mode HDR
        Assert.Equal((true, true), DisplayConfig.DecodeHdr(0xF3, Hdr));
    }

    [Fact]
    public void LaptopWithAutoColorManagementIsNotHdr()
    {
        // Portable avec gestion automatique des couleurs : couleur avancée active, gamme étendue seulement
        Assert.Equal((false, false), DisplayConfig.DecodeHdr(0xC3, Wcg));
    }

    [Fact]
    public void WideGamutScreenWithoutHdrIsNotHdr()
    {
        Assert.Equal((false, false), DisplayConfig.DecodeHdr(0x41, Sdr));
    }

    [Fact]
    public void HdrScreenLeftInSdrIsAvailableButInactive()
    {
        Assert.Equal((true, false), DisplayConfig.DecodeHdr(0x11, Sdr));
    }

    [Fact]
    public void HdrChosenButLimitedByPolicyIsNotActive()
    {
        // HDR choisi par l'utilisateur mais limité (sur batterie, par exemple) : le mode actif reste SDR
        Assert.Equal((true, false), DisplayConfig.DecodeHdr(0x39, Sdr));
    }

    [Fact]
    public void LegacyApiActiveWithoutWideColorIsHdr()
    {
        Assert.Equal((true, true), DisplayConfig.DecodeHdrLegacy(0x3));
    }

    [Fact]
    public void LegacyApiWideColorEnforcedIsNotHdr()
    {
        // Ancienne interface : couleur avancée active mais gamme étendue forcée = WCG, pas HDR
        Assert.Equal(((bool?)null, false), DisplayConfig.DecodeHdrLegacy(0x7));
    }

    [Fact]
    public void LegacyApiOnlySupportedLeavesHdrSupportUnknown()
    {
        // « Couleur avancée prise en charge » peut n'être que de la gamme étendue : pas de faux « HDR disponible »
        Assert.Equal(((bool?)null, false), DisplayConfig.DecodeHdrLegacy(0x1));
    }
}
