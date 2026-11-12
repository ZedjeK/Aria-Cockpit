using Aria.Core.Nvidia;

namespace Aria.Tests;

/// <summary>Repère « EN PLACE » de « Mes courbes » : la courbe enregistrée qui tourne, et elle seule.</summary>
public class FanProfileInPlaceTests
{
    private static readonly IReadOnlyList<FanCurvePoint> Points = [new(45, 60), new(60, 80), new(70, 100)];
    private static readonly SavedFanProfile Saved = new("Ma config", Points, new DateTime(2026, 11, 1));

    [Fact]
    public void SavedCurveThatRunsIsInPlace() =>
        Assert.True(Saved.IsRunning(new FanCurve("Ma config", [new(45, 60), new(60, 80), new(70, 100)])));

    [Fact]
    public void NameIsComparedWithoutCaseNorSpaces() => Assert.True(Saved.IsRunning(new FanCurve(" ma CONFIG ", Points)));

    [Fact]
    public void NothingIsInPlaceWhenTheDriverHasTheFans() => Assert.False(Saved.IsRunning(null));

    [Fact]
    public void AnotherCurveIsNotThisOne() => Assert.False(Saved.IsRunning(new FanCurve("Silencieux", Points)));

    [Fact]
    public void SameNameWithAChangedPointIsNoLongerThisCurve()
    {
        // La courbe a été retouchée dans les points puis appliquée sous le même nom, sans être réenregistrée
        var edited = new FanCurve("Ma config", [new(45, 60), new(60, 85), new(70, 100)]);

        Assert.False(Saved.IsRunning(edited));
    }
}
