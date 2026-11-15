using System.Xml.Linq;
using Aria.Core.Models;
using Aria.Core.Startup;
using Microsoft.Win32;

namespace Aria.Tests;

/// <summary>
/// Tâches de démarrage des applications du Store (lecture du manifeste, état, activation) et vrai nom des programmes
/// lancés par un lanceur Squirrel. Les écritures se font dans une clé d'essai de l'utilisateur et dans un dossier
/// temporaire, supprimés à la fin : aucune vraie tâche de démarrage n'est touchée.
/// Même collection que les autres tests qui partagent cette clé : ils ne tournent pas en même temps, sinon l'un
/// supprimerait la clé parente pendant que l'autre y écrit.
/// </summary>
[Collection("RegistreEssai")]
public sealed class StoreStartupTests : IDisposable
{
    private const string TestParent = @"Software\Aria-Cockpit-Tests";
    private static readonly string TestRoot = TestParent + @"\" + Guid.NewGuid().ToString("N");
    private const string Family = "AppUp.IntelArcSoftware_8j3eq9eme6ctt";
    private const string TaskId = "IntelGraphicsSoftwareStartup";
    private static readonly string Name = StoreStartupTasks.EntryName(Family, TaskId);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "Aria-Cockpit-Tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
        bool empty;
        using (var parent = Registry.CurrentUser.OpenSubKey(TestParent)) empty = parent is { SubKeyCount: 0, ValueCount: 0 };
        if (empty) Registry.CurrentUser.DeleteSubKey(TestParent, throwOnMissingSubKey: false);
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    /// <summary>Note une tâche comme le fait Windows ; « opened » ajoute la clé d'activité créée à la première ouverture de l'appli.</summary>
    private static void Store(int state, bool opened = false, bool userEnabledOnce = false)
    {
        using var key = Registry.CurrentUser.CreateSubKey($@"{TestRoot}\{Family}\{TaskId}", writable: true);
        key.SetValue("State", state, RegistryValueKind.DWord);
        key.SetValue("UserEnabledStartupOnce", userEnabledOnce ? 1 : 0, RegistryValueKind.DWord);
        if (opened) Registry.CurrentUser.CreateSubKey($@"{TestRoot}\{Family}\HAM\AUI\INTELGRAPHICSSOFTWARE\V1\LU").Dispose();
    }

