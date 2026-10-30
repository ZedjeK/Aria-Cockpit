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

    /// <summary>Passe de 15 minutes en Ultra 4K, avec le temps passé hors du premier plan et l'éventuel plafond.</summary>
    private static BenchResult Timed(double arrierePlanS, string? plafond = null) => BenchResult.Parse($$"""
        {
          "statut": "terminé",
          "resultats": [
            {
              "qualite": "Ultra", "definition": "4K (2160p)", "images": 26985, "secondes": 889.6,
              "fpsMoyen": 30.3, "fpsMedian": 30, "fps1PourcentBas": 29.2, "saccades": 1,
              "verifications": 301, "erreursCalcul": 0, "pixelsDifferents": 0,
              "plafond": {{(plafond is null ? "null" : $"\"{plafond}\"")}}, "arrierePlanS": {{arrierePlanS.ToString(System.Globalization.CultureInfo.InvariantCulture)}}
            }
          ],
          "remarques": [],
          "erreurs": []
        }
        """);

    [Fact]
    public void BackgroundSecondsAreRead()
    {
        Assert.Equal(889.6, Timed(42.5).Pass!.Secondes);
        Assert.Equal(42.5, Timed(42.5).Pass!.ArrierePlanS);
        // Journal d'avant le garde-fou : rien n'indique d'arrière-plan
        Assert.Equal(0, BenchResult.Parse(Log("terminé")).Pass!.ArrierePlanS);
    }

    [Fact]
    public void ABenchLeftInTheBackgroundIsNotAMeasure()
    {
        // Une notification de quelques secondes passe ; au-delà de 2 % des 15 minutes (≈ 18 s), la mesure est faussée
        Assert.False(Timed(4).RanInBackground);
        Assert.False(Timed(17).RanInBackground);
        Assert.True(Timed(19).RanInBackground);
        Assert.True(Timed(889.6).RanInBackground);
    }

    [Fact]
    public void CappedFpsAtLowLoadMeanTheCardWasNotMeasured()
    {
        const string cap = "FPS plafonnés à environ 30 : 30 (limite d'images ou économie d'énergie).";

        // Tour, 30/10 : 30 FPS en 4K Ultra, 74 % de charge en moyenne
        Assert.True(Timed(0, cap).CappedBelowFullLoad(74.3));
        // Petite carte qui fait vraiment 30 FPS en 4K : pleine charge, la mesure vaut
        Assert.False(Timed(0, cap).CappedBelowFullLoad(98));
        Assert.False(Timed(0).CappedBelowFullLoad(74.3));
    }
}
