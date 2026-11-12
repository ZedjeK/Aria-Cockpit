using Aria.Core.Backup;

namespace Aria.Tests;

/// <summary>Dossier « cache » : créé à la demande, vidé de ce qui a plus d'une semaine, sans jamais lever.</summary>
public sealed class DataCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aria-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Cache => Path.Combine(_root, DataCache.FolderName);

    private string File(string name, DateTime written)
    {
        var path = DataCache.PathFor(Cache, name);
        System.IO.File.WriteAllText(path, "x");
        System.IO.File.SetLastWriteTime(path, written);
        return path;
    }

    [Fact]
    public void TheFolderIsCalledCacheAsTheAriaContractAsks() => Assert.Equal("cache", DataCache.FolderName);

    [Fact]
    public void AskingForAPathCreatesTheFolder()
    {
        var path = DataCache.PathFor(Cache, "banc.json");

        Assert.True(Directory.Exists(Cache));
        Assert.Equal(Path.Combine(Cache, "banc.json"), path);
        Assert.False(System.IO.File.Exists(path));
    }

    [Fact]
    public void TheSweepRemovesOnlyWhatIsOlderThanTheLimit()
    {
        var now = new DateTime(2026, 11, 12, 12, 0, 0);
        var old = File("vieux.json", now.AddDays(-8));
        var recent = File("recent.json", now.AddDays(-6));

        Assert.Equal(1, DataCache.Sweep(Cache, DataCache.MaxAge, now));
        Assert.False(System.IO.File.Exists(old));
        Assert.True(System.IO.File.Exists(recent));
    }

    [Fact]
    public void TheSweepNeverLeavesTheCacheFolder()
    {
        var now = new DateTime(2026, 11, 12, 12, 0, 0);
        Directory.CreateDirectory(_root);
        var outside = Path.Combine(_root, "reglages.json");
        System.IO.File.WriteAllText(outside, "{}");
        System.IO.File.SetLastWriteTime(outside, now.AddYears(-1));
        File("vieux.json", now.AddDays(-30));

        DataCache.Sweep(Cache, DataCache.MaxAge, now);

        Assert.True(System.IO.File.Exists(outside));
    }

    [Fact]
    public void SweepingAMissingFolderDoesNothing() =>
        Assert.Equal(0, DataCache.Sweep(Cache, DataCache.MaxAge, DateTime.Now));
}