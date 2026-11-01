using System.Security.AccessControl;
using System.Security.Principal;
using Aria.Core.Backup;

namespace Aria.Tests;

/// <summary>Dossier des données : repris de l'ancien emplacement, et modifiable par les seuls administrateurs.</summary>
public sealed class DataFolderTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "aria-tests-" + Guid.NewGuid().ToString("N"));

    public DataFolderTests() => Directory.CreateDirectory(_temp);

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void OldProgramDataFolderIsMovedWithItsFiles()
    {
        var home = Path.Combine(_temp, ".Aria-Cockpit");
        var old = Path.Combine(_temp, "AriaCockpit");
        Directory.CreateDirectory(Path.Combine(old, "Sauvegardes"));
        File.WriteAllText(Path.Combine(old, "Sauvegardes", "sauvegarde_2026-10-30_120000.json"), "{}");

        Assert.Equal(home, BackupStore.PrepareDataRoot(home, old, Path.Combine(_temp, "Aria")));
        Assert.True(File.Exists(Path.Combine(home, "Sauvegardes", "sauvegarde_2026-10-30_120000.json")));
        Assert.False(Directory.Exists(old));
    }

    [Fact]
    public void MostRecentOldFolderWins()
    {
        var home = Path.Combine(_temp, ".Aria-Cockpit");
        Directory.CreateDirectory(Path.Combine(_temp, "AriaCockpit"));
        Directory.CreateDirectory(Path.Combine(_temp, "PCTune"));

        BackupStore.PrepareDataRoot(home, Path.Combine(_temp, "AriaCockpit"), Path.Combine(_temp, "PCTune"));

        Assert.True(Directory.Exists(Path.Combine(_temp, "PCTune")));
    }

    [Fact]
    public void ExistingFolderIsKeptAndOldOneLeftAlone()
    {
        var home = Directory.CreateDirectory(Path.Combine(_temp, ".Aria-Cockpit")).FullName;
        var old = Directory.CreateDirectory(Path.Combine(_temp, "AriaCockpit")).FullName;

        Assert.Equal(home, BackupStore.PrepareDataRoot(home, old));
        Assert.True(Directory.Exists(old));
    }

    [Fact]
    public void NewInstallUsesTheProfileFolder()
    {
        var home = Path.Combine(_temp, ".Aria-Cockpit");
        Assert.Equal(home, BackupStore.PrepareDataRoot(home, Path.Combine(_temp, "AriaCockpit")));
    }

    [Fact]
    public void UserCanReadButOnlyAdministratorsWrite()
    {
        var user = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var security = DataFolderSecurity.RootSecurity(user);
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();

        Assert.True(security.AreAccessRulesProtected);                         // rien d'hérité du profil
        Assert.Equal(admins, security.GetOwner(typeof(SecurityIdentifier)));
        var userRule = Assert.Single(rules, r => r.IdentityReference.Equals(user));
        Assert.Equal(FileSystemRights.ReadAndExecute, userRule.FileSystemRights & ~FileSystemRights.Synchronize);
        Assert.Equal(FileSystemRights.FullControl, Assert.Single(rules, r => r.IdentityReference.Equals(admins)).FileSystemRights);
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
    }
}
