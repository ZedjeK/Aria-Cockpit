using Aria.Core.Nvidia;
using Aria.Core.Tray;

namespace Aria.Tests;

public class TrayStateTests
{
    private static readonly GpuTuningState Factory = new(320_000, 0, 0, 0, false);
    private static readonly GpuTuningState Tuned = new(320_000, 2685, 145, 750, true);

    private static TrayState State(GpuProfileStatus profile = GpuProfileStatus.Factory, string? curve = null, params string[] work) =>
        new(profile, null, curve, 52, 60, work);

    // ---- État du réglage GPU, d'après la carte ----

    [Fact]
    public void SavedTuningFoundOnTheCardIsApplied()
    {
        // Le plafond n'est pas relisible : la carte le reprend du réglage enregistré
        var card = new GpuTuningState(320_000, 2685, 145, 750, false);

        Assert.Equal(GpuProfileStatus.Applied, TrayState.ProfileStatus(card, Factory, Tuned));
    }

    [Fact]
    public void SavedTuningMissingFromTheCardIsLost()
    {
        // Pilote rechargé : les décalages sont revenus à zéro
        var card = new GpuTuningState(320_000, 2685, 0, 0, false);

        Assert.Equal(GpuProfileStatus.Lost, TrayState.ProfileStatus(card, Factory, Tuned));
    }

    [Fact]
    public void FactoryCardWithoutSavedTuningIsFactory() =>
        Assert.Equal(GpuProfileStatus.Factory, TrayState.ProfileStatus(Factory, Factory, saved: null));

    [Fact]
    public void TunedCardWithoutSavedTuningBelongsToAnotherTool()
    {
        var card = new GpuTuningState(272_000, 0, 0, 0, false);

        Assert.Equal(GpuProfileStatus.Foreign, TrayState.ProfileStatus(card, Factory, saved: null));
    }

    [Fact]
    public void SavedTuningEqualToFactoryDoesNotCountAsAProfile()
    {
        // Valeurs d'usine gardées au démarrage : il n'y a rien à signaler
        var saved = Factory with { AtStartup = true };

        Assert.Equal(GpuProfileStatus.Factory, TrayState.ProfileStatus(Factory, Factory, saved));
    }

    // ---- Règle de fermeture ----

    [Fact]
    public void NothingActiveLetsTheWindowQuit()
    {
        var state = State();

        Assert.False(state.KeepsRunning);
        Assert.Null(state.KeepReason());
    }

    [Theory]
    [InlineData(GpuProfileStatus.Applied, true)]
    [InlineData(GpuProfileStatus.Lost, true)]
    [InlineData(GpuProfileStatus.Factory, false)]
    [InlineData(GpuProfileStatus.Foreign, false)]
    [InlineData(GpuProfileStatus.Unknown, false)]
    public void OnlyAnAriaProfileKeepsAriaRunning(GpuProfileStatus profile, bool keeps) =>
        Assert.Equal(keeps, State(profile).KeepsRunning);

    [Fact]
    public void RunningFanCurveKeepsAriaRunning() => Assert.True(State(curve: "Ma config").KeepsRunning);

    [Fact]
    public void WorkInProgressKeepsAriaRunning() => Assert.True(State(work: "nettoyage").KeepsRunning);

    [Fact]
    public void KeepReasonNamesEverythingActive()
    {
        var state = State(GpuProfileStatus.Applied, "Ma config", "nettoyage");

        Assert.Equal("Ton profil GPU est appliqué, la courbe « Ma config » pilote les ventilateurs, nettoyage en cours.", state.KeepReason());
    }

    // ---- Courbes proposées dans le menu ----

    private static readonly IReadOnlyList<FanCurvePoint> Points = [new(45, 60), new(70, 100)];

    [Fact]
    public void MenuOffersPresetsThenSavedCurves()
    {
        var curves = TrayChoices.FanCurves([new SavedFanProfile("Ma config", Points, DateTime.Now)], last: null);

        Assert.Equal(["Silencieux", "Équilibré", "Refroidissement max", "Ma config"], curves.Select(c => c.Name));
    }

    [Fact]
    public void LastCurveAlreadySavedIsListedOnce()
    {
        var curves = TrayChoices.FanCurves([new SavedFanProfile("Ma config", Points, DateTime.Now)],
            new FanCurveChoice("ma config", Points, Active: false));

        Assert.Single(curves, c => TrayChoices.SameName(c.Name, "Ma config"));
    }

    [Fact]
    public void UnsavedCustomCurveIsOffered()
    {
        var curves = TrayChoices.FanCurves([], new FanCurveChoice("Personnalisée", Points, Active: false));

        Assert.Equal("Personnalisée", curves[^1].Name);
        Assert.Equal(Points, curves[^1].Points);
    }

    [Fact]
    public void LastPresetIsNotListedTwice()
    {
        var curves = TrayChoices.FanCurves([], new FanCurveChoice("Silencieux", FanCurve.Silent.Points, Active: true));

        Assert.Equal(FanCurve.Presets.Count, curves.Count);
    }

    // ---- Icône, infobulle, menu ----

    [Theory]
    [InlineData(GpuProfileStatus.Factory, null, TrayIconKind.Idle)]
    [InlineData(GpuProfileStatus.Applied, null, TrayIconKind.Active)]
    [InlineData(GpuProfileStatus.Factory, "Ma config", TrayIconKind.Active)]
    [InlineData(GpuProfileStatus.Lost, "Ma config", TrayIconKind.Alert)]
    public void IconFollowsTheState(GpuProfileStatus profile, string? curve, TrayIconKind icon) =>
        Assert.Equal(icon, State(profile, curve).Icon);

    [Fact]
    public void SummaryPutsTheProfileFirst() =>
        Assert.Equal("Aria Cockpit · profil GPU appliqué · courbe « Ma config » · 52 °C · ventilateurs 60 %",
            State(GpuProfileStatus.Applied, "Ma config").Summary());

    [Fact]
    public void SummaryWithNothingReadableSaysSo() => Assert.Equal("Aria Cockpit · rien d'actif", TrayState.Empty.Summary());

    [Fact]
    public void SummaryFitsInATooltip()
    {
        var state = State(GpuProfileStatus.Applied, new string('x', 200));

        Assert.Equal(TrayState.MaxTooltip, state.Summary().Length);
        Assert.EndsWith("…", state.Summary());
    }

    [Fact]
    public void TemperatureAloneDoesNotRebuildTheMenu()
    {
        var before = State(GpuProfileStatus.Applied, "Ma config");

        Assert.Equal(before.Signature, (before with { TemperatureC = 71, FanPercent = 100 }).Signature);
        Assert.NotEqual(before.Signature, (before with { FanCurve = null }).Signature);
    }
}
