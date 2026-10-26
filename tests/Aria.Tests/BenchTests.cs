using Aria.Core.Bench;

namespace Aria.Tests;

public class BenchTests
{
    /// <summary>Journal d'Aria-Bench réduit aux champs lus par Aria Cockpit.</summary>
    private static string Log(string statut, int erreursCalcul = 0, string? plafond = null, params string[] erreurs) => $$"""
        {
          "statut": "{{statut}}",
          "resultats": [
            {
              "qualite": "Ultra", "definition": "1440p", "images": 25828,
              "fpsMoyen": 141.1, "fpsMedian": 141, "fps1PourcentBas": 131, "saccades": 0,
              "verifications": 61, "erreursCalcul": {{erreursCalcul}}, "pixelsDifferents": {{(erreursCalcul > 0 ? 14854 : 0)}},
              "plafond": {{(plafond is null ? "null" : $"\"{plafond}\"")}}
            }
          ],
          "remarques": [],
          "erreurs": [{{string.Join(", ", erreurs.Select(e => $"\"{e}\""))}}]
        }
        """;

    [Fact]
    public void CleanRunIsStable()
    {
        var result = BenchResult.Parse(Log("terminé"));

        Assert.True(result.Completed);
        Assert.False(result.Unstable);
        Assert.Equal(141.1, result.Pass!.FpsMoyen);
    }

    [Fact]
    public void CalculationErrorsMakeTheStepUnstable()
    {
        // Base de Performance sur la tour : 32 vérifications fausses sur 61, sans plantage
        var result = BenchResult.Parse(Log("terminé", erreursCalcul: 32));

        Assert.True(result.Completed);
        Assert.True(result.CalculationErrors);
        Assert.True(result.Unstable);
    }

    [Fact]
    public void LostDeviceIsADriverFailure()
    {
        var result = BenchResult.Parse(Log("erreur", erreurs: "DXGI_ERROR_DEVICE_REMOVED pendant le rendu"));

        Assert.True(result.Failed);
        Assert.True(result.DriverFailure);
        Assert.True(result.Unstable);
    }

    [Fact]
    public void InterruptionIsNeitherFailureNorInstability()
    {
        var result = BenchResult.Parse(Log("interrompu"));

        Assert.True(result.Interrupted);
        Assert.False(result.Failed);
        Assert.False(result.Unstable);
    }

    [Fact]
    public void FrameRateCapIsRead()
    {
        var result = BenchResult.Parse(Log("terminé", plafond: "FPS plafonnés à environ 141"));

        Assert.Equal("FPS plafonnés à environ 141", result.Pass!.Plafond);
    }

    [Theory]
    [InlineData(900, 1080)]
    [InlineData(1080, 1080)]
    [InlineData(1440, 1440)]
    [InlineData(1600, 1440)]
    [InlineData(2160, 2160)]
    public void BenchDefinitionFollowsTheScreenHeight(int screenHeight, int expected)
    {
        Assert.Equal(expected, BenchRunner.DefinitionFor(screenHeight));
    }

    [Fact]
    public void DefinitionGoesUpUntil4K()
    {
        Assert.Equal(1440, BenchRunner.NextDefinition(1080));
        Assert.Equal(2160, BenchRunner.NextDefinition(1440));
        Assert.Null(BenchRunner.NextDefinition(2160));
    }

    [Fact]
    public void ArgumentsRoundTheDurationUp()
    {
        var args = BenchRunner.Arguments("ultra", 2160, TimeSpan.FromSeconds(184.2), @"C:\temp\banc.json");

        Assert.Equal(["--qualite", "ultra", "--def", "2160", "--duree", "185", "--sortie", @"C:\temp\banc.json"], args);
    }
}
