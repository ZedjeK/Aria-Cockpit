using Aria.Core.Diagnostics.Checks;
using Aria.Core.Models;
using Aria.Core.Nvidia;

namespace Aria.Tests;

public class GpuProfileTests
{
    private static readonly GpuTuningLimits Limits = new(150_000, 370_000, 320_000, 210, 3105, -1000, 1000, -2000, 3000);

    [Fact]
    public void OriginIsTheFactoryValuesNotWhatAnotherToolSet()
    {
        // NVIDIA App avait posé 272 W : l'origine reste la limite d'usine lue sur la carte
        Assert.Equal(new GpuTuningState(320_000, 0, 0, 0, false), GpuProfile.Factory(Limits, fallbackPowerMilliwatts: 272_000));
    }

    [Fact]
    public void OriginKeepsTheCurrentPowerWhenTheDriverGivesNoDefault()
    {
        var noDefault = Limits with { DefaultPowerMilliwatts = 0 };

        Assert.Equal(272_000u, GpuProfile.Factory(noDefault, 272_000).PowerLimitMilliwatts);
    }

    private static GpuProfileFile Profile(string current, DateTime appliedAt) =>
        new("p=320000;c=0;o=0;m=0;b=0", current, appliedAt, "GPU-test", "Carte de test");

    [Fact]
    public void TrialSettingAppliedBeforeTheLastBootIsGone()
    {
        var boot = new DateTime(2026, 10, 26, 8, 0, 0);

        Assert.True(GpuProfile.WipedByRestart(Profile("p=272000;c=2550;o=300;m=0;b=0", boot.AddHours(-9)), boot));
    }

    [Fact]
    public void SettingKeptAtStartupSurvivesARestart()
    {
        var boot = new DateTime(2026, 10, 26, 8, 0, 0);

        Assert.False(GpuProfile.WipedByRestart(Profile("p=272000;c=2550;o=300;m=0;b=1", boot.AddHours(-9)), boot));
    }

    [Fact]
    public void SettingAppliedSinceTheLastBootIsStillInPlace()
    {
        var boot = new DateTime(2026, 10, 26, 8, 0, 0);

        Assert.False(GpuProfile.WipedByRestart(Profile("p=272000;c=2550;o=300;m=0;b=0", boot.AddMinutes(5)), boot));
    }

    [Fact]
    public void UnreadableProfileIsNotTreatedAsWiped()
    {
        var boot = new DateTime(2026, 10, 26, 8, 0, 0);

        Assert.False(GpuProfile.WipedByRestart(Profile("illisible", boot.AddHours(-9)), boot));
    }

    [Fact]
    public void PowerLimitLoweredByAnotherToolIsReported()
    {
        var finding = GpuPowerLimitCheck.Describe(272_000, 320_000, setByAria: false);

        Assert.NotNull(finding);
        Assert.Equal(Severity.Info, finding!.Severity);
        Assert.Contains("272 W au lieu de 320 W (85 %)", finding.Detail);
        Assert.Contains("NVIDIA App", finding.Detail);
    }

    [Theory]
    [InlineData(320_000u, 320_000u)]
    [InlineData(316_000u, 320_000u)]
    [InlineData(272_000u, 0u)]
    public void FactoryPowerOrUnknownDefaultIsNotReported(uint current, uint factory)
    {
        Assert.Null(GpuPowerLimitCheck.Describe(current, factory, setByAria: false));
    }
}