    private static object? Stored(string value)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\{Family}\{TaskId}");
        return key?.GetValue(value);
    }

    private static int? Stored()
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\{Family}\{TaskId}");
        return key?.GetValue("State") as int?;
    }

    // ---- Noms ----

    [Theory]
    [InlineData("AppUp.IntelArcSoftware_26.36.2702.0_x64__8j3eq9eme6ctt", "AppUp.IntelArcSoftware_8j3eq9eme6ctt")]
    [InlineData("Microsoft.WindowsTerminal_1.25.3131.0_neutral_split.scale-100_8wekyb3d8bbwe", "Microsoft.WindowsTerminal_8wekyb3d8bbwe")]
    [InlineData("Claude_5.11492.5.0_x64__pzs8sxrjxfjjc", "Claude_pzs8sxrjxfjjc")]
    [InlineData("PasUnPaquet", null)]
    [InlineData("Trop_de_parties_dans_ce_nom_la", null)]
    [InlineData("_1.0_x64__editeur", null)]
    public void ThePackageFamilyComesFromItsFullName(string fullName, string? expected)
    {
        Assert.Equal(expected, StoreStartupTasks.FamilyOf(fullName));
    }

    [Fact]
    public void AnEntryNameSplitsBackIntoFamilyAndTask()
    {
        Assert.Equal("AppUp.IntelArcSoftware_8j3eq9eme6ctt!IntelGraphicsSoftwareStartup", Name);
        Assert.Equal((Family, TaskId), StoreStartupTasks.Split(Name));
    }

    [Theory]
    [InlineData("SansSeparateur")]
    [InlineData("!Tache")]
    [InlineData("Famille!")]
    [InlineData(@"Famille\Ailleurs!Tache")]
    [InlineData(@"Famille!Tache\Ailleurs")]
    public void AMalformedEntryNameIsRefused(string name)
    {
        Assert.Null(StoreStartupTasks.Split(name));
        Assert.False(StoreStartupTasks.IsEnabled(name, TestRoot));
        Assert.Throws<FormatException>(() => StoreStartupTasks.SetEnabled(name, false, TestRoot));
    }

    // ---- États ----

    [Theory]
    [InlineData(StoreStartupTasks.Disabled, false, true)]
    [InlineData(StoreStartupTasks.DisabledByUser, false, true)]
    [InlineData(StoreStartupTasks.Enabled, true, true)]
    [InlineData(StoreStartupTasks.DisabledByPolicy, false, false)]
    [InlineData(StoreStartupTasks.EnabledByPolicy, true, false)]
    public void StatesFollowTheDocumentedEnumeration(int state, bool enabled, bool userControlled)
    {
        Assert.Equal(enabled, StoreStartupTasks.IsEnabledState(state));
        Assert.Equal(userControlled, StoreStartupTasks.IsUserControlled(state));
    }

    [Theory]
    [InlineData(StoreStartupTasks.Enabled, true, false, true)]              // notée activée, appli déjà ouverte
    [InlineData(StoreStartupTasks.Enabled, false, true, true)]              // activée par l'utilisateur dans les Paramètres
    [InlineData(StoreStartupTasks.Enabled, false, false, false)]            // activée par son manifeste, jamais ouverte
    [InlineData(StoreStartupTasks.Disabled, true, true, false)]
    [InlineData(StoreStartupTasks.DisabledByUser, true, true, false)]
    [InlineData(StoreStartupTasks.DisabledByPolicy, true, true, false)]
    [InlineData(StoreStartupTasks.EnabledByPolicy, false, false, true)]
    public void ATaskTakesEffectOnlyOnceTheAppHasBeenOpenedOrTheUserEnabledIt(int state, bool opened, bool userEnabledOnce, bool expected)
    {
        Assert.Equal(expected, StoreStartupTasks.TakesEffect(state, opened, userEnabledOnce));
    }

    [Fact]
    public void ATaskEnabledByItsManifestIsOffUntilTheAppIsOpened()
    {
        // Relevé sur un vrai PC : « State » = 2 depuis l'installation, et Windows affiche « Désactivé »
        Store(StoreStartupTasks.Enabled);
        Assert.False(StoreStartupTasks.IsEnabled(Name, TestRoot));

        Store(StoreStartupTasks.Enabled, opened: true);                     // l'appli est ouverte une fois : rien d'autre ne change
        Assert.True(StoreStartupTasks.IsEnabled(Name, TestRoot));
        Assert.Equal(StoreStartupTasks.Enabled, Stored());
    }

    [Fact]
    public void ATaskTheUserEnabledInTheSettingsIsOnEvenIfTheAppWasNeverOpened()
    {
        Store(StoreStartupTasks.Enabled, userEnabledOnce: true);

        Assert.True(StoreStartupTasks.IsEnabled(Name, TestRoot));
    }

    [Fact]
    public void DisablingWritesWhatTheWindowsSettingsWrite()
    {
        Store(StoreStartupTasks.Enabled, opened: true);
        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        StoreStartupTasks.SetEnabled(Name, false, TestRoot);

        Assert.Equal(StoreStartupTasks.DisabledByUser, Stored());
        Assert.False(StoreStartupTasks.IsEnabled(Name, TestRoot));
        var disabledAt = Assert.IsType<long>(Stored("LastDisabledTime"));    // QWORD, en secondes Unix
        Assert.InRange(disabledAt, before, before + 60);
        Assert.Equal(0, Stored("UserEnabledStartupOnce"));                    // laissée telle quelle
    }

    [Fact]
    public void EnablingAgainOnlyChangesTheState()
    {
        Store(StoreStartupTasks.Enabled, opened: true);
        StoreStartupTasks.SetEnabled(Name, false, TestRoot);
        var disabledAt = Stored("LastDisabledTime");

        StoreStartupTasks.SetEnabled(Name, true, TestRoot);

        Assert.Equal(StoreStartupTasks.Enabled, Stored());
        Assert.True(StoreStartupTasks.IsEnabled(Name, TestRoot));
        Assert.Equal(disabledAt, Stored("LastDisabledTime"));
    }

    [Fact]
    public void ATaskNeverEnabledIsNotMarkedDisabledByTheUser()
    {
        Store(StoreStartupTasks.Disabled);

        StoreStartupTasks.SetEnabled(Name, false, TestRoot);

        Assert.Equal(StoreStartupTasks.Disabled, Stored());
        Assert.Null(Stored("LastDisabledTime"));
    }

    [Theory]
    [InlineData(StoreStartupTasks.DisabledByPolicy)]
    [InlineData(StoreStartupTasks.EnabledByPolicy)]
    public void ATaskSetByPolicyIsLeftAlone(int state)
    {
        Store(state);

        Assert.Throws<InvalidOperationException>(() => StoreStartupTasks.SetEnabled(Name, state == StoreStartupTasks.DisabledByPolicy, TestRoot));
        Assert.Equal(state, Stored());
    }

    [Fact]
    public void AMissingTaskIsReportedAndNoKeyIsCreated()
    {
        Assert.False(StoreStartupTasks.IsEnabled(Name, TestRoot));
        Assert.Throws<InvalidOperationException>(() => StoreStartupTasks.SetEnabled(Name, true, TestRoot));

        using var key = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\{Family}");
        Assert.Null(key);
    }

    // ---- Manifeste ----

    private const string DesktopManifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
                 xmlns:desktop="http://schemas.microsoft.com/appx/manifest/desktop/windows10">
          <Identity Name="AppUp.IntelArcSoftware" Publisher="CN=X" Version="26.36.2702.0" />
          <Properties>
            <DisplayName>Intel® Graphics Software</DisplayName>
            <PublisherDisplayName>INTEL CORP</PublisherDisplayName>
          </Properties>
          <Applications>
            <Application Id="App" Executable="App\Principal.exe" EntryPoint="Windows.FullTrustApplication">
              <Extensions>
                <uap:Extension Category="windows.protocol" />
                <desktop:Extension Category="windows.startupTask" Executable="VFS\Intel\IntelGraphicsSoftware.exe" EntryPoint="Windows.FullTrustApplication">
                  <desktop:StartupTask TaskId="IntelGraphicsSoftwareStartup" Enabled="true" DisplayName="Intel® Graphics Software" />
                </desktop:Extension>
              </Extensions>
            </Application>
          </Applications>
        </Package>
        """;

    private const string ResourceManifest = """
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
                 xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5">
          <Identity Name="Microsoft.WindowsTerminal" />
          <Properties>
            <DisplayName>ms-resource:AppStoreName</DisplayName>
            <PublisherDisplayName>Microsoft Corporation</PublisherDisplayName>
          </Properties>
          <Applications>
            <Application Id="App" Executable="WindowsTerminal.exe">
              <Extensions>
                <uap5:Extension Category="windows.startupTask">
                  <uap5:StartupTask TaskId="StartTerminalOnLoginTask" Enabled="false" DisplayName="ms-resource:AppName" />
                </uap5:Extension>
              </Extensions>
            </Application>
          </Applications>
        </Package>
        """;

    [Fact]
    public void TheManifestGivesTheNameThePublisherAndTheExecutableOfTheTask()
    {
        var info = StoreStartupTasks.Declared(XDocument.Parse(DesktopManifest), "intelgraphicssoftwarestartup");

        Assert.NotNull(info);
        Assert.Equal("AppUp.IntelArcSoftware", info.PackageName);
        Assert.Equal("Intel® Graphics Software", info.DisplayName);
        Assert.Equal("INTEL CORP", info.Publisher);
        Assert.Equal(@"VFS\Intel\IntelGraphicsSoftware.exe", info.Executable);
    }

    [Fact]
    public void WithoutItsOwnExecutableTheTaskRunsTheApplication()
    {
        var info = StoreStartupTasks.Declared(XDocument.Parse(ResourceManifest), "StartTerminalOnLoginTask");

        Assert.NotNull(info);
        Assert.Equal("ms-resource:AppName", info.DisplayName);
        Assert.Equal("ms-resource:AppStoreName", info.PackageDisplayName);
        Assert.Equal("WindowsTerminal.exe", info.Executable);
    }

    [Fact]
    public void ATaskTheManifestDoesNotDeclareIsNotAStartupTask()
    {
        Assert.Null(StoreStartupTasks.Declared(XDocument.Parse(DesktopManifest), "PersistedTitleBarData"));
        Assert.Null(StoreStartupTasks.Declared(XDocument.Parse("<Package />"), TaskId));
    }

    [Fact]
    public void ResourceNamesAreLookedUpInThePackage()
    {
        const string full = "Microsoft.WindowsTerminal_1.25.3131.0_x64__8wekyb3d8bbwe", name = "Microsoft.WindowsTerminal";

        Assert.Equal(
            [$"@{{{full}?ms-resource://{name}/Resources/AppName}}", $"@{{{full}?ms-resource://{name}/AppName}}"],
            StoreStartupTasks.ResourceReferences(full, name, "ms-resource:AppName"));
        Assert.Equal([$"@{{{full}?ms-resource://{name}/Fichier/AppName}}"],
            StoreStartupTasks.ResourceReferences(full, name, "ms-resource:/Fichier/AppName"));
        Assert.Equal([$"@{{{full}?ms-resource://Autre/Resources/AppName}}"],
            StoreStartupTasks.ResourceReferences(full, name, "ms-resource://Autre/Resources/AppName"));
        Assert.Empty(StoreStartupTasks.ResourceReferences(full, name, "Terminal"));
        Assert.Empty(StoreStartupTasks.ResourceReferences(full, name, "ms-resource:"));
    }

    // ---- Dans la liste Démarrage ----

    [Fact]
    public void AStoreTaskIsOfferedLikeAnyOtherStartupProgram()
    {
        var entry = new StartupEntry(StartupSource.StoreTask, Name, "", "Intel® Graphics Software", Enabled: true, "INTEL CORP");
        var setting = new StartupAppSetting(entry);

        Assert.False(entry.IsProtected);
        Assert.Equal("startup.store.AppUp.IntelArcSoftware_8j3eq9eme6ctt!IntelGraphicsSoftwareStartup", setting.Id);
        Assert.Equal("Intel® Graphics Software", setting.DisplayName);
        Assert.Equal("INTEL CORP · votre compte", setting.ComputeTarget(new HardwareProfile()).Reason);
        Assert.False(setting.SelectedByDefault);
    }

    [Fact]
    public void ScanningTheRealTasksOnlyReads()
    {
        var entries = StoreStartupTasks.Scan();

        Assert.All(entries, entry =>
        {
            Assert.Equal(StartupSource.StoreTask, entry.Source);
            Assert.NotNull(StoreStartupTasks.Split(entry.Name));
            Assert.False(string.IsNullOrWhiteSpace(entry.DisplayName));
            Assert.Equal(entry.Enabled, StoreStartupTasks.IsEnabled(entry.Name));
        });
    }

    // ---- Lanceur Squirrel (Discord) ----

    [Theory]
    [InlineData(@"""C:\Users\u\AppData\Local\Discord\Update.exe"" --processStart Discord.exe", "Discord.exe")]
    [InlineData(@"C:\Users\u\AppData\Local\Discord\Update.exe --processStart Discord.exe", "Discord.exe")]
    [InlineData(@"""C:\Users\u\AppData\Local\Outil\Update.exe"" --processStart ""Mon Outil.exe"" --process-start-args ""--minimise""", "Mon Outil.exe")]
    [InlineData(@"""C:\Users\u\AppData\Local\Outil\Update.exe"" --processStartAndWait Outil.exe", "Outil.exe")]
    [InlineData(@"""C:\Users\u\AppData\Local\Outil\update.exe"" --processstart=Outil.exe", "Outil.exe")]
    [InlineData(@"""C:\Users\u\AppData\Local\Outil\Update.exe"" --processStart ..\Ailleurs\Outil.exe", null)]
    [InlineData(@"""C:\Users\u\AppData\Local\Outil\Update.exe"" --checkForUpdate", null)]
    [InlineData(@"""C:\Users\u\AppData\Local\Outil\Update.exe"" --processStart", null)]
    [InlineData(@"""C:\Program Files (x86)\Steam\steam.exe"" -silent", null)]
    [InlineData(@"""C:\Program Files\Outil\Outil.exe"" --processStart Autre.exe", null)]
    public void TheProgramBehindASquirrelLauncherIsFound(string command, string? expected)
    {
        Assert.Equal(expected, StartupApps.SquirrelProcessName(command));
    }

    [Fact]
    public void TheNewestVersionBehindTheLauncherIsTheOneDescribed()
    {
        foreach (var version in new[] { "app-1.0.9", "app-1.0.10", "app-2.0.0", "packages" })
            Directory.CreateDirectory(Path.Combine(_folder, version));
        File.WriteAllText(Path.Combine(_folder, "Update.exe"), "");
        File.WriteAllText(Path.Combine(_folder, "app-1.0.9", "Outil.exe"), "");
        File.WriteAllText(Path.Combine(_folder, "app-1.0.10", "Outil.exe"), "");   // « app-2.0.0 » n'a pas encore le programme

        var target = StartupApps.SquirrelTarget($@"""{_folder}\Update.exe"" --processStart Outil.exe");

        Assert.Equal(Path.Combine(_folder, "app-1.0.10", "Outil.exe"), target);
    }

    [Fact]
    public void ALauncherWithoutItsProgramGivesNothing()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "app-1.0.0"));
        File.WriteAllText(Path.Combine(_folder, "Update.exe"), "");

        Assert.Null(StartupApps.SquirrelTarget($@"""{_folder}\Update.exe"" --processStart Outil.exe"));
        Assert.Null(StartupApps.SquirrelTarget($@"""{_folder}\Absent\Update.exe"" --processStart Outil.exe"));
        Assert.Null(StartupApps.SquirrelTarget($@"""{_folder}\Update.exe"" --checkForUpdate"));
    }
}
