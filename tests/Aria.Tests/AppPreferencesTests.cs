using Aria.Core.Backup;
using Aria.Core.Settings;
using Aria.Core.Tray;

namespace Aria.Tests;

/// <summary>Paramètres de l'appli : fichier, bornes, comportement de la croix, aperçu du ménage.</summary>
public sealed class AppPreferencesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aria-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // ---- Fond animé ----

    [Fact]
    public void TheBackgroundIsOnByDefaultWithTwoToFivePoints()
    {
        var background = AppPreferences.Default.Background;

        Assert.True(background.Active);
        Assert.Equal(BackgroundSpeed.Normal, background.Speed);
        Assert.Equal(2, background.Minimum);
        Assert.Equal(5, background.Maximum);
    }

    [Fact]
    public void TheFileNamesTheBackgroundAsTheAriaContractDoes()
    {
        var json = AppPreferencesStore.ToJson(new AppPreferences { Background = new BackgroundSettings(false, BackgroundSpeed.Fast, 3, 8) });

        Assert.Contains("\"fond\"", json);
        Assert.Contains("\"actif\": false", json);
        Assert.Contains("\"vitesse\": \"rapide\"", json);
        Assert.Contains("\"minimum\": 3", json);
        Assert.Contains("\"maximum\": 8", json);
    }

    [Fact]
    public void TheBackgroundSurvivesTheFile()
    {
        var saved = new AppPreferences { Background = new BackgroundSettings(false, BackgroundSpeed.Slow, 4, 9) };

        Assert.Equal(saved, AppPreferencesStore.FromJson(AppPreferencesStore.ToJson(saved)));
    }

    [Fact]
    public void AFileFromBeforeTheBackgroundGetsTheDefaultOne() =>
        Assert.Equal(BackgroundSettings.Default, AppPreferencesStore.FromJson("{ \"NotifyOnHide\": false }").Background);

    [Theory]
    [InlineData("{ \"fond\": null }")]
    [InlineData("{ \"fond\": { \"vitesse\": \"éclair\" } }")]
    [InlineData("{ \"fond\": { \"vitesse\": 3 } }")]
    public void AnUnreadableBackgroundFallsBackToTheDefaults(string json) =>
        Assert.Equal(BackgroundSettings.Default, AppPreferencesStore.FromJson(json).Background);

    [Fact]
    public void BackgroundBoundsAreClampedAndPutBackInOrder()
    {
        var loaded = AppPreferencesStore.FromJson("{ \"fond\": { \"minimum\": 40, \"maximum\": -2 } }").Background;

        Assert.Equal(BackgroundSettings.MinPoints, loaded.Minimum);
        Assert.Equal(BackgroundSettings.MaxPoints, loaded.Maximum);
    }

    [Fact]
    public void AFasterBackgroundMakesAShorterPass() =>
        Assert.True(new BackgroundSettings(Speed: BackgroundSpeed.Fast).PassDuration < new BackgroundSettings(Speed: BackgroundSpeed.Slow).PassDuration);
    // ---- Fichier ----

    [Fact]
    public void DefaultsAreTheBehaviourFromBeforeThePage()
    {
        var defaults = AppPreferences.Default;

        Assert.Equal(CloseBehavior.Auto, defaults.Close);
        Assert.True(defaults.NotifyOnHide);
        Assert.Equal(RetentionSettings.Default, defaults.Retention);
    }

    [Fact]
    public void PreferencesSurviveTheFile()
    {
        var saved = new AppPreferences(CloseBehavior.AlwaysTray, NotifyOnHide: false, ReportsMonths: 3, BackupsMonths: 24, KeepBackups: 5);

        Assert.Equal(saved, AppPreferencesStore.FromJson(AppPreferencesStore.ToJson(saved)));
    }

    [Fact]
    public void TheFileNamesTheCloseBehaviourInWords() =>
        Assert.Contains("\"AlwaysQuit\"", AppPreferencesStore.ToJson(new AppPreferences(CloseBehavior.AlwaysQuit)));

    [Theory]
    [InlineData("")]
    [InlineData("pas du json")]
    [InlineData("{ \"Close\": \"Inconnu\" }")]
    public void UnreadableFileGivesTheDefaults(string json) => Assert.Equal(AppPreferences.Default, AppPreferencesStore.FromJson(json));

    [Fact]
    public void MissingFieldsKeepTheirDefault()
    {
        var loaded = AppPreferencesStore.FromJson("{ \"NotifyOnHide\": false }");

        Assert.False(loaded.NotifyOnHide);
        Assert.Equal(6, loaded.ReportsMonths);
    }

    [Fact]
    public void HandEditedDurationsAreBroughtBackInRange()
    {
        var loaded = AppPreferencesStore.FromJson("{ \"ReportsMonths\": 0, \"BackupsMonths\": 9999, \"KeepBackups\": -3 }");

        Assert.Equal(AppPreferences.MinMonths, loaded.ReportsMonths);
        Assert.Equal(AppPreferences.MaxMonths, loaded.BackupsMonths);
        Assert.Equal(AppPreferences.MinKeep, loaded.KeepBackups);
    }

    // ---- Croix de la fenêtre ----

    private static TrayState State(GpuProfileStatus profile = GpuProfileStatus.Factory, string? curve = null, params string[] work) =>
        new(profile, null, curve, null, null, work);

    [Fact]
    public void AutoHidesOnlyWhenSomethingIsActive()
    {
        Assert.Equal(CloseAction.Quit, State().OnClose(CloseBehavior.Auto));
        Assert.Equal(CloseAction.Hide, State(GpuProfileStatus.Applied).OnClose(CloseBehavior.Auto));
        Assert.Equal(CloseAction.Hide, State(curve: "Ma config").OnClose(CloseBehavior.Auto));
    }

    [Fact]
    public void AlwaysTrayHidesEvenWithNothingActive() => Assert.Equal(CloseAction.Hide, State().OnClose(CloseBehavior.AlwaysTray));

    [Fact]
    public void AlwaysQuitStopsEvenWithACurveRunning() =>
        Assert.Equal(CloseAction.Quit, State(GpuProfileStatus.Applied, "Ma config").OnClose(CloseBehavior.AlwaysQuit));

    [Fact]
    public void AlwaysQuitAsksBeforeCuttingAnOperation() =>
        Assert.Equal(CloseAction.AskThenQuit, State(work: "nettoyage").OnClose(CloseBehavior.AlwaysQuit));

    // ---- Aperçu du ménage et taille du dossier ----

    private void Touch(string folder, string name, int bytes = 10)
    {
        Directory.CreateDirectory(Path.Combine(_root, folder));
        File.WriteAllBytes(Path.Combine(_root, folder, name), new byte[bytes]);
    }

    [Fact]
    public void PreviewCountsWhatShorterDurationsWouldDeleteWithoutDeleting()
    {
        var now = new DateTime(2026, 11, 12);
        Touch("Rapports", "rapport_2026-10-01_120000.txt");
        Touch("Rapports", "rapport_2026-03-01_120000.txt");
        Touch("Sauvegardes", "sauvegarde_2026-10-01_120000.json");
        Touch("Sauvegardes", "sauvegarde_2025-01-01_120000.json");

        Assert.True(DataRetention.Preview(_root, RetentionSettings.Default, now).Reports == 1);
        var strict = DataRetention.Preview(_root, new RetentionSettings(ReportsMonths: 1, BackupsMonths: 1, KeepBackups: 1), now);

        Assert.Equal(new RetentionPreview(Reports: 2, Backups: 1), strict);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "Rapports")).Length);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(_root, "Sauvegardes")).Length);
    }

    [Fact]
    public void RunDeletesExactlyWhatThePreviewAnnounced()
    {
        var now = new DateTime(2026, 11, 12);
        Touch("Rapports", "rapport_2026-10-01_120000.txt");
        Touch("Rapports", "rapport_2026-03-01_120000.txt");
        var settings = new RetentionSettings(ReportsMonths: 3, BackupsMonths: 12, KeepBackups: 10);
        var announced = DataRetention.Preview(_root, settings, now);

        DataRetention.Run(_root, settings, now);

        Assert.Equal(1, announced.Reports);
        Assert.Equal(["rapport_2026-10-01_120000.txt"], Directory.GetFiles(Path.Combine(_root, "Rapports")).Select(Path.GetFileName));
    }

    [Fact]
    public void RunIgnoresTheDailyMarker()
    {
        var now = new DateTime(2026, 11, 12);
        Touch("Rapports", "rapport_2020-01-01_120000.txt");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, DataRetention.LastRunName), "2026-11-12");

        Assert.Null(DataRetention.RunDaily(_root, RetentionSettings.Default, now));
        Assert.NotNull(DataRetention.Run(_root, RetentionSettings.Default, now));
    }

    [Fact]
    public void MeasureGivesSizeAndCounts()
    {
        Touch("Rapports", "rapport_2026-10-01_120000.txt", 100);
        Touch("Rapports", "notes-perso.txt", 50);
        Touch("Sauvegardes", "sauvegarde_2026-10-01_120000.json", 25);

        Assert.Equal(new DataFolderInfo(Bytes: 175, Reports: 1, Backups: 1), DataRetention.Measure(_root));
    }

    [Fact]
    public void MeasureOfAMissingFolderIsEmpty() => Assert.Equal(new DataFolderInfo(0, 0, 0), DataRetention.Measure(_root));
}
