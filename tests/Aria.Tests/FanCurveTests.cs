using Aria.Core.Nvidia;

namespace Aria.Tests;

/// <summary>Courbe de ventilation : interpolation, sécurités, va-et-vient évité, ventilateurs toujours rendus au pilote.</summary>
public class FanCurveTests
{
    private static readonly FanCurve Curve = new("Essai", [new(50, 40), new(70, 60), new(80, 90)]);

    private static FanCurveController Controller(uint min = 30, uint max = 100) => new(Curve, min, max);

    [Fact]
    public void PresetsAreValidAndGetLouder()
    {
        Assert.All(FanCurve.Presets, c => Assert.Null(c.Check()));
        Assert.True(FanCurve.Silent.SpeedAt(70) < FanCurve.Balanced.SpeedAt(70));
        Assert.True(FanCurve.Balanced.SpeedAt(70) < FanCurve.Cooling.SpeedAt(70));
    }

    [Theory]
    [InlineData(new[] { 50 }, new[] { 40 })]                    // un seul point
    [InlineData(new[] { 50, 50 }, new[] { 40, 60 })]            // températures égales
    [InlineData(new[] { 50, 70 }, new[] { 60, 40 })]            // vitesse qui baisse
    [InlineData(new[] { 10, 70 }, new[] { 40, 60 })]            // trop froid
    [InlineData(new[] { 50, 99 }, new[] { 40, 60 })]            // trop chaud
    [InlineData(new[] { 50, 70 }, new[] { 40, 120 })]           // plus de 100 %
    public void InvalidCurvesAreRejected(int[] temperatures, int[] percents)
    {
        var curve = new FanCurve("Faux", temperatures.Zip(percents, (t, p) => new FanCurvePoint(t, p)).ToList());
        Assert.NotNull(curve.Check());
        Assert.Throws<ArgumentException>(() => new FanCurveController(curve, 30, 100));
    }

    [Theory]
    [InlineData(40, 40)]
    [InlineData(50, 40)]
    [InlineData(60, 50)]
    [InlineData(75, 75)]
    [InlineData(71, 63)]    // 60 + 30 × 0,1 = 63
    [InlineData(95, 90)]
    public void SpeedIsInterpolatedBetweenPoints(double temperature, int expected)
    {
        Assert.Equal(expected, Curve.SpeedAt(temperature));
    }

    [Fact]
    public void DriverKeepsControlBelowTheFirstPoint()
    {
        var c = Controller();
        Assert.Null(c.Next(35));
        Assert.Null(c.Next(49));
        Assert.Equal(FanMode.Auto, c.Mode);
    }

    [Fact]
    public void CurveTakesOverAtTheFirstPointAndRespectsTheCardMinimum()
    {
        var c = Controller(min: 45);
        Assert.Equal(FanCommand.Manual(45), c.Next(50));    // 40 % demandés, 45 % au minimum pour la carte
        Assert.Equal(FanCommand.Manual(50), c.Next(60));
        Assert.Null(c.Next(60));                            // rien ne change : rien n'est envoyé
    }

    [Fact]
    public void FullSpeedAboveTheSafetyThresholdWhateverTheCurve()
    {
        var c = new FanCurveController(FanCurve.Silent, 30, 95);
        Assert.Equal(FanCommand.Manual(95), c.Next(FanCurveController.FullSpeedC));
        Assert.Null(c.Next(90));
    }

    [Fact]
    public void UnreadableTemperatureHandsControlBackToTheDriver()
    {
        var c = Controller();
        c.Next(70);
        Assert.Equal(FanCommand.Auto, c.Next(null));
        Assert.Equal(FanMode.Auto, c.Mode);
        Assert.Null(c.Next(null));
    }

    [Fact]
    public void SpeedRisesAtOnceButFallsSlowly()
    {
        var c = Controller();
        c.Next(60);                                          // 50 %
        Assert.Equal(FanCommand.Manual(75), c.Next(75));     // montée immédiate
        Assert.Null(c.Next(73));                             // 2 °C de moins : dans l'hystérésis
        Assert.Equal(FanCommand.Manual(73), c.Next(65));     // descente de 2 % par relevé
        Assert.Equal(FanCommand.Manual(71), c.Next(65));
    }

    [Fact]
    public void DriverTakesBackControlOnlyWellBelowTheFirstPoint()
    {
        var c = Controller();
        c.Next(55);
        Assert.NotEqual(FanCommand.Auto, c.Next(48));        // 2 °C sous le seuil : la courbe garde la main
        Assert.Equal(FanCommand.Auto, c.Next(46));           // 4 °C sous le seuil : le pilote reprend
        Assert.Equal(0u, c.Percent);
    }

    [Fact]
    public async Task RunnerStartsAndStopsInDriverMode()
    {
        var fans = new FakeFans { Temperature = 70 };
        var runner = new FanCurveRunner(fans, Controller());

        runner.Start();
        Assert.Equal(1, fans.AutoCalls);                     // état connu au démarrage
        await runner.StopAsync();
        Assert.Equal(2, fans.AutoCalls);
        Assert.False(runner.Running);
    }

    [Fact]
    public void RunnerAppliesTheCurveEachTick()
    {
        var fans = new FakeFans { Temperature = 60 };
        var runner = new FanCurveRunner(fans, Controller());
        FanStatus? last = null;
        runner.Updated += s => last = s;

        Assert.True(runner.Tick());
        Assert.Equal(50u, fans.Speed);
        Assert.Equal(new FanStatus(60, FanMode.Manual, 50), last);

        fans.Temperature = 40;
        Assert.True(runner.Tick());
        Assert.Equal(1, fans.AutoCalls);
    }

    [Fact]
    public void DriverErrorStopsTheCurveAndRestoresAuto()
    {
        var fans = new FakeFans { Temperature = 70, FailOnSet = true };
        var runner = new FanCurveRunner(fans, Controller());
        string? error = null;
        runner.Failed += e => error = e;

        Assert.False(runner.Tick());
        Assert.Equal("refusé", error);
        Assert.Equal(1, fans.AutoCalls);
    }

    private sealed class FakeFans : IFanDevice
    {
        public uint? Temperature { get; set; }
        public uint? Speed { get; private set; }
        public int AutoCalls { get; private set; }
        public bool FailOnSet { get; init; }

        public uint? ReadTemperature() => Temperature;

        public void SetSpeed(uint percent)
        {
            if (FailOnSet) throw new GpuTuningException("refusé");
            Speed = percent;
        }

        public void RestoreAuto()
        {
            AutoCalls++;
            Speed = null;
        }
    }
}
