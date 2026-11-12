using Aria.Core.Battery;
using Aria.Core.Diagnostics.Checks;
using Aria.Core.Models;

namespace Aria.Tests;

/// <summary>
/// Limite de charge de la batterie : fabricant reconnu, Dell (repris de CTI-Cockpit), HP, ASUS, Lenovo, conseils pour
/// les autres marques et diagnostic. Rien n'y touche au BIOS ni aux pilotes : seule la logique est testée.
/// </summary>
public class ChargeLimitTests
{
    // ---- Fabricant ----

    [Theory]
    [InlineData("Dell Inc.", ChargeLimitVendor.Dell)]
    [InlineData("Alienware", ChargeLimitVendor.Dell)]
    [InlineData("HP", ChargeLimitVendor.Hp)]
    [InlineData("Hewlett-Packard", ChargeLimitVendor.Hp)]
    [InlineData("ASUSTeK COMPUTER INC.", ChargeLimitVendor.Asus)]
    [InlineData("LENOVO", ChargeLimitVendor.Lenovo)]
    [InlineData("Acer", null)]
    [InlineData("Micro-Star International Co., Ltd.", null)]
    [InlineData("HPE Solutions", null)]   // ne commence pas par « HP » suivi d'un espace
    public void VendorIsRecognised(string manufacturer, ChargeLimitVendor? expected)
    {
        Assert.Equal(expected, ChargeLimits.VendorOf(manufacturer));
    }

    // ---- Dell ----

    private static DellChargeMode Dell(string mode, int start = 50, int stop = 90) => new(mode, start, stop);

    [Theory]
    [InlineData("Custom", 50, 80, true)]
    [InlineData("Custom", 75, 85, true)]
    [InlineData("Custom", 50, 90, false)]    // arrêt trop haut pour vraiment ménager la batterie
    [InlineData("PrimAcUse", 0, 0, true)]
    [InlineData("Standard", 50, 80, false)]  // seuils présents mais ignorés hors mode Custom
    [InlineData("Adaptive", 0, 0, false)]
    public void DellProtectiveModesAreRecognised(string mode, int start, int stop, bool protects)
    {
        Assert.Equal(protects, new DellChargeMode(mode, start, stop).Protects);
    }

