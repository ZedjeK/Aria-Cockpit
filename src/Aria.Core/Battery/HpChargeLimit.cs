using System.Management;
using Aria.Core.Models;

namespace Aria.Core.Battery;

/// <summary>
/// Limite de charge des portables HP, par l'interface WMI officielle du BIOS (espace root\HP\InstrumentedBIOS,
/// classes HP_BIOSEnumeration et HP_BIOSSettingInterface) : réglage « Battery Health Manager », présent dans les
/// BIOS depuis fin 2018. Ses libellés varient selon le modèle et la version du BIOS (« Maximize My Battery Health »,
/// « Maximize Battery Health »…) : les choix possibles sont lus dans le BIOS lui-même, jamais écrits en dur.
/// </summary>
internal sealed class HpChargeLimit : IChargeLimitControl
{
    private const string Scope = @"root\HP\InstrumentedBIOS";
    internal const string SettingName = "Battery Health Manager";

    /// <summary>Codes de retour de SetBIOSSetting, d'après la définition MOF publiée par HP.</summary>
    private static string ReturnCode(uint code) => code switch
    {
        1 => "non pris en charge",
        2 => "erreur non précisée",
        3 => "délai dépassé",
        4 => "échec",
        5 => "paramètre invalide",
        6 => "accès refusé",
        _ => $"code {code}"
    };

    public ChargeLimitVendor Vendor => ChargeLimitVendor.Hp;
    public bool ReappliesAtLogon => false;   // écrit dans le BIOS

    public ChargeLimitInfo? Read() => ReadSetting() is { } s ? Info(s.Current) : null;

    public ChargeLimitInfo Recommended()
    {
        var setting = ReadSetting() ?? throw new InvalidOperationException("Réglage Battery Health Manager introuvable dans le BIOS HP.");
        var health = HealthValue(setting.Possible)
                     ?? throw new InvalidOperationException("Ce BIOS HP ne propose pas de mode qui ménage la batterie.");
        return Info(health);
    }

    public ChargeLimitInfo InfoFor(string raw) => Info(raw);

    public string? Blocker() =>
        IsSetupPasswordSet() == true ? "Mot de passe du BIOS posé : réglage à faire dans le BIOS (F10)" : null;

    public void Write(ChargeLimitInfo current, string raw)
    {
        var setting = ReadSetting() ?? throw new InvalidOperationException("Réglage Battery Health Manager introuvable dans le BIOS HP.");
        if (!setting.Possible.Contains(raw, StringComparer.Ordinal))
            throw new InvalidOperationException($"Valeur refusée par ce BIOS HP : « {raw} ».");

        using var searcher = new ManagementObjectSearcher(Scope, "SELECT * FROM HP_BIOSSettingInterface");
        var bios = searcher.Get().Cast<ManagementObject>().FirstOrDefault()
                   ?? throw new InvalidOperationException("Interface de réglage du BIOS HP introuvable.");

        // Sans mot de passe, le paramètre Password est omis ; certains BIOS exigent quand même le marqueur d'encodage
        var code = Set(bios, raw, password: null);
        if (code == 5) code = Set(bios, raw, password: "<utf-16/>");
        if (code != 0) throw new InvalidOperationException($"Le BIOS HP a refusé « {raw} » ({ReturnCode(code)}).");
    }

    private static uint Set(ManagementObject bios, string value, string? password)
    {
        using var input = bios.GetMethodParameters("SetBIOSSetting");
        input["Name"] = SettingName;
        input["Value"] = value;
        if (password is not null) input["Password"] = password;
        using var output = bios.InvokeMethod("SetBIOSSetting", input, null);
        return Convert.ToUInt32(output["Return"]);
    }

    internal static ChargeLimitInfo Info(string value) => new(ChargeLimitVendor.Hp, value, DescribeValue(value), IsHealth(value));

    /// <summary>Le mode qui ménage la batterie : son libellé contient « Health » quelle que soit la version du BIOS.</summary>
    internal static bool IsHealth(string value) => value.Contains("Health", StringComparison.OrdinalIgnoreCase);

    internal static string? HealthValue(IEnumerable<string> possible) => possible.FirstOrDefault(IsHealth);

    internal static string DescribeValue(string value) =>
        IsHealth(value) ? "Santé de la batterie (charge plafonnée par le BIOS)"
        : value.Contains("Duration", StringComparison.OrdinalIgnoreCase) ? "Autonomie maximale, jusqu'à 100 %"
        : value.Contains("Manage", StringComparison.OrdinalIgnoreCase) ? "Gérée par HP (charge adaptative)"
        : value;

    private sealed record Setting(string Current, IReadOnlyList<string> Possible);

    private static Setting? ReadSetting()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope,
                $"SELECT Name, CurrentValue, PossibleValues FROM HP_BIOSEnumeration WHERE Name = '{SettingName}'");
            foreach (ManagementBaseObject setting in searcher.Get())
            {
                var current = setting["CurrentValue"]?.ToString();
                var possible = setting["PossibleValues"] is string[] values ? values : [];
                if (!string.IsNullOrWhiteSpace(current)) return new Setting(current, possible);
            }
            return null;
        }
        catch (ManagementException) { return null; }   // espace WMI HP absent (portable grand public, autre fabricant)
    }

    /// <summary>Mot de passe de configuration du BIOS posé ? null si illisible.</summary>
    private static bool? IsSetupPasswordSet()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(Scope, "SELECT Name, IsSet FROM HP_BIOSPassword WHERE Name = 'Setup Password'");
            foreach (ManagementBaseObject password in searcher.Get())
                return Convert.ToInt64(password["IsSet"]) == 1;
            return null;
        }
        catch (ManagementException) { return null; }
    }
}
