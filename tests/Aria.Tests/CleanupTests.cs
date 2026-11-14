using Aria.Core.Cleanup;
using Aria.Core.Models;
using Aria.Core.Settings;
using Aria.Core.Windows;

namespace Aria.Tests;

/// <summary>
/// Nettoyage : âge d'un fichier (création et modification), caches de shaders anciens, rapports de plantage du pilote
/// graphique, garde-fous de la suppression, et réglage de la veille prolongée. Tout se joue dans un dossier d'essai.
/// </summary>
public sealed class CleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aria-tests", "nettoyage-" + Guid.NewGuid().ToString("N"));

    public CleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Un fichier de <paramref name="bytes"/> octets, créé et modifié il y a le nombre de jours donné.</summary>
    private static string File(string folder, string name, int createdDaysAgo, int writtenDaysAgo, int bytes = 100)
    {
        var path = Path.Combine(folder, name);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        System.IO.File.SetCreationTime(path, DateTime.Now.AddDays(-createdDaysAgo));
        System.IO.File.SetLastWriteTime(path, DateTime.Now.AddDays(-writtenDaysAgo));
        return path;
    }

    private CleanupPaths Paths(string? shaders = null, string? dumps = null) => new()
    {
        Temp = [Folder("Temp")],
        ShaderCaches = [shaders ?? Folder("Shaders")],
        CrashDumpFolders = [dumps ?? Folder("Dumps")],
        CrashDumpFiles = [],
        DeliveryOptimizationCache = Path.Combine(_root, "absent"),
        PreviousWindows = Path.Combine(_root, "Windows.old"),
        SystemDrive = Path.GetPathRoot(_root)!
    };

    private static CleanupItem Item(IEnumerable<CleanupItem> items, CleanupKind kind) => items.Single(i => i.Kind == kind);

    // ---------------------------------------------------------------- Âge d'un fichier

    [Fact]
    public void AFileExtractedTodayFromAnArchiveIsNotOld()
    {
        // Sorti aujourd'hui d'un installeur : modifié « il y a trois ans », mais arrivé sur ce disque à l'instant
        var temp = Folder("Temp");
        var extracted = File(temp, "setup.dll", createdDaysAgo: 0, writtenDaysAgo: 1100);
        var old = File(temp, "vieux.tmp", createdDaysAgo: 40, writtenDaysAgo: 40);

        var tally = SafeDelete.Delete(temp, DateTime.Now.AddDays(-7));

        Assert.Equal(1, tally.Files);
        Assert.True(System.IO.File.Exists(extracted));
        Assert.False(System.IO.File.Exists(old));
    }

    [Fact]
    public void AFileCreatedLongAgoButWrittenRecentlyIsNotOld()
    {
        var temp = Folder("Temp");
        var journal = File(temp, "journal.log", createdDaysAgo: 400, writtenDaysAgo: 1);

        Assert.Equal(0, SafeDelete.Delete(temp, DateTime.Now.AddDays(-7)).Files);
        Assert.True(System.IO.File.Exists(journal));
    }

    [Fact]
    public void WithoutAnAgeLimitEverythingCounts()
    {
        var temp = Folder("Temp");
        File(temp, "a", 0, 0);
        File(temp, "b", 90, 90);

        Assert.Equal(2, SafeDelete.Measure(temp).Files);
    }

    // ---------------------------------------------------------------- Caches de shaders

    [Fact]
    public void OldShaderCachesAreMeasuredApartFromAllCaches()
    {
        var shaders = Folder("Shaders");
        File(shaders, "ancien.nvph", 90, 60, bytes: 3000);
        File(shaders, "recent.nvph", 90, 2, bytes: 5000);

        var items = CleanupService.Scan(Paths(shaders));

        Assert.Equal(3000, Item(items, CleanupKind.OldShaderCaches).Bytes);
        Assert.Equal(8000, Item(items, CleanupKind.ShaderCaches).Bytes);
        Assert.False(Item(items, CleanupKind.OldShaderCaches).SelectedByDefault);
        Assert.False(Item(items, CleanupKind.ShaderCaches).SelectedByDefault);
    }

    [Fact]
    public void CleaningOldShaderCachesKeepsTheRecentOnes()
    {
        var shaders = Folder("Shaders");
        var old = File(shaders, "ancien.nvph", 90, 60, bytes: 3000);
        var recent = File(shaders, "recent.nvph", 90, 2, bytes: 5000);
        var paths = Paths(shaders);
        var item = Item(CleanupService.Scan(paths), CleanupKind.OldShaderCaches);

        var result = CleanupService.Run(paths, [item]).Single();

        Assert.Equal(CleanupStatus.Done, result.Status);
        Assert.Equal(3000, result.Freed);
        Assert.False(System.IO.File.Exists(old));
        Assert.True(System.IO.File.Exists(recent));
    }

    [Fact]
    public void OldCachesCheckedWithAllCachesAreCountedOnce()
    {
        var shaders = Folder("Shaders");
        File(shaders, "ancien.nvph", 90, 60, bytes: 3000);
        File(shaders, "recent.nvph", 90, 2, bytes: 5000);
        var items = CleanupService.Scan(Paths(shaders));
        var old = Item(items, CleanupKind.OldShaderCaches);
        var all = Item(items, CleanupKind.ShaderCaches);

        Assert.Equal(3000, CleanupService.Recoverable([old]));
        Assert.Equal(8000, CleanupService.Recoverable([old, all]));
    }

    // ---------------------------------------------------------------- Rapports de plantage

    [Fact]
    public void GraphicsDriverHangReportsAreCrashDumps()
    {
        var paths = CleanupPaths.ForThisPc();

        Assert.Contains(paths.CrashDumpFolders, p => p.EndsWith(@"\LiveKernelReports", StringComparison.OrdinalIgnoreCase));
        Assert.All(paths.CrashDumpFolders, p => Assert.True(SafeDelete.IsSafeRoot(p), p));
    }

    [Fact]
    public void DumpsInSubfoldersAreCountedAndRemoved()
    {
        var dumps = Folder("Dumps");
        File(Folder(@"Dumps\WATCHDOG"), "WATCHDOG-20261101-1200.dmp", 5, 5, bytes: 4000);
        var paths = Paths(dumps: dumps);
        var item = Item(CleanupService.Scan(paths), CleanupKind.CrashDumps);

        Assert.Equal(4000, item.Bytes);
        Assert.Equal(4000, CleanupService.Run(paths, [item]).Single().Freed);
        Assert.True(Directory.Exists(dumps));
    }

    // ---------------------------------------------------------------- Garde-fous

    [Fact]
    public void ALinkedFolderIsNeverFollowed()
    {
        var temp = Folder("Temp");
        var outside = Folder("Ailleurs");
        var precious = File(outside, "important.txt", 400, 400);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(temp, "lien"), outside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return; // créer un lien demande un droit que ce compte n'a pas : rien à vérifier ici
        }

        SafeDelete.Delete(temp);

        Assert.True(System.IO.File.Exists(precious));
    }

    [Theory]
    [InlineData(@"C:\")]
    [InlineData(@"C:\Windows")]
    [InlineData(@"C:\Temp")]
    public void DangerousRootsAreRefused(string root) => Assert.False(SafeDelete.IsSafeRoot(root));

    // ---------------------------------------------------------------- Veille prolongée

    [Theory]
    [InlineData(null, true, true)]    // jamais réglée : le fichier hiberfil.sys le dit
    [InlineData(null, false, false)]
    [InlineData(1, false, true)]
    [InlineData(0, true, false)]      // coupée, fichier pas encore retiré
    public void HibernationStateComesFromTheRegistryThenFromTheFile(int? registry, bool file, bool expected) =>
        Assert.Equal(expected, HibernationSetting.IsEnabled(registry, file));

    [Fact]
    public void HibernationReasonGivesTheSpaceFreed()
    {
        var reason = HibernationSetting.Reason(27_369_803_776);

        Assert.StartsWith("Libère 25,5 Go", reason);
        Assert.Contains("démarrage rapide", reason);
        Assert.StartsWith("Libère le fichier hiberfil.sys", HibernationSetting.Reason(null));
    }

    [Fact]
    public void HibernationIsAnUncheckedDesktopOnlySetting()
    {
        var setting = new HibernationSetting();

        Assert.False(setting.SelectedByDefault);
        Assert.Equal(SettingCategory.Windows, setting.Category);
        Assert.Equal("windows.hibernation", setting.Id);
        Assert.Null(setting.WhyNotApplicable(new HardwareProfile()));
    }

    [Fact]
    public void HibernationRefusesAnUnknownValue() =>
        Assert.Throws<FormatException>(() => new HibernationSetting().Apply(new HardwareProfile(), new SettingValue("peut-être", "?")));

    [Fact]
    public void HibernationIsInTheCatalog() =>
        Assert.Contains(SettingsCatalog.Build(new HardwareProfile()), s => s.Id == "windows.hibernation");
}
