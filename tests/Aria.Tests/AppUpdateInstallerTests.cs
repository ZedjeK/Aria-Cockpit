using System.IO.Compression;
using Aria.Core.Updates;

namespace Aria.Tests;

/// <summary>
/// Installation d'une nouvelle version : décompression limitée aux fichiers attendus, version en cours gardée,
/// remplacement défait à la première erreur, retour à la version gardée. Tout se passe dans un dossier jetable.
/// </summary>
public sealed class AppUpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aria-tests-" + Guid.NewGuid().ToString("N"));

    public AppUpdateInstallerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly Version Old = new(0, 26, 0), New = new(0, 27, 0);

    private string App => Folder("appli");
    private string Staging => Path.Combine(_root, "decompression");
    private string Versions => Path.Combine(_root, "versions");
    private string Cache => Folder("cache");

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Put(string folder, string name, string content)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, name), content);
    }

    private static string Read(string folder, string name) => File.ReadAllText(Path.Combine(folder, name));

    /// <summary>Une appli installée : les deux programmes et la licence, marqués du numéro de version.</summary>
    private void InstallApp(string version)
    {
        Put(App, "Aria-Cockpit.exe", $"cockpit {version}");
        Put(App, "Aria-Bench.exe", $"banc {version}");
        Put(App, "LICENSE", $"licence {version}");
    }

    private string Zip(params (string Name, string Content)[] entries)
    {
        var zip = Path.Combine(_root, "maj.zip");
        File.Delete(zip);
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
        return zip;
    }

    private string GoodZip(string version) => Zip(
        ("Aria-Cockpit.exe", $"cockpit {version}"), ("Aria-Bench.exe", $"banc {version}"),
        ("LICENSE", $"licence {version}"), ("THIRD-PARTY-NOTICES.txt", "mentions"));

    // ----- D'où Aria peut se remplacer -----

    [Fact]
    public void AnExeUnzippedAnywhereCanReplaceItself() =>
        Assert.Null(AppUpdateInstaller.WhyNotInstallable(@"D:\Outils\Aria\Aria-Cockpit.exe", @"C:\Program Files\Aria Cockpit"));

    [Fact]
    public void TheProtectedCopyNeverReplacesItself() =>
        Assert.NotNull(AppUpdateInstaller.WhyNotInstallable(@"C:\Program Files\Aria Cockpit\Aria-Cockpit.exe", @"C:\Program Files\Aria Cockpit"));

    [Fact]
    public void AFolderThatOnlyStartsLikeTheProtectedOneIsNotProtected() =>
        Assert.Null(AppUpdateInstaller.WhyNotInstallable(@"C:\Program Files\Aria Cockpit 2\Aria-Cockpit.exe", @"C:\Program Files\Aria Cockpit"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"D:\Outils\Cockpit-renomme.exe")]
    public void AnUnknownOrRenamedExeIsNotReplaced(string? exe) =>
        Assert.NotNull(AppUpdateInstaller.WhyNotInstallable(exe, @"C:\Program Files\Aria Cockpit"));

    // ----- Décompression -----

    [Fact]
    public void TheExpectedFilesAreExtracted()
    {
        var result = AppUpdateInstaller.Extract(GoodZip("0.27.0"), Staging);

        Assert.True(result.Done, result.Error);
        Assert.Equal("cockpit 0.27.0", Read(Staging, "Aria-Cockpit.exe"));
        Assert.Equal("banc 0.27.0", Read(Staging, "Aria-Bench.exe"));
        Assert.Equal(4, Directory.GetFiles(Staging).Length);
    }

    [Fact]
    public void AnythingElseInTheZipIsIgnoredAndNothingIsWrittenOutside()
    {
        var zip = Zip(
            ("Aria-Cockpit.exe", "cockpit"), ("Aria-Bench.exe", "banc"),
            ("../evasion.txt", "dehors"), ("..\\evasion2.txt", "dehors"), ("sous/Aria-Cockpit.exe", "leurre"),
            ("autre.dll", "en trop"));

        var result = AppUpdateInstaller.Extract(zip, Staging);

        Assert.True(result.Done, result.Error);
        Assert.Equal(["Aria-Bench.exe", "Aria-Cockpit.exe"], Directory.GetFiles(Staging).Select(Path.GetFileName).Order());
        Assert.Empty(Directory.GetDirectories(Staging));
        Assert.Equal("cockpit", Read(Staging, "Aria-Cockpit.exe"));
        Assert.False(File.Exists(Path.Combine(_root, "evasion.txt")));
        Assert.False(File.Exists(Path.Combine(_root, "evasion2.txt")));
    }

    [Fact]
    public void AZipWithoutBothProgramsIsRefusedAndLeavesNothing()
    {
        var result = AppUpdateInstaller.Extract(Zip(("Aria-Cockpit.exe", "cockpit")), Staging);

        Assert.False(result.Done);
        Assert.Contains("Aria-Bench.exe", result.Error);
        Assert.False(Directory.Exists(Staging));
    }

    [Fact]
    public void AZipWithTheSameFileTwiceIsRefused()
    {
        var zip = Zip(("Aria-Cockpit.exe", "un"), ("Aria-Bench.exe", "banc"), ("Aria-Cockpit.exe", "deux"));

        Assert.False(AppUpdateInstaller.Extract(zip, Staging).Done);
        Assert.False(Directory.Exists(Staging));
    }

    [Fact]
    public void AFileThatIsNotAZipIsRefused()
    {
        var fake = Path.Combine(_root, "faux.zip");
        File.WriteAllText(fake, "ceci n'est pas un zip");

        Assert.False(AppUpdateInstaller.Extract(fake, Staging).Done);
    }

    [Fact]
    public void ExtractingAgainStartsFromAnEmptyFolder()
    {
        Put(Staging, "reste.txt", "d'un essai précédent");

        Assert.True(AppUpdateInstaller.Extract(GoodZip("0.27.0"), Staging).Done);
        Assert.False(File.Exists(Path.Combine(Staging, "reste.txt")));
    }

    // ----- Installer -----

    [Fact]
    public void InstallingKeepsTheCurrentVersionAndPutsTheNewOneInPlace()
    {
        InstallApp("0.26.0");
        AppUpdateInstaller.Extract(GoodZip("0.27.0"), Staging);

        var result = AppUpdateInstaller.Install(Staging, App, Old, Versions);

        Assert.True(result.Done, result.Error);
        Assert.Equal("cockpit 0.27.0", Read(App, "Aria-Cockpit.exe"));
        Assert.Equal("banc 0.27.0", Read(App, "Aria-Bench.exe"));
        Assert.Equal("licence 0.27.0", Read(App, "LICENSE"));
        // La version quittée : à côté de l'exe le temps de la relance, et gardée dans les sauvegardes
        Assert.Equal("cockpit 0.26.0", Read(App, "Aria-Cockpit.exe.ancien"));
        Assert.Equal("cockpit 0.26.0", Read(Path.Combine(Versions, "0.26.0"), "Aria-Cockpit.exe"));
        Assert.Equal("banc 0.26.0", Read(Path.Combine(Versions, "0.26.0"), "Aria-Bench.exe"));
        Assert.Equal(Old, AppUpdateInstaller.KeptVersion(Versions));
    }

    [Fact]
    public void OnlyThePreviousVersionIsKept()
    {
        Put(Path.Combine(Versions, "0.25.0"), "Aria-Cockpit.exe", "cockpit 0.25.0");
        InstallApp("0.26.0");

        AppUpdateInstaller.Keep(App, Old, Versions);

        Assert.Equal(["0.26.0"], Directory.GetDirectories(Versions).Select(Path.GetFileName));
    }

    [Fact]
    public void KeepingTheSameVersionAgainReplacesItsCopy()
    {
        Put(Path.Combine(Versions, "0.26.0"), "Aria-Cockpit.exe", "ancienne copie");
        InstallApp("0.26.0");

        AppUpdateInstaller.Keep(App, Old, Versions);

        Assert.Equal("cockpit 0.26.0", Read(Path.Combine(Versions, "0.26.0"), "Aria-Cockpit.exe"));
    }

    [Fact]
    public void AFailedReplacementPutsEverythingBack()
    {
        InstallApp("0.26.0");
        AppUpdateInstaller.Extract(GoodZip("0.27.0"), Staging);
        AppInstallResult result;
        // Aria-Bench.exe est tenu ouvert sans droit de renommage, comme un banc d'essai en cours : son remplacement
        // échoue alors qu'Aria-Cockpit.exe, traité avant lui, a déjà été remplacé
        using (File.Open(Path.Combine(App, "Aria-Bench.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            result = AppUpdateInstaller.Swap(Staging, App);

        Assert.False(result.Done);
        Assert.Contains("Aria-Bench.exe", result.Error);
        Assert.Equal("cockpit 0.26.0", Read(App, "Aria-Cockpit.exe"));
        Assert.Equal("banc 0.26.0", Read(App, "Aria-Bench.exe"));
        Assert.Equal("licence 0.26.0", Read(App, "LICENSE"));
        Assert.Equal(["Aria-Bench.exe", "Aria-Cockpit.exe", "LICENSE"], Directory.GetFiles(App).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void AnAppWithoutItsExeIsNotTouched()
    {
        Put(App, "Aria-Bench.exe", "banc seul");
        AppUpdateInstaller.Extract(GoodZip("0.27.0"), Staging);

        var result = AppUpdateInstaller.Install(Staging, App, Old, Versions);

        Assert.False(result.Done);
        Assert.Equal("banc seul", Read(App, "Aria-Bench.exe"));
        Assert.False(File.Exists(Path.Combine(App, "Aria-Cockpit.exe")));
    }

    // ----- Revenir à la version gardée -----

    [Fact]
    public void GoingBackRestoresTheKeptVersionAndKeepsTheOneLeft()
    {
        InstallApp("0.26.0");
        AppUpdateInstaller.Extract(GoodZip("0.27.0"), Staging);
        AppUpdateInstaller.Install(Staging, App, Old, Versions);
        AppUpdateInstaller.CleanUp(App, Cache);

        var result = AppUpdateInstaller.Restore(App, New, Versions);

        Assert.True(result.Done, result.Error);
        Assert.Equal("cockpit 0.26.0", Read(App, "Aria-Cockpit.exe"));
        Assert.Equal("banc 0.26.0", Read(App, "Aria-Bench.exe"));
        Assert.Equal(["0.27.0"], Directory.GetDirectories(Versions).Select(Path.GetFileName));
        Assert.Equal("cockpit 0.27.0", Read(Path.Combine(Versions, "0.27.0"), "Aria-Cockpit.exe"));
        Assert.Equal(New, AppUpdateInstaller.KeptVersion(Versions));
    }

    [Fact]
    public void GoingBackWithoutAKeptVersionChangesNothing()
    {
        InstallApp("0.27.0");

        var result = AppUpdateInstaller.Restore(App, New, Versions);

        Assert.False(result.Done);
        Assert.Equal("cockpit 0.27.0", Read(App, "Aria-Cockpit.exe"));
    }

    [Fact]
    public void AFailedReturnLeavesTheKeptVersionWhereItWas()
    {
        InstallApp("0.27.0");
        Put(Path.Combine(Versions, "0.26.0"), "Aria-Cockpit.exe", "cockpit 0.26.0");
        Put(Path.Combine(Versions, "0.26.0"), "Aria-Bench.exe", "banc 0.26.0");
        AppInstallResult result;
        using (File.Open(Path.Combine(App, "Aria-Bench.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            result = AppUpdateInstaller.Restore(App, New, Versions);

        Assert.False(result.Done);
        Assert.Equal("cockpit 0.27.0", Read(App, "Aria-Cockpit.exe"));
        Assert.Equal(["0.26.0"], Directory.GetDirectories(Versions).Select(Path.GetFileName));
        Assert.Equal("cockpit 0.26.0", Read(Path.Combine(Versions, "0.26.0"), "Aria-Cockpit.exe"));
    }

    [Fact]
    public void AFolderThatIsNotAVersionIsNotAKeptVersion()
    {
        Put(Path.Combine(Versions, "brouillon"), "Aria-Cockpit.exe", "x");
        Put(Path.Combine(Versions, "0.25.0"), "autre.txt", "sans exe");

        Assert.Null(AppUpdateInstaller.KeptVersion(Versions));
    }

    // ----- Après la relance -----

    [Fact]
    public void TheRelaunchedVersionRemovesWhatTheReplacementLeft()
    {
        InstallApp("0.26.0");
        AppUpdateInstaller.Extract(GoodZip("0.27.0"), Staging);
        AppUpdateInstaller.Install(Staging, App, Old, Versions);
        Put(Path.Combine(Cache, "maj-0.27.0"), "Aria-Cockpit.exe", "reste de décompression");
        Put(Cache, "maj-Aria-Cockpit-0.27.0-leger.zip", "le zip contrôlé");
        Put(Cache, "autre.txt", "sans rapport");

        AppUpdateInstaller.CleanUp(App, Cache);

        Assert.Equal(["Aria-Bench.exe", "Aria-Cockpit.exe", "LICENSE", "THIRD-PARTY-NOTICES.txt"],
            Directory.GetFiles(App).Select(Path.GetFileName).Order());
        Assert.Empty(Directory.GetDirectories(Cache));
        // Les fichiers du cache restent : le ménage hebdomadaire s'en charge
        Assert.Equal(2, Directory.GetFiles(Cache).Length);
    }

    [Fact]
    public void TheInstallIsAnnouncedOnceToTheRelaunchedVersion()
    {
        Put(Cache, AppUpdateInstaller.DoneMarker, "0.26.0>0.27.0");

        Assert.Equal((Old, New), AppUpdateInstaller.TakeDoneMarker(Cache));
        Assert.Null(AppUpdateInstaller.TakeDoneMarker(Cache));
    }

    [Fact]
    public void AnUnreadableMarkerIsDroppedWithoutAnnouncingAnything()
    {
        Put(Cache, AppUpdateInstaller.DoneMarker, "n'importe quoi");

        Assert.Null(AppUpdateInstaller.TakeDoneMarker(Cache));
        Assert.False(File.Exists(Path.Combine(Cache, AppUpdateInstaller.DoneMarker)));
    }
}
