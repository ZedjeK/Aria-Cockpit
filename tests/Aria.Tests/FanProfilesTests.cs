using Aria.Core.Nvidia;

namespace Aria.Tests;

/// <summary>Courbes de ventilation enregistrées : remplacées par nom, et une courbe abîmée n'emporte pas les autres.</summary>
public class FanProfilesTests
{
    private static SavedFanProfile Profile(string name, int first = 45) =>
        new(name, [new(first, 35), new(70, 70)], new DateTime(2026, 11, 1));

    [Fact]
    public void SameNameReplacesTheCurveWhateverTheCase()
    {
        var list = FanProfiles.Upsert([Profile("Nuit"), Profile("Jeu")], Profile(" nuit ", first: 50));

        Assert.Equal(2, list.Count);
        Assert.Equal(50, Assert.Single(list, p => p.Name.Trim() == "nuit").Points[0].TemperatureC);
    }

    [Fact]
    public void DeletingRemovesOnlyThatCurve()
    {
        var list = FanProfiles.Remove([Profile("Nuit"), Profile("Jeu")], "NUIT");

        Assert.Equal("Jeu", Assert.Single(list).Name);
    }

    [Fact]
    public void CurvesSurviveARoundTrip()
    {
        var saved = new List<SavedFanProfile> { Profile("Nuit"), Profile("Jeu", first: 50) };

        var loaded = FanProfiles.FromJson(FanProfiles.ToJson(saved));

        Assert.Equal(["Nuit", "Jeu"], loaded.Select(p => p.Name));
        Assert.Equal(saved[1].Points, loaded[1].Points);
    }

    [Fact]
    public void DamagedCurveIsDroppedButOthersKept()
    {
        const string json = """
            [
              { "Name": "Bonne", "Points": [ { "TemperatureC": 45, "Percent": 35 }, { "TemperatureC": 70, "Percent": 70 } ], "At": "2026-11-01T20:00:00" },
              { "Name": "Fausse", "Points": [ { "TemperatureC": 70, "Percent": 70 }, { "TemperatureC": 45, "Percent": 35 } ], "At": "2026-11-01T20:00:00" },
              { "Name": "  ", "Points": [ { "TemperatureC": 45, "Percent": 35 }, { "TemperatureC": 70, "Percent": 70 } ], "At": "2026-11-01T20:00:00" }
            ]
            """;

        Assert.Equal("Bonne", Assert.Single(FanProfiles.FromJson(json)).Name);
        Assert.Empty(FanProfiles.FromJson("pas du json"));
    }

    [Fact]
    public void SummaryListsEveryPoint()
    {
        Assert.Equal("45 °C 35 % · 70 °C 70 %", Profile("Nuit").Summary);
    }
}
