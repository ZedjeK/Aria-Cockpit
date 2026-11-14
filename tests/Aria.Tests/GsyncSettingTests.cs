using Aria.Core.Models;
using Aria.Core.Nvidia;

namespace Aria.Tests;

/// <summary>
/// Réglage « G-SYNC » de la page NVIDIA (VRR_MODE du SDK NVIDIA) : lecture, cible, restauration et conditions. Le pilote est
/// remplacé par un faux : rien n'est lu ni écrit sur la vraie carte. L'effet réel du réglage a été essayé à la main sur une
/// RTX 4080 SUPER (valeur 0 : l'écran quitte le mode VRR en moins d'une demi-seconde ; valeur d'origine remise : il y revient).
/// </summary>
[Collection("NvidiaDriverSettings")]
public sealed class GsyncSettingTests : IDisposable
{
    private readonly IDriverSettings _original = NvApiDriverSettings.Instance;
    private readonly FakeStore _store = new();

    public GsyncSettingTests() => NvApiDriverSettings.Instance = _store;

    public void Dispose() => NvApiDriverSettings.Instance = _original;

    private sealed class FakeStore : IDriverSettings
    {
        public Dictionary<uint, uint> Values { get; } = [];
        public List<uint> Restored { get; } = [];

        public DriverValue Read(uint settingId) => Values.TryGetValue(settingId, out var v) ? new DriverValue(v, true) : new DriverValue(null, false);
        public void Write(uint settingId, uint value) => Values[settingId] = value;

        public void RestoreDefault(uint settingId)
        {
            Restored.Add(settingId);
            Values.Remove(settingId);
        }
    }

    private static HardwareProfile Hardware(VrrState? vrr, bool nvidiaDisplay = true, string monitor = "LG UltraGear")
    {
        var display = new DisplayInfo(@"\\.\DISPLAY1", monitor, nvidiaDisplay ? "NVIDIA GeForce RTX 4080 SUPER" : "Intel UHD Graphics",
            IsPrimary: true, Width: 2560, Height: 1440, CurrentRefreshHz: 144, MaxRefreshHz: 144, NativeWidth: 2560, NativeHeight: 1440);
        return new HardwareProfile
        {
            Gpus = [new GpuInfo("NVIDIA GeForce RTX 4080 SUPER", GpuVendor.Nvidia, 0, "32.0.16.2114", "621.14", null, "")],
            Displays = [display],
            Nvidia = new NvidiaDriverInfo(null, null, vrr is null ? [] : [new NvidiaDisplayInfo(display.DeviceName, vrr, null)])
        };
    }

    private static readonly VrrState Possible = new(Enabled: false, Possible: true, Requested: false, InVrrMode: false);

    [Theory]
    [InlineData(0u, "Désactivé")]
    [InlineData(1u, "Plein écran seulement")]
    [InlineData(2u, "Plein écran et fenêtré")]
    public void CurrentValueIsReadAndNamed(uint stored, string expected)
    {
        _store.Values[DrsIds.VrrMode] = stored;

        var current = new GsyncSetting().ReadCurrent(Hardware(Possible));

        Assert.Equal(expected, current.Display);
        Assert.StartsWith("user:", current.Raw);
    }

    [Fact]
    public void ASettingNeverChosenShowsTheFactoryValue()
    {
        var current = new GsyncSetting().ReadCurrent(Hardware(Possible));

        Assert.Equal("Plein écran seulement (par défaut)", current.Display);
        Assert.StartsWith("default:", current.Raw);
    }

    [Fact]
    public void ProposedWhenTheMonitorAllowsGsyncEvenIfItIsOff() =>
        Assert.Null(new GsyncSetting().WhyNotApplicable(Hardware(Possible)));

    [Fact]
    public void NotProposedWithoutAnNvidiaPrimaryDisplay() =>
        Assert.Equal("Écran principal non relié à la carte NVIDIA",
            new GsyncSetting().WhyNotApplicable(Hardware(Possible, nvidiaDisplay: false)));

    [Fact]
    public void NotProposedWhenTheStateIsUnreadable() =>
        Assert.StartsWith("État G-SYNC illisible", new GsyncSetting().WhyNotApplicable(Hardware(vrr: null)));