    [Theory]
    [InlineData("Custom:50-80", "Custom", 50, 80)]
    [InlineData("custom:60-90", "Custom", 60, 90)]
    [InlineData("Standard", "Standard", 0, 0)]
    public void DellSavedValueIsReadBack(string raw, string mode, int start, int stop)
    {
        var limit = DellChargeMode.Parse(raw);
        Assert.Equal(mode, limit.Mode);
        Assert.Equal((start, stop), (limit.Start, limit.Stop));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Custom:")]
    [InlineData("Custom:50")]
    public void DellUnreadableSavedValueIsRefused(string raw)
    {
        Assert.Throws<FormatException>(() => DellChargeMode.Parse(raw));
    }

    [Fact]
    public void DellStandardToRecommendedSetsStopThenSwitchesMode()
    {
        var steps = DellChargeLimit.WriteSteps(Dell("Standard", 50, 100), DellChargeMode.Recommended);

        Assert.Equal([("CustomChargeStop", "80"), ("PrimaryBattChargeCfg", "Custom")], steps);
    }

    [Fact]
    public void DellStartIsLoweredFirstWhenTheCurrentStopAllowsIt()
    {
        // Seuils actuels 90-95 : baisser l'arrêt à 80 d'abord laisserait la reprise (90) au-dessus, le BIOS refuserait
        var steps = DellChargeLimit.WriteSteps(Dell("Custom", 90, 95), DellChargeMode.Recommended);

        Assert.Equal([("CustomChargeStart", "50"), ("CustomChargeStop", "80")], steps);
    }

    [Fact]
    public void DellRestoringAModeOnlyWritesTheMode()
    {
        Assert.Equal([("PrimaryBattChargeCfg", "Standard")],
            DellChargeLimit.WriteSteps(DellChargeMode.Recommended, DellChargeMode.Parse("Standard")));
    }

    [Theory]
    [InlineData(40, 80)]   // reprise sous 50
    [InlineData(50, 50)]   // moins de 5 points d'écart
    [InlineData(50, 101)]
    public void DellOutOfRangeThresholdsAreRefused(int start, int stop)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            DellChargeLimit.WriteSteps(Dell("Standard"), new DellChargeMode("Custom", start, stop)));
    }

    // ---- HP ----

    [Theory]
    [InlineData(new[] { "Let HP Manage My Battery Charging", "Maximize My Battery Duration", "Maximize My Battery Health" }, "Maximize My Battery Health")]
    [InlineData(new[] { "Maximize Battery Duration", "Maximize Battery Health Management" }, "Maximize Battery Health Management")]
    [InlineData(new[] { "Disable", "Enable" }, null)]
    public void HpHealthValueIsReadFromTheBiosChoices(string[] possible, string? expected)
    {
        Assert.Equal(expected, HpChargeLimit.HealthValue(possible));
    }

    [Theory]
    [InlineData("Maximize My Battery Health", true)]
    [InlineData("Let HP Manage My Battery Charging", false)]
    [InlineData("Maximize My Battery Duration", false)]
    public void HpOnlyTheHealthModeProtects(string value, bool protects)
    {
        Assert.Equal(protects, HpChargeLimit.Info(value).Protects);
        Assert.Equal(value, HpChargeLimit.Info(value).Raw);   // la Restauration réécrit la valeur exacte du BIOS
    }

    // ---- ASUS ----

    [Fact]
    public void AsusRequestCarriesMethodSizeDeviceAndValue()
    {
        var request = AsusChargeLimit.Request(AsusChargeLimit.Devs, AsusChargeLimit.BatteryLimitDevice, 80);

        Assert.Equal(16, request.Length);
        Assert.Equal("DEVS", System.Text.Encoding.ASCII.GetString(request, 0, 4));
        Assert.Equal(8u, BitConverter.ToUInt32(request, 4));
        Assert.Equal(0x00120057u, BitConverter.ToUInt32(request, 8));
        Assert.Equal(80u, BitConverter.ToUInt32(request, 12));
        Assert.Equal("DSTS", System.Text.Encoding.ASCII.GetString(AsusChargeLimit.Request(AsusChargeLimit.Dsts, 0, 0), 0, 4));
    }

    [Theory]
    [InlineData(0x00010000, true)]
    [InlineData(0x00010064, true)]
    [InlineData(0x00000000, false)]
    [InlineData(-2, false)]   // 0xFFFFFFFE : fonction inconnue
    public void AsusPresenceBitSaysIfTheModelHasTheFunction(int status, bool present)
    {
        Assert.Equal(present, AsusChargeLimit.IsPresent(status));
    }

    [Theory]
    [InlineData("80", 80, true)]
    [InlineData("60", 60, true)]
    [InlineData("100", 100, false)]
    [InlineData("90", 90, false)]
    public void AsusLimitIsReadBack(string raw, int limit, bool protects)
    {
        Assert.Equal(limit, AsusChargeLimit.Parse(raw));
        Assert.Equal(protects, AsusChargeLimit.Info(limit).Protects);
    }

    [Theory]
    [InlineData("39")]
    [InlineData("101")]
    [InlineData("Custom:50-80")]
    public void AsusOutOfRangeLimitIsRefused(string raw)
    {
        Assert.Throws<FormatException>(() => AsusChargeLimit.Parse(raw));
    }

    // ---- Lenovo ----

    private static uint DriverOutput(params int[] bits)
    {
        // Le pilote renvoie l'état octets inversés : on construit l'état, puis on l'inverse comme lui
        var state = bits.Aggregate(0u, (value, bit) => value | (1u << bit));
        return System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(state);
    }

    [Fact]
    public void LenovoStateIsDecoded()
    {
        Assert.Equal(LenovoChargeLimit.Mode.Normal, LenovoChargeLimit.Decode(DriverOutput(17)));
        Assert.Equal(LenovoChargeLimit.Mode.Rapid, LenovoChargeLimit.Decode(DriverOutput(17, 26)));
        Assert.Equal(LenovoChargeLimit.Mode.Conservation, LenovoChargeLimit.Decode(DriverOutput(29)));
        Assert.Equal(LenovoChargeLimit.Mode.Normal, LenovoChargeLimit.Decode(DriverOutput(17, 29)));   // en charge : pas en conservation
        Assert.Null(LenovoChargeLimit.Decode(0));
    }

    [Fact]
    public void LenovoRapidChargeIsTurnedOffBeforeConservation()
    {
        Assert.Equal([0x8u, 0x3u], LenovoChargeLimit.Codes(LenovoChargeLimit.Mode.Rapid, LenovoChargeLimit.Mode.Conservation));
        Assert.Equal([0x3u], LenovoChargeLimit.Codes(LenovoChargeLimit.Mode.Normal, LenovoChargeLimit.Mode.Conservation));
        Assert.Equal([0x5u], LenovoChargeLimit.Codes(LenovoChargeLimit.Mode.Conservation, LenovoChargeLimit.Mode.Normal));
        Assert.Equal([0x5u, 0x7u], LenovoChargeLimit.Codes(LenovoChargeLimit.Mode.Conservation, LenovoChargeLimit.Mode.Rapid));
    }

    [Theory]
    [InlineData("Conservation", true)]
    [InlineData("Normale", false)]
    [InlineData("Rapide", false)]
    public void LenovoSavedModeIsReadBack(string raw, bool protects)
    {
        var info = LenovoChargeLimit.Info(LenovoChargeLimit.Parse(raw));
        Assert.Equal(raw, info.Raw);
        Assert.Equal(protects, info.Protects);
        Assert.True(info.Experimental);
    }

    // ---- Conseils et diagnostic ----

    [Theory]
    [InlineData("Acer", "Aspire 5", "Battery Charge Limit")]
    [InlineData("Micro-Star International Co., Ltd.", "Katana 15", "Battery Master")]
    [InlineData("LENOVO", "ThinkPad T14 Gen 4", "seuils de charge")]
    [InlineData("LENOVO", "Legion 5 15ACH6H", "mode conservation")]
    [InlineData("Microsoft Corporation", "Surface Laptop 5", "Surface")]
    [InlineData("Razer", "Blade 15", "Logiciel du fabricant")]
    public void AdviceDependsOnTheBrand(string manufacturer, string model, string expected)
    {
        Assert.Contains(expected, ChargeLimitAdvice.For(manufacturer, model));
    }

    [Fact]
    public void ActiveLimitIsCompliant()
    {
        var finding = BatteryChargeCheck.Evaluate(DellChargeMode.Recommended.ToInfo(), "Dell Inc.", "XPS 15");

        Assert.Equal(Severity.Ok, finding.Severity);
        Assert.Contains("50 à 80 %", finding.Detail);
    }

    [Fact]
    public void FullChargePointsToTheSetting()
    {
        var finding = BatteryChargeCheck.Evaluate(HpChargeLimit.Info("Let HP Manage My Battery Charging"), "HP", "OMEN 16");

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal("Batterie chargée jusqu'à 100 %", finding.Title);
        Assert.Contains("gérée par HP", finding.Detail);   // marque gardée en majuscules
        Assert.Contains("Optimisation → Batterie", finding.Advice);
    }

    [Fact]
    public void ExperimentalVendorsAreFlagged()
    {
        var finding = BatteryChargeCheck.Evaluate(AsusChargeLimit.Info(100), "ASUSTeK COMPUTER INC.", "ROG Strix G16");

        Assert.Contains("expérimental", finding.Advice);
    }

    [Fact]
    public void UnsupportedLaptopGetsTheBrandAdvice()
    {
        var finding = BatteryChargeCheck.Evaluate(null, "Acer", "Nitro AN515-58");

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Contains("NitroSense", finding.Advice);
    }

    // ---- Réglage ----

    [Fact]
    public void SettingIsHiddenWithoutAnInterface()
    {
        var setting = new BatteryChargeLimitSetting("Aria-Cockpit.exe", vendor: null);
        var laptop = new HardwareProfile { Machine = new MachineInfo("Acer", "Aspire 5", MachineType.Laptop, true) };

        Assert.True(setting.HiddenWhenNotApplicable);
        Assert.NotNull(setting.WhyNotApplicable(laptop));
    }

    [Theory]
    [InlineData(ChargeLimitVendor.Dell, true, false)]
    [InlineData(ChargeLimitVendor.Hp, true, false)]
    [InlineData(ChargeLimitVendor.Asus, false, true)]
    [InlineData(ChargeLimitVendor.Lenovo, false, true)]
    public void OfficialInterfacesAreCheckedExperimentalOnesAreNot(ChargeLimitVendor vendor, bool selected, bool experimental)
    {
        var setting = new BatteryChargeLimitSetting("Aria-Cockpit.exe", vendor);

        Assert.Equal(selected, setting.SelectedByDefault);
        Assert.Equal(experimental, setting.Experimental);
        Assert.Equal(SettingCategory.Battery, setting.Category);
    }
}
