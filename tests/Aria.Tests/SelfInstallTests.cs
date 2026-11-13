using System.Security.AccessControl;
using System.Security.Principal;
using Aria.Core.Backup;
using Aria.Core.Install;
using Aria.Core.Settings;
using Aria.Core.Updates;

namespace Aria.Tests;

/// <summary>Installation d'Aria Cockpit par son exe : où il tourne, droits du dossier, copie, raccourci.</summary>
public sealed class SelfInstallTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "aria-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _downloads;
    private readonly string _home;
    private readonly string _versions;
    private readonly string _cache;
    private readonly string _shortcut;

    public SelfInstallTests()
    {
        _downloads = Directory.CreateDirectory(Path.Combine(_temp, "Telechargements")).FullName;
        _home = Path.Combine(_temp, "donnees", "programme");
        _versions = Path.Combine(_temp, "donnees", "Sauvegardes", "versions");
        _cache = Path.Combine(_temp, "donnees", "cache");
        _shortcut = Path.Combine(Directory.CreateDirectory(Path.Combine(_temp, "Bureau")).FullName, DesktopShortcut.FileName);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temp)) Directory.Delete(_temp, recursive: true);
    }

    private static readonly Func<string, string?> Safe = _ => null;

    private SelfInstallResult Install(string exe, bool shortcut = false, Func<string, string?>? whyUnsafe = null) =>
        SelfInstall.Install(exe, shortcut, Path.Combine(_temp, "donnees"), _home, _versions, _shortcut, _cache, whyUnsafe ?? Safe);

    private string Download(string name, string content = "exe") => Write(Path.Combine(_downloads, name), content);

    private static string Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    // ---- Où tourne l'exe ----

    [Theory]
    [InlineData(@"C:\Users\x\.Aria-Cockpit\programme\Aria-Cockpit.exe", InstallPlace.Home)]
    [InlineData(@"c:\users\x\.aria-cockpit\PROGRAMME\Aria-Cockpit.exe", InstallPlace.Home)]
    [InlineData(@"C:\Program Files\Aria Cockpit\Aria-Cockpit.exe", InstallPlace.ProtectedCopy)]
    [InlineData(@"C:\Users\x\Downloads\Aria-Cockpit.exe", InstallPlace.Elsewhere)]
    [InlineData(@"C:\Users\x\.Aria-Cockpit\programme-autre\Aria-Cockpit.exe", InstallPlace.Elsewhere)]
    public void PlaceOfTheExe(string exe, InstallPlace expected) =>
        Assert.Equal(expected, SelfInstall.PlaceOf(exe, @"C:\Users\x\.Aria-Cockpit\programme", @"C:\Program Files\Aria Cockpit"));

    [Fact]
    public void UnknownPathIsNeverOffered()
    {
        Assert.Equal(InstallPlace.Unknown, SelfInstall.PlaceOf(null, _home, _temp));
        Assert.False(SelfInstall.ShouldOffer(null, AppPreferences.Default, []));
    }

    [Fact]
    public void AnExeLaunchedElsewhereIsOffered()
    {
        var exe = Path.Combine(_downloads, "Aria-Cockpit.exe");

        Assert.Equal(InstallPlace.Elsewhere, SelfInstall.PlaceOf(exe));
        Assert.True(SelfInstall.ShouldOffer(exe, AppPreferences.Default, []));
    }

    [Fact]
    public void TheOfferCanBeSwitchedOffInTheSettings() =>
        Assert.False(SelfInstall.ShouldOffer(Path.Combine(_downloads, "Aria-Cockpit.exe"), new AppPreferences { OfferInstall = false }, []));

    [Fact]
    public void ThePortableArgumentSkipsTheOffer() =>
        Assert.False(SelfInstall.ShouldOffer(Path.Combine(_downloads, "Aria-Cockpit.exe"), AppPreferences.Default, ["--PORTABLE"]));

    [Fact]
    public void OfferInstallIsOnByDefaultAndSurvivesTheFile()
    {
        Assert.True(AppPreferences.Default.OfferInstall);

        var off = AppPreferencesStore.FromJson(AppPreferencesStore.ToJson(new AppPreferences { OfferInstall = false }));

        Assert.False(off.OfferInstall);
        Assert.Contains("proposerInstallation", AppPreferencesStore.ToJson(off));
    }

    // ---- Droits du dossier ----

    [Fact]
    public void TheDataFolderRightsAreSafe() =>
        Assert.Null(SelfInstall.WhyUnsafe(DataFolderSecurity.RootSecurity(WindowsIdentity.GetCurrent().User)));

    [Fact]
    public void AFolderWritableByUsersIsNotSafe()
    {
        var security = DataFolderSecurity.RootSecurity(WindowsIdentity.GetCurrent().User);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        security.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));

        Assert.Contains("peut y écrire", SelfInstall.WhyUnsafe(security));
    }

    [Fact]
    public void AFolderOwnedByTheUserIsNotSafe()
    {
        // Un propriétaire peut toujours changer les droits : être en lecture seule ne protège pas
        var security = DataFolderSecurity.RootSecurity(WindowsIdentity.GetCurrent().User);
        security.SetOwner(WindowsIdentity.GetCurrent().User!);

        Assert.Contains("propriétaire", SelfInstall.WhyUnsafe(security));
    }

    [Fact]
    public void ReadOnlyForTheUserIsEnough()
    {
        var security = DataFolderSecurity.RootSecurity(WindowsIdentity.GetCurrent().User);

        Assert.Null(SelfInstall.WhyUnsafe(security));
    }

    // ---- Installation ----

    [Fact]
    public void TheExeAndItsCompanionsLandInTheProgramFolder()
    {
        var exe = Download("Aria-Cockpit.exe", "cockpit");
        Download("Aria-Bench.exe", "bench");
        Download("LICENSE", "licence");

        var outcome = Install(exe);

        Assert.True(outcome.Done, outcome.Error);
        Assert.Equal(Path.Combine(_home, "Aria-Cockpit.exe"), outcome.Exe);
        Assert.False(outcome.BenchMissing);
        Assert.Equal("cockpit", File.ReadAllText(Path.Combine(_home, "Aria-Cockpit.exe")));
        Assert.Equal("bench", File.ReadAllText(Path.Combine(_home, "Aria-Bench.exe")));
        Assert.Equal("licence", File.ReadAllText(Path.Combine(_home, "LICENSE")));
    }

    [Fact]
    public void TheSourceIsLeftWhereItWas()
    {
        var exe = Download("Aria-Cockpit.exe");

        Install(exe);

        Assert.True(File.Exists(exe));
    }

    [Fact]
    public void ABrowserRenamedExeIsInstalledUnderItsRealName()
    {
        var exe = Download("Aria-Cockpit (1).exe", "cockpit");

        var outcome = Install(exe);

        Assert.True(outcome.Done, outcome.Error);
        Assert.Equal(["Aria-Cockpit.exe"], Directory.GetFiles(_home).Select(Path.GetFileName));
    }

    [Fact]
    public void WithoutTheBenchTheOutcomeSaysSo()
    {
        var outcome = Install(Download("Aria-Cockpit.exe"));

        Assert.True(outcome.Done, outcome.Error);
        Assert.True(outcome.BenchMissing);
    }

    [Fact]
    public void OnlyTheExpectedFilesAreCopied()
    {
        var exe = Download("Aria-Cockpit.exe");
        Download("notes-perso.txt");
        Download("autre.dll");

        Install(exe);

        Assert.Equal(["Aria-Cockpit.exe"], Directory.GetFiles(_home).Select(Path.GetFileName));
    }

    [Fact]
    public void TheInstalledFilesDoNotCarryTheDownloadMark()
    {
        var exe = Download("Aria-Cockpit.exe");
        File.WriteAllText(exe + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3");
        Assert.True(File.ReadAllText(exe + ":Zone.Identifier").Length > 0);

        Install(exe);

        Assert.Throws<FileNotFoundException>(() => File.ReadAllText(Path.Combine(_home, "Aria-Cockpit.exe") + ":Zone.Identifier"));
    }

    [Fact]
    public void AnUnprotectedDataFolderRefusesTheInstallation()
    {
        var exe = Download("Aria-Cockpit.exe");

        var outcome = Install(exe, whyUnsafe: _ => "Users peut y écrire");

        Assert.False(outcome.Done);
        Assert.Contains("Users peut y écrire", outcome.Error);
        Assert.False(Directory.Exists(_home));
    }

    [Fact]
    public void AnUnprotectedProgramFolderRefusesTheInstallation()
    {
        var exe = Download("Aria-Cockpit.exe");

        // Les données sont sûres, le dossier du programme ne l'est pas : il n'hérite pas, ou a reçu d'autres droits
        var outcome = Install(exe, whyUnsafe: folder => folder == _home ? "Users peut y écrire" : null);

        Assert.False(outcome.Done);
        Assert.Contains("dossier du programme", outcome.Error);
        Assert.Empty(Directory.GetFiles(_home));
    }

    [Fact]
    public void ThePreviousVersionIsKeptAside()
    {
        // L'assembly de test sert d'« exe » : elle porte un numéro de version, comme un vrai Aria-Cockpit.exe
        var versioned = typeof(SelfInstallTests).Assembly.Location;
        Directory.CreateDirectory(_home);
        File.Copy(versioned, Path.Combine(_home, "Aria-Cockpit.exe"));
        var installed = SelfInstall.InstalledVersion(_home);
        Assert.NotNull(installed);

        var outcome = Install(Download("Aria-Cockpit.exe", "nouvelle version"));

        Assert.True(outcome.Done, outcome.Error);
        Assert.Equal("nouvelle version", File.ReadAllText(Path.Combine(_home, "Aria-Cockpit.exe")));
        Assert.True(File.Exists(Path.Combine(_versions, installed.ToString(3), "Aria-Cockpit.exe")));
    }

    [Fact]
    public void InstalledVersionOfAnEmptyFolderIsNull() => Assert.Null(SelfInstall.InstalledVersion(_home));

    // ---- Raccourci ----

    [Fact]
    public void NoShortcutIsCreatedUnlessAskedFor()
    {
        var outcome = Install(Download("Aria-Cockpit.exe"));

        Assert.True(outcome.Done, outcome.Error);
        Assert.False(outcome.ShortcutDone);
        Assert.False(File.Exists(_shortcut));
    }

    [Fact]
    public void TheShortcutPointsToTheInstalledExe()
    {
        var outcome = Install(Download("Aria-Cockpit.exe"), shortcut: true);

        Assert.True(outcome.Done, outcome.Error);
        Assert.True(outcome.ShortcutDone, outcome.ShortcutError);
        Assert.Equal(Path.Combine(_home, "Aria-Cockpit.exe"), DesktopShortcut.TargetOf(_shortcut));
    }

    [Fact]
    public void AnExistingShortcutIsPointedAtTheInstalledExe()
    {
        DesktopShortcut.Create(_shortcut, Download("ancien.exe"));

        Install(Download("Aria-Cockpit.exe"), shortcut: true);

        Assert.Equal(Path.Combine(_home, "Aria-Cockpit.exe"), DesktopShortcut.TargetOf(_shortcut));
    }

    [Fact]
    public void AShortcutThatFailsDoesNotUndoTheInstallation()
    {
        var missingDesktop = Path.Combine(_temp, "bureau-absent", DesktopShortcut.FileName);

        var outcome = SelfInstall.Install(Download("Aria-Cockpit.exe"), shortcut: true, Path.Combine(_temp, "donnees"), _home, _versions, missingDesktop, _cache, Safe);

        Assert.True(outcome.Done);
        Assert.False(outcome.ShortcutDone);
        Assert.NotNull(outcome.ShortcutError);
        Assert.True(File.Exists(Path.Combine(_home, "Aria-Cockpit.exe")));
    }

    [Fact]
    public void ARemovalOnlyTouchesOurOwnShortcut()
    {
        var other = Download("autre-programme.exe");
        DesktopShortcut.Create(_shortcut, other);

        Assert.False(DesktopShortcut.Remove(_shortcut, Path.Combine(_home, "Aria-Cockpit.exe")));
        Assert.True(File.Exists(_shortcut));
        Assert.True(DesktopShortcut.Remove(_shortcut, other));
        Assert.False(File.Exists(_shortcut));
    }

    [Fact]
    public void TargetOfAMissingShortcutIsNull() => Assert.Null(DesktopShortcut.TargetOf(_shortcut));
}
