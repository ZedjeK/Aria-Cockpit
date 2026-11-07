using Aria.Core.Diagnostics.Checks;
using Aria.Core.Models;
using static Aria.Core.Inventory.SecurityCollector;

namespace Aria.Tests;

/// <summary>Contrôles de sécurité du diagnostic : pare-feu Windows et dernier correctif. Repris de CTI-Cockpit.</summary>
public class SecurityPostureTests
{
    // ---- Pare-feu ----

    [Theory]
    [InlineData(null, null, true)]   // aucune valeur : actif par défaut
    [InlineData(null, 1, true)]
    [InlineData(null, 0, false)]
    [InlineData(1, 0, true)]         // la stratégie de groupe l'emporte sur le réglage local
    [InlineData(0, 1, false)]
    public void FirewallStateFollowsPolicyThenLocal(int? policy, int? local, bool expected)
    {
        Assert.Equal(expected, IsFirewallOn(policy, local));
    }

    private static readonly Dictionary<string, bool> AllOn = new() { ["Domaine"] = true, ["Privé"] = true, ["Public"] = true };

    [Fact]
    public void FirewallOnEveryProfileIsCompliant()
    {
        Assert.Equal(Severity.Ok, FirewallCheck.Evaluate(AllOn, null)!.Severity);
    }

    [Fact]
    public void FirewallOffIsAWarningUnlessAnotherProductProtects()
    {
        var publicOff = new Dictionary<string, bool>(AllOn) { ["Public"] = false };

        var alone = FirewallCheck.Evaluate(publicOff, null)!;
        Assert.Equal(Severity.Warning, alone.Severity);
        Assert.Contains("public", alone.Title);

        Assert.Equal(Severity.Info, FirewallCheck.Evaluate(publicOff, "Bitdefender Firewall")!.Severity);
    }

    [Fact]
    public void UnreadableFirewallGivesNoFinding()
    {
        Assert.Null(FirewallCheck.Evaluate(new Dictionary<string, bool>(), null));
    }

    // ---- Dernier correctif ----

    private static HistoryEntry Entry(string date, string title, int result = 2, params string[] categories) =>
        new(1, result, DateTime.Parse(date), title, categories);

    [Fact]
    public void DefenderStoreAppsAndDriversAreNotPatches()
    {
        // Relevé réel d'un Windows en français : « KB » y devient « Ko »
        var entries = new[]
        {
            Entry("2026-11-03 08:30", "Mise à jour de la sélection disjointe pour Microsoft Defender Antivirus – 2267602 Ko (version 1.463.102.0)"),
            Entry("2026-10-31 22:06", "9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2"),
            Entry("2026-10-30 10:00", "NVIDIA - Display - 32.0.15.9186"),
            Entry("2026-10-29 02:43", "2026-10 Mise à jour de la préversion (KB5127936) (26300.9818)"),
            Entry("2026-10-15 03:00", "2026-10 Mise à jour cumulative (KB5127001)")
        };

        Assert.Equal(DateTime.Parse("2026-10-29 02:43"), LastSystemUpdate(entries));
    }

    [Fact]
    public void FailedInstallationsAndDriverCategoryAreIgnored()
    {
        var entries = new[]
        {
            Entry("2026-11-02 10:00", "Mise à jour cumulative (KB5130000)", result: 4),
            Entry("2026-11-01 10:00", "Pilote Intel (KB5129999)", 2, "EBFC1FC5-71A4-4F7B-9ACA-3B9A503104A0"),
            Entry("2026-10-14 10:00", "Security Update (KB5127001)")
        };

        Assert.Equal(DateTime.Parse("2026-10-14 10:00"), LastSystemUpdate(entries));
    }

    [Fact]
    public void NoPatchAtAllGivesNoDate()
    {
        Assert.Null(LastSystemUpdate([Entry("2026-11-03 08:30", "Defender – 2267602 Ko (version 1.463)")]));
    }

    [Theory]
    [InlineData("2026-10-29", Severity.Ok)]
    [InlineData("2026-09-19", Severity.Ok)]        // 45 jours : encore à jour
    [InlineData("2026-09-18", Severity.Warning)]   // 46 jours : un mois de correctifs manqué
    public void PatchAgeThreshold(string lastInstalled, Severity expected)
    {
        Assert.Equal(expected, WindowsPatchCheck.Evaluate(DateTime.Parse(lastInstalled), DateTime.Parse("2026-11-03")).Severity);
    }

    [Fact]
    public void UnknownPatchDateIsInformation()
    {
        Assert.Equal(Severity.Info, WindowsPatchCheck.Evaluate(null, DateTime.Parse("2026-11-03")).Severity);
    }
}
