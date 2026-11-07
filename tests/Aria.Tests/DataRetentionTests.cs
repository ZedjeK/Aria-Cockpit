using Aria.Core.Backup;

namespace Aria.Tests;

/// <summary>Ménage du dossier des données (dossier temporaire, jamais le vrai). Repris de CTI-Cockpit.</summary>
public sealed class DataRetentionTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("aria-cockpit-menage-");
    private static readonly DateTime Now = new(2026, 11, 3, 10, 0, 0);

    public void Dispose() => _root.Delete(recursive: true);

    private string Reports => Path.Combine(_root.FullName, "Rapports");
    private string Backups => Path.Combine(_root.FullName, "Sauvegardes");

    private string Create(string folder, string name, int bytes = 10)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private string? Run(RetentionSettings? settings = null, DateTime? at = null) =>
        DataRetention.RunDaily(_root.FullName, settings ?? RetentionSettings.Default, at ?? Now);

    [Fact]
    public void ReportsOlderThanSixMonthsAreRemoved()
    {
        var old = Create(Reports, "rapport_2026-05-02_120000.txt");          // 6 mois et un jour
        var recent = Create(Reports, "mises-a-jour_2026-05-04_120000.txt");  // un peu moins de 6 mois
        var sheet = Create(Reports, "fiche-pc_2026-01-15_090000.html");
        var tuning = Create(Reports, "reglage-gpu_2026-03-10_220000.txt");
        var cleanup = Create(Reports, "nettoyage_2026-10-30_180000.txt");

        var summary = Run();

        Assert.False(File.Exists(old));
        Assert.False(File.Exists(sheet));
        Assert.False(File.Exists(tuning));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(cleanup));
        Assert.Contains("3 rapport(s)", summary);
    }

    [Fact]
    public void TenMostRecentBackupsAreAlwaysKept()
    {
        // 12 sauvegardes, toutes de plus d'un an : les 10 plus récentes restent quand même
        var paths = Enumerable.Range(1, 12)
            .Select(day => Create(Backups, $"sauvegarde_2025-01-{day:00}_100000.json"))
            .ToList();

        Run();

        Assert.False(File.Exists(paths[0]));    // 01/01
        Assert.False(File.Exists(paths[1]));    // 02/01
        Assert.All(paths.Skip(2), path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void RecentBackupsBeyondTheTenAreKept()
    {
        var paths = Enumerable.Range(1, 15)
            .Select(day => Create(Backups, $"sauvegarde_2026-10-{day:00}_100000.json"))
            .ToList();

        Assert.Null(Run());
        Assert.All(paths, path => Assert.True(File.Exists(path)));
    }

    [Fact]
    public void UnknownFilesAndCurrentSettingsAreNeverTouched()
    {
        var handmade = Create(Reports, "mon-rapport-a-moi.txt");
        var oddDate = Create(Reports, "rapport_ancien.txt");
        var otherKind = Create(Backups, "sauvegarde_2020-01-01_100000.bak");
        // Réglages en place : jamais concernés, quel que soit leur âge
        var settings = new[] { "gpu-profile.json", "equilibre.json", "profils.json", "ventilateur.json", "ventilateur-profils.json" }
            .Select(name => Create(_root.FullName, name))
            .ToList();
        foreach (var path in settings) File.SetLastWriteTime(path, new DateTime(2020, 1, 1));

        Run();

        Assert.True(File.Exists(handmade));
        Assert.True(File.Exists(oddDate));
        Assert.True(File.Exists(otherKind));
        Assert.All(settings, path => Assert.True(File.Exists(path)));
    }

    [Theory]
    [InlineData("erreurs.log", "erreurs-precedent.log")]
    [InlineData("gpu-profile.log", "gpu-profile-precedent.log")]
    public void LargeLogIsArchived(string log, string previous)
    {
        Create(_root.FullName, previous, 5);
        Create(_root.FullName, log, (int)DataRetention.MaxLogBytes + 1);

        var summary = Run();

        Assert.False(File.Exists(Path.Combine(_root.FullName, log)));
        Assert.Equal(DataRetention.MaxLogBytes + 1, new FileInfo(Path.Combine(_root.FullName, previous)).Length);
        Assert.Contains($"{log} archivé dans {previous}", summary);
    }

    [Fact]
    public void SmallLogsStayInPlace()
    {
        Create(_root.FullName, "erreurs.log", 1000);
        Create(_root.FullName, "gpu-profile.log", 1000);

        Assert.Null(Run());
        Assert.True(File.Exists(Path.Combine(_root.FullName, "erreurs.log")));
        Assert.True(File.Exists(Path.Combine(_root.FullName, "gpu-profile.log")));
    }

    [Fact]
    public void CleanupRunsOncePerDay()
    {
        Create(Reports, "rapport_2026-01-01_120000.txt");
        Assert.NotNull(Run());

        // Le même jour, un nouveau rapport ancien attend le lendemain
        var later = Create(Reports, "rapport_2026-01-02_120000.txt");
        Assert.Null(Run(at: Now.AddHours(5)));
        Assert.True(File.Exists(later));

        Assert.NotNull(Run(at: Now.AddDays(1)));
        Assert.False(File.Exists(later));
    }

    [Fact]
    public void MissingFolderIsLeftAlone()
    {
        var missing = Path.Combine(_root.FullName, "absent");

        Assert.Null(DataRetention.RunDaily(missing, RetentionSettings.Default, Now));
        Assert.False(Directory.Exists(missing));
    }
}
