using Aria.Core.Models;
using Aria.Core.Privacy;
using Microsoft.Win32;

namespace Aria.Tests;

/// <summary>
/// Réglages de confidentialité faits de plusieurs valeurs du registre : lecture, cible, application, restauration, et les
/// réglages contre les applications installées en silence, les écrans de configuration et les rappels de compte.
/// Les écritures se font dans une clé d'essai de l'utilisateur, supprimée à la fin : aucun vrai réglage n'est touché.
/// </summary>
public sealed class PrivacySettingsTests : IDisposable
{
    private const string TestParent = @"Software\Aria-Cockpit-Tests";
    private static readonly string TestRoot = TestParent + @"\" + Guid.NewGuid().ToString("N");
    private static readonly HardwareProfile Hw = new();

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(TestRoot, throwOnMissingSubKey: false);
        // La clé parente ne reste pas derrière les tests, sauf si une autre série y travaille encore
        bool empty;
        using (var parent = Registry.CurrentUser.OpenSubKey(TestParent)) empty = parent is { SubKeyCount: 0, ValueCount: 0 };
        if (empty) Registry.CurrentUser.DeleteSubKey(TestParent, throwOnMissingSubKey: false);
    }

    /// <summary>Deux valeurs d'essai, actives quand elles sont absentes, à mettre à 0.</summary>
    private sealed class Probe : UserRegistryValuesSetting
    {
        public override string Id => "test.probe";
        public override string DisplayName => "Essai";
        protected override string OffText => "Désactivés";
        protected override string OnText => "Activés";
        protected override string Reason => "essai";
        protected override IReadOnlyList<(string Path, string Name, int Missing, int Wanted)> Values =>
            [(TestRoot + @"\A", "Un", 1, 0), (TestRoot + @"\B", "Deux", 1, 0)];
    }

    private static int? Stored(string sub, string name)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{TestRoot}\{sub}");
        return key?.GetValue(name) as int?;
    }

    [Fact]
    public void AbsentValuesReadAsTheWindowsDefault()
    {
        var current = new Probe().ReadCurrent(Hw);

        Assert.Equal("absent;absent", current.Raw);
        Assert.Equal("Activés", current.Display);
    }

    [Fact]
    public void ApplyingTheTargetWritesEveryValueAndIsThenAtTarget()
    {
        var setting = new Probe();
        var target = setting.ComputeTarget(Hw);
        Assert.False(setting.IsAtTarget(setting.ReadCurrent(Hw), target.Value));

        setting.Apply(Hw, target.Value);

        Assert.Equal(0, Stored("A", "Un"));
        Assert.Equal(0, Stored("B", "Deux"));
        Assert.Equal("Désactivés", setting.ReadCurrent(Hw).Display);
        Assert.True(setting.IsAtTarget(setting.ReadCurrent(Hw), target.Value));
    }

    [Fact]
    public void RestoringPutsBackExactlyWhatWasSaved()
    {
        var setting = new Probe();
        var before = setting.ReadCurrent(Hw);                       // « absent;absent »
        setting.Apply(Hw, setting.ComputeTarget(Hw).Value);

        setting.Apply(Hw, before);

        Assert.Null(Stored("A", "Un"));                             // une valeur absente redevient absente, pas « 1 »
        Assert.Null(Stored("B", "Deux"));
        Assert.Equal("absent;absent", setting.ReadCurrent(Hw).Raw);
    }

    [Fact]
    public void APartlyAppliedSettingSaysSo()
    {
        var setting = new Probe();
        setting.Apply(Hw, new SettingValue("0;1", ""));

        var current = setting.ReadCurrent(Hw);

        Assert.Equal("En partie (1/2 désactivés)", current.Display);
        Assert.False(setting.IsAtTarget(current, setting.ComputeTarget(Hw).Value));
    }

    [Fact]
    public void AValueAbsentOrExplicitlyAtItsDefaultIsTheSameState()
    {
        var setting = new Probe();

        Assert.True(setting.IsAtTarget(new SettingValue("absent;1", ""), new SettingValue("1;absent", "")));
    }

    [Fact]
    public void ASavedValueOfTheWrongShapeIsRefused() =>
        Assert.Throws<FormatException>(() => new Probe().Apply(Hw, new SettingValue("0", "")));

    [Fact]
    public void TheSuggestionsSettingKeepsItsEightValuesSoOldBackupsStillRestore()
    {
        var target = new WindowsSuggestionsSetting().ComputeTarget(Hw).Value;

        Assert.Equal("0;0;0;0;0;0;0;1", target.Raw);
        Assert.Equal("Désactivées", target.Display);
        Assert.Equal("privacy.suggestions", new WindowsSuggestionsSetting().Id);
    }

    [Fact]
    public void SetupPromptsAndAccountRemindersTurnTheirTwoValuesOff()
    {
        var prompts = new SetupPromptsSetting();
        var reminders = new AccountRemindersSetting();

        Assert.Equal("0;0", prompts.ComputeTarget(Hw).Value.Raw);
        Assert.Equal("Désactivés", prompts.ComputeTarget(Hw).Value.Display);
        Assert.Equal("0;0", reminders.ComputeTarget(Hw).Value.Raw);
        Assert.Equal("privacy.setupprompts", prompts.Id);
        Assert.Equal("privacy.accountreminders", reminders.Id);
        Assert.Equal(SettingCategory.Privacy, prompts.Category);
        Assert.True(prompts.SelectedByDefault && reminders.SelectedByDefault);
    }

    [Fact]
    public void SilentAppInstallsAreTurnedOff()
    {
        var setting = new SilentAppInstallSetting();
        var target = setting.ComputeTarget(Hw);

        Assert.Equal("0", target.Value.Raw);
        Assert.Equal("Désactivée", target.Value.Display);
        Assert.Equal("privacy.silentinstalls", setting.Id);
        Assert.Contains("Applications", target.Reason);
        Assert.True(setting.SelectedByDefault);
    }

    [Fact]
    public void TheNewSettingsFollowTheSuggestionsInThePrivacyList()
    {
        var ids = PrivacySettings.Build(Hw).Select(s => s.Id).ToList();
        var at = ids.IndexOf("privacy.suggestions");

        Assert.Equal(["privacy.setupprompts", "privacy.accountreminders", "privacy.silentinstalls"], ids.Skip(at + 1).Take(3));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
