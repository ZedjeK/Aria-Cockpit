using System.Text.RegularExpressions;
using Microsoft.Win32;
using Aria.Core.Models;
using Aria.Core.Updates;

namespace Aria.Core.Inventory;

/// <summary>
/// Pare-feu Windows et dernier correctif installé : lecture seule, pour le diagnostic. Les règles de décision sont
/// séparées de la lecture pour être testables. Repris de CTI-Cockpit.
/// </summary>
public static partial class SecurityCollector
{
    internal static SecurityInfo Collect(List<string> warnings) =>
        new(ReadFirewall(), ReadThirdPartyFirewall(), ReadLastUpdate(warnings));

    // ---------------------------------------------------------------- Pare-feu

    /// <summary>Nom affiché → (clé de la stratégie de groupe, clé locale).</summary>
    private static readonly (string Name, string Policy, string Local)[] FirewallProfiles =
    [
        ("Domaine", "DomainProfile", "DomainProfile"),
        ("Privé", "PrivateProfile", "StandardProfile"),
        ("Public", "PublicProfile", "PublicProfile")
    ];

    private const string FirewallPolicyKey = @"SOFTWARE\Policies\Microsoft\WindowsFirewall";
    private const string FirewallLocalKey = @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";

    private static IReadOnlyDictionary<string, bool> ReadFirewall()
    {
        var result = new Dictionary<string, bool>();
        foreach (var (name, policy, local) in FirewallProfiles)
            result[name] = IsFirewallOn(
                Dword(Registry.LocalMachine, $@"{FirewallPolicyKey}\{policy}", "EnableFirewall"),
                Dword(Registry.LocalMachine, $@"{FirewallLocalKey}\{local}", "EnableFirewall"));
        return result;
    }

    /// <summary>La stratégie de groupe l'emporte sur le réglage local ; sans valeur, le pare-feu est actif par défaut.</summary>
    internal static bool IsFirewallOn(int? policy, int? local) => (policy ?? local ?? 1) != 0;

    /// <summary>Pare-feu d'un autre éditeur déclaré au Centre de sécurité (Windows coupe alors le sien).</summary>
    private static string? ReadThirdPartyFirewall()
    {
        try
        {
            return Wmi.Query("SELECT displayName FROM FirewallProduct", @"root\SecurityCenter2")
                .Select(p => Wmi.Str(p, "displayName"))
                .FirstOrDefault(n => n.Length > 0);
        }
        catch (Exception)
        {
            return null;   // Centre de sécurité absent ou illisible : rien à signaler
        }
    }

    // ---------------------------------------------------------------- Dernier correctif

    /// <summary>Une ligne de l'historique de Windows Update.</summary>
    /// <param name="Categories">Identifiants des classifications (indépendants de la langue).</param>
    internal sealed record HistoryEntry(int Operation, int ResultCode, DateTime Date, string Title, IReadOnlyList<string> Categories);

    private const int Installation = 1;
    private const int Succeeded = 2;
    private const int SucceededWithErrors = 3;

    /// <summary>Définitions Microsoft Defender (quotidiennes) et plateforme antivirus : ne sont pas des correctifs de Windows.</summary>
    private static readonly HashSet<string> DefenderKbs = ["2267602", "4052623", "2311978", "915597"];

    /// <summary>
    /// Numéro d'article : « KB5127936 », ou « 2267602 Ko » sur un Windows en français (« KB » y est traduit).
    /// Les applications du Microsoft Store et les pilotes n'en ont pas.
    /// </summary>
    [GeneratedRegex(@"\bKB\s?(?<kb>\d{6,8})\b|\b(?<kb>\d{6,8})\s?Ko\b", RegexOptions.IgnoreCase)]
    private static partial Regex KbNumber();

    private static DateTime? ReadLastUpdate(List<string> warnings)
    {
        try
        {
            var session = Com.Create("Microsoft.Update.Session");
            var searcher = Com.Call(session, "CreateUpdateSearcher")!;
            var total = Convert.ToInt32(Com.Call(searcher, "GetTotalHistoryCount"));
            if (total == 0) return null;
            var history = Com.Call(searcher, "QueryHistory", 0, Math.Min(total, 300));
            var entries = Com.Items(history).Select(e => new HistoryEntry(
                Convert.ToInt32(Com.Get(e, "Operation")),
                Convert.ToInt32(Com.Get(e, "ResultCode")),
                Com.Get(e, "Date") is DateTime date ? DateTime.SpecifyKind(date, DateTimeKind.Utc).ToLocalTime() : DateTime.MinValue,
                Com.Str(e, "Title"),
                Categories(e)));
            return LastSystemUpdate(entries);
        }
        catch (Exception e)
        {
            warnings.Add($"Historique de Windows Update illisible : {e.Message}");
            return null;
        }
    }

    private static IReadOnlyList<string> Categories(object entry)
    {
        try
        {
            return Com.Items(Com.Get(entry, "Categories")).Select(c => Com.Str(c, "CategoryID")).ToList();
        }
        catch (Exception)
        {
            return [];   // ancienne version de l'agent : le filtre sur le titre suffit
        }
    }

    /// <summary>
    /// Dernière installation réussie d'un correctif : une mise à jour qui porte un numéro d'article (KB), hors définitions
    /// et plateforme Microsoft Defender, hors pilotes et applications du Microsoft Store.
    /// </summary>
    internal static DateTime? LastSystemUpdate(IEnumerable<HistoryEntry> entries) =>
        entries
            .Where(e => e.Operation == Installation && e.ResultCode is Succeeded or SucceededWithErrors && e.Date > DateTime.MinValue)
            .Where(e => !e.Categories.Any(c => c.Equals(WindowsUpdateService.DefinitionUpdates, StringComparison.OrdinalIgnoreCase)
                                               || c.Equals(WindowsUpdateService.Drivers, StringComparison.OrdinalIgnoreCase)))
            .Where(e => KbNumber().Match(e.Title) is { Success: true } kb && !DefenderKbs.Contains(kb.Groups["kb"].Value))
            .Select(e => (DateTime?)e.Date)
            .Max();

    // ---------------------------------------------------------------- Registre

    private static int? Dword(RegistryKey hive, string path, string name)
    {
        try
        {
            using var key = hive.OpenSubKey(path);
            return key?.GetValue(name) is int value ? value : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
