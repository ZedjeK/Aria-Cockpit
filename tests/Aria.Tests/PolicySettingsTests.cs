using Aria.Core.Models;
using Aria.Core.Privacy;
using Aria.Core.Settings;
using Aria.Core.Windows;

namespace Aria.Tests;

/// <summary>
/// Réglages faits d'une stratégie documentée par Microsoft : pilotes livrés par Windows Update, envoi de la frappe,
/// mises en avant et contenus en ligne de la recherche, assistance à distance, échange de mises à jour entre PC.
/// Seules la cible, la description et la place dans les listes sont vérifiées : rien n'est lu ni écrit dans le registre.
/// </summary>
public class PolicySettingsTests
{
    private static readonly HardwareProfile Hw = new();

    public static TheoryData<ISetting, string, string, string, bool, SettingCategory> Settings => new()
    {
        { new WindowsUpdateDriversSetting(), "windows.wudrivers", "1", "Exclus", false, SettingCategory.Windows },
        { new TypingDataSetting(), "privacy.typingdata", "0", "Refusé", true, SettingCategory.Privacy },
        { new SearchHighlightsSetting(), "privacy.searchhighlights", "0", "Masquées", true, SettingCategory.Privacy },
        { new CloudSearchSetting(), "privacy.cloudsearch", "0", "Désactivée", true, SettingCategory.Privacy },
        { new RemoteAssistanceSetting(), "privacy.remoteassistance", "0", "Interdite", true, SettingCategory.Privacy },
        { new DeliveryOptimizationSetting(), "privacy.deliveryoptimization", "0", "Aucun échange (serveurs seulement)", false, SettingCategory.Privacy },
    };

    [Theory]
    [MemberData(nameof(Settings))]
    public void EachSettingHasItsTargetAndItsPlace(ISetting setting, string id, string raw, string display, bool selected, SettingCategory category)
    {
        var target = setting.ComputeTarget(Hw);

        Assert.Equal(id, setting.Id);
        Assert.Equal(raw, target.Value.Raw);
        Assert.Equal(display, target.Value.Display);
        Assert.Equal(selected, setting.SelectedByDefault);
        Assert.Equal(category, setting.Category);
        Assert.False(setting.RequiresReboot);
        Assert.Null(setting.WhyNotApplicable(Hw));
        Assert.False(string.IsNullOrWhiteSpace(target.Reason));
        Assert.True(setting.IsAtTarget(target.Value, target.Value));
        Assert.False(setting.IsAtTarget(new SettingValue("absent", "Par défaut de Windows"), target.Value));
    }

    [Fact]
    public void TheDriverSettingSaysWhatIsGivenUp()
    {
        var reason = new WindowsUpdateDriversSetting().ComputeTarget(Hw).Reason;

        Assert.Contains("carte graphique", reason);
        Assert.Contains("Contrepartie", reason);
    }

    [Fact]
    public void TheNewSettingsAreInTheCatalogOnce()
    {
        var ids = SettingsCatalog.Build(Hw).Select(s => s.Id).ToList();
        string[] added =
        [
            "windows.wudrivers", "privacy.typingdata", "privacy.searchhighlights", "privacy.cloudsearch",
            "privacy.remoteassistance", "privacy.deliveryoptimization"
        ];

        foreach (var id in added)
            Assert.Equal(1, ids.Count(existing => existing == id));
    }

    [Fact]
    public void TheSearchSettingsFollowTheBingSearchOne()
    {
        var ids = PrivacySettings.Build(Hw).Select(s => s.Id).ToList();
        var at = ids.IndexOf("privacy.startwebsearch");

        Assert.Equal(["privacy.searchhighlights", "privacy.cloudsearch"], ids.Skip(at + 1).Take(2));
        Assert.Equal(ids.IndexOf("privacy.feedback") + 1, ids.IndexOf("privacy.typingdata"));
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }
}