    [Fact]
    public void NotProposedWhenTheMonitorCannotDoGsync() =>
        Assert.Equal("LG UltraGear ne permet pas G-SYNC (écran ou branchement)",
            new GsyncSetting().WhyNotApplicable(Hardware(new VrrState(false, Possible: false, false, false))));

    [Fact]
    public void ADisabledGsyncIsBroughtBackToFullscreenMode()
    {
        _store.Values[DrsIds.VrrMode] = DrsIds.VrrDisabled;
        var setting = new GsyncSetting();
        var hw = Hardware(Possible);

        var target = setting.ComputeTarget(hw);

        Assert.Equal(DrsIds.VrrFullscreenOnly, NvidiaDriverSetting.Effective(target.Value.Raw));
        Assert.False(setting.IsAtTarget(setting.ReadCurrent(hw), target.Value));
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public void AnActiveModeIsLeftAsItIs(uint stored)
    {
        _store.Values[DrsIds.VrrMode] = stored;
        var setting = new GsyncSetting();
        var hw = Hardware(Possible);

        Assert.True(setting.IsAtTarget(setting.ReadCurrent(hw), setting.ComputeTarget(hw).Value));
    }

    [Fact]
    public void ASettingNeverChosenIsAlreadyAtTarget()
    {
        var setting = new GsyncSetting();
        var hw = Hardware(Possible);

        Assert.True(setting.IsAtTarget(setting.ReadCurrent(hw), setting.ComputeTarget(hw).Value));
    }

    [Fact]
    public void ApplyingWritesTheModeAndRestoringPutsTheOldValueBack()
    {
        _store.Values[DrsIds.VrrMode] = DrsIds.VrrDisabled;
        var setting = new GsyncSetting();
        var hw = Hardware(Possible);
        var before = setting.ReadCurrent(hw);

        setting.Apply(hw, setting.ComputeTarget(hw).Value);
        Assert.Equal(DrsIds.VrrFullscreenOnly, _store.Values[DrsIds.VrrMode]);

        // La restauration applique l'ancienne valeur sauvegardée : la valeur 0 revient, et non « tout sauf 0 »
        setting.Apply(hw, before);
        Assert.Equal(DrsIds.VrrDisabled, _store.Values[DrsIds.VrrMode]);
        Assert.False(setting.IsAtTarget(setting.ReadCurrent(hw), setting.ComputeTarget(hw).Value));
    }

    [Fact]
    public void RestoringTheFactoryValueRemovesTheSettingFromTheProfile()
    {
        _store.Values[DrsIds.VrrMode] = DrsIds.VrrFullscreenAndWindowed;
        var setting = new GsyncSetting();
        var hw = Hardware(Possible);

        setting.Apply(hw, new SettingValue("default:0x00000001", "Plein écran seulement (par défaut)"));

        Assert.Contains(DrsIds.VrrMode, _store.Restored);
        Assert.False(_store.Values.ContainsKey(DrsIds.VrrMode));
    }

    [Fact]
    public void NotCheckedByDefaultBecauseTurningGsyncOffCanBeWanted() =>
        Assert.False(new GsyncSetting().SelectedByDefault);

    [Fact]
    public void GsyncComesFirstInTheNvidiaList()
    {
        var settings = NvidiaSettings.Build(Hardware(Possible)).ToList();

        Assert.Equal("nvidia.gsync", settings[0].Id);
        Assert.Equal("nvidia.lowlatency", settings[1].Id);
        Assert.Equal("G-SYNC", settings[0].DisplayName);
    }

    [Fact]
    public void NothingIsProposedWithoutAnNvidiaCard() =>
        Assert.Empty(NvidiaSettings.Build(new HardwareProfile()));

    [Fact]
    public void TheDriverValuesAreTheOnesPublishedByNvidia()
    {
        // NvApiDriverSettings.h du SDK NVAPI : VRR_MODE_ID et les trois valeurs de EValues_VRR_MODE
        Assert.Equal(0x1194F158u, DrsIds.VrrMode);
        Assert.Equal(0u, DrsIds.VrrDisabled);
        Assert.Equal(1u, DrsIds.VrrFullscreenOnly);
        Assert.Equal(2u, DrsIds.VrrFullscreenAndWindowed);
    }
}
