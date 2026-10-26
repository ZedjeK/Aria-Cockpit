using Aria.Core.Nvidia;
using Microsoft.Data.Sqlite;

namespace Aria.Tests;

/// <summary>Lecture de la base de NVIDIA App sur des bases factices au même schéma (tables utiles seulement).</summary>
public sealed class NvidiaAppTuningTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"aria-tests-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private sealed record Result(long Time, int Stable, int AutoPersist, int Mhz);

    /// <summary>Base au schéma de nvtopps.db3 ; settings null = table vide.</summary>
    private string Database(int? backgroundScan = 0, int? autoOc = 0, params Result[] results)
    {
        var path = Path.Combine(_folder, $"{Guid.NewGuid():N}.db3");
        // Pooling=False : le fichier est libéré dès la fermeture, comme pour la vraie base que NVIDIA garde ouverte
        using var db = new SqliteConnection($"Data Source={path};Pooling=False");
        db.Open();
        Execute(db, "CREATE TABLE 'ocScannerSettings' ( 'gpuId' TEXT PRIMARY KEY NOT NULL , 'backgroundScanEnabled' INTEGER NOT NULL , 'grdOcEnabled' INTEGER NOT NULL )");
        Execute(db, "CREATE TABLE 'ocScannerResults' ( 'id' INTEGER PRIMARY KEY AUTOINCREMENT NOT NULL , 'gpuId' TEXT NOT NULL , 'type' INTEGER NOT NULL , " +
                    "'time' INTEGER NOT NULL , 'stable' INTEGER NOT NULL , 'telemetryReported' INTEGER DEFAULT (0) NOT NULL , 'autoPersist' INTEGER NOT NULL , " +
                    "'completionStatus' INTEGER NOT NULL , 'dominantLimiters' INTEGER NOT NULL , 'averageOverlockMhz' INTEGER NOT NULL , 'memoryOffsetMhz' INTEGER NOT NULL )");
        if (backgroundScan is { } scan && autoOc is { } oc)
            Execute(db, $"INSERT INTO ocScannerSettings VALUES ('00000000006050c0_00000005110001e1', {scan}, {oc})");
        foreach (var r in results)
            Execute(db, "INSERT INTO ocScannerResults (gpuId, type, time, stable, autoPersist, completionStatus, dominantLimiters, averageOverlockMhz, memoryOffsetMhz) " +
                        $"VALUES ('gpu', 0, {r.Time}, {r.Stable}, {r.AutoPersist}, 0, 0, {r.Mhz}, 0)");
        return path;
    }

    private static void Execute(SqliteConnection db, string sql)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void EverythingOffIsInactive()
    {
        var state = NvidiaAppTuning.Read(Database());

        Assert.True(state.Installed);
        Assert.False(state.Active);
        Assert.Null(state.Unreadable);
        Assert.Equal("Aucun overclock de NVIDIA App", state.Describe());
    }

    [Fact]
    public void EmptyTablesAreInactive()
    {
        Assert.False(NvidiaAppTuning.Read(Database(backgroundScan: null, autoOc: null)).Active);
    }

    [Fact]
    public void AutomaticOverclockIsDetected()
    {
        var state = NvidiaAppTuning.Read(Database(autoOc: 1));

        Assert.True(state.Active);
        Assert.True(state.AutoOc);
        Assert.False(state.BackgroundScan);
    }

    [Fact]
    public void BackgroundScanIsDetected()
    {
        var state = NvidiaAppTuning.Read(Database(backgroundScan: 1));

        Assert.True(state.Active);
        Assert.True(state.BackgroundScan);
    }

    [Fact]
    public void LatestSavedStableCurveIsReported()
    {
        var state = NvidiaAppTuning.Read(Database(results:
        [
            new(Time: 1, Stable: 1, AutoPersist: 1, Mhz: 60),
            new(Time: 3, Stable: 1, AutoPersist: 1, Mhz: 95),
            new(Time: 2, Stable: 1, AutoPersist: 1, Mhz: 80)
        ]));

        Assert.True(state.Active);
        Assert.Equal(95, state.SavedCurveMHz);
        Assert.Equal("Courbe enregistrée par le scan (+95 MHz en moyenne)", state.Describe());
    }

    [Fact]
    public void UnstableOrUnsavedCurvesAreIgnored()
    {
        var state = NvidiaAppTuning.Read(Database(results:
        [
            new(Time: 1, Stable: 0, AutoPersist: 1, Mhz: 120),
            new(Time: 2, Stable: 1, AutoPersist: 0, Mhz: 90)
        ]));

        Assert.False(state.Active);
        Assert.Null(state.SavedCurveMHz);
    }

    [Fact]
    public void MissingDatabaseMeansNotInstalled()
    {
        Assert.Equal(NvidiaAppTuningState.NotInstalled, NvidiaAppTuning.Read(Path.Combine(_folder, "absente.db3")));
    }

    [Fact]
    public void CorruptDatabaseIsReportedWithoutBlocking()
    {
        var path = Path.Combine(_folder, "corrompue.db3");
        File.WriteAllText(path, "pas une base");

        var state = NvidiaAppTuning.Read(path);

        Assert.False(state.Active);
        Assert.NotNull(state.Unreadable);
    }

    [Fact]
    public void ChangedFormatIsReportedWithoutBlocking()
    {
        var path = Path.Combine(_folder, "autre-format.db3");
        using (var db = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            db.Open();
            Execute(db, "CREATE TABLE autreChose (x INTEGER)");
        }

        var state = NvidiaAppTuning.Read(path);

        Assert.False(state.Active);
        Assert.Contains("ocScannerSettings", state.Unreadable);
    }

    [Fact]
    public void ReadingLeavesNoTemporaryCopyAndNoLockOnTheSource()
    {
        var path = Database(autoOc: 1);
        int Copies() => Directory.GetFiles(Path.GetTempPath(), "aria-nvtopps-*.db3").Length;
        var before = Copies();

        NvidiaAppTuning.Read(path);

        Assert.Equal(before, Copies());
        File.Delete(path);   // échouerait si un descripteur restait ouvert
    }
}
