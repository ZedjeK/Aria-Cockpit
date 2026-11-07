using System.Management;
using System.Text.RegularExpressions;
using Aria.Core.Models;

namespace Aria.Core.Battery;

/// <summary>Mode de charge du BIOS Dell (attribut PrimaryBattChargeCfg et ses seuils).</summary>
/// <param name="Mode">Standard, Express, Adaptive, Auto, PrimAcUse ou Custom.</param>
/// <param name="Start">Seuil de reprise de la charge, en % (pris en compte en mode Custom).</param>
/// <param name="Stop">Seuil d'arrêt de la charge, en % (pris en compte en mode Custom).</param>
internal sealed record DellChargeMode(string Mode, int Start, int Stop)
{
    public const string Custom = "Custom";
    public const string PrimaryAcUse = "PrimAcUse";

    /// <summary>Au-delà, la batterie reste trop longtemps presque pleine pour que la limite serve vraiment.</summary>
    public const int MaxProtectiveStop = 85;

    /// <summary>Recommandation : la charge s'arrête à 80 % et ne reprend que sous 50 %.</summary>
    public static readonly DellChargeMode Recommended = new(Custom, 50, 80);

    public bool IsCustom => string.Equals(Mode, Custom, StringComparison.OrdinalIgnoreCase);

    /// <summary>La batterie ne reste pas chargée à 100 % : seuil d'arrêt bas, ou mode « principalement sur secteur ».</summary>
    public bool Protects =>
        (IsCustom && Stop <= MaxProtectiveStop) || string.Equals(Mode, PrimaryAcUse, StringComparison.OrdinalIgnoreCase);

    /// <summary>Valeur sauvegardée : « Custom:50-80 », ou le nom du mode seul (les seuils n'y servent pas).</summary>
    public string Raw => IsCustom ? $"{Custom}:{Start}-{Stop}" : Mode;

    public string Describe => Mode.ToLowerInvariant() switch
    {
        "custom" => $"Personnalisée, de {Start} à {Stop} %",
        "primacuse" => "Principalement sur secteur",
        "standard" => "Standard, jusqu'à 100 %",
        "express" => "Charge rapide, jusqu'à 100 %",
        "adaptive" => "Adaptative (le BIOS ajuste selon l'usage)",
        "auto" => "Automatique (le BIOS ajuste selon l'usage)",
        _ => Mode
    };

    public ChargeLimitInfo ToInfo() => new(ChargeLimitVendor.Dell, Raw, Describe, Protects);

    /// <summary>Relit une valeur sauvegardée (<see cref="Raw"/>).</summary>
    public static DellChargeMode Parse(string raw)
    {
        var match = Regex.Match(raw.Trim(), @"^Custom:(?<start>\d{1,3})-(?<stop>\d{1,3})$", RegexOptions.IgnoreCase);
        if (match.Success)
            return new(Custom, int.Parse(match.Groups["start"].Value), int.Parse(match.Groups["stop"].Value));
        if (raw.Trim().Length == 0 || raw.Contains(':'))
            throw new FormatException($"Mode de charge illisible : « {raw} ».");
        return new(raw.Trim(), 0, 0);
    }
}

/// <summary>
/// Limite de charge des portables Dell, par l'interface WMI intégrée au BIOS des modèles professionnels sortis depuis
/// 2018 (aucun outil Dell à installer). Mêmes appels que les scripts publiés par Dell (Endpoint-Management-Script-Library) :
/// classes EnumerationAttribute, IntegerAttribute et BIOSAttributeInterface. Repris de CTI-Cockpit.
/// </summary>
internal sealed class DellChargeLimit : IChargeLimitControl
{
    private const string AttributesScope = @"root\dcim\sysman\biosattributes";
    private const string SecurityScope = @"root\dcim\sysman\wmisecurity";

    internal const string ModeAttribute = "PrimaryBattChargeCfg";
    internal const string StartAttribute = "CustomChargeStart";
    internal const string StopAttribute = "CustomChargeStop";

    /// <summary>Écart minimal imposé par Dell entre la reprise et l'arrêt de la charge.</summary>
    internal const int MinGap = 5;

    public ChargeLimitVendor Vendor => ChargeLimitVendor.Dell;
    public bool ReappliesAtLogon => false;   // écrit dans le BIOS : la limite reste après un redémarrage

    public ChargeLimitInfo? Read() => ReadMode()?.ToInfo();

    public ChargeLimitInfo Recommended() => DellChargeMode.Recommended.ToInfo();

    public ChargeLimitInfo InfoFor(string raw) => DellChargeMode.Parse(raw).ToInfo();

    public string? Blocker() =>
        IsAdminPasswordSet() == true ? "Mot de passe administrateur du BIOS posé : réglage à faire dans le BIOS (F2)" : null;

    public void Write(ChargeLimitInfo current, string raw)
    {
        var from = ReadMode() ?? throw new InvalidOperationException("Limite de charge illisible dans le BIOS Dell.");
        var target = DellChargeMode.Parse(raw);
        using var searcher = new ManagementObjectSearcher(AttributesScope, "SELECT * FROM BIOSAttributeInterface");
        var bios = searcher.Get().Cast<ManagementObject>().FirstOrDefault()
                   ?? throw new InvalidOperationException("Interface de réglage du BIOS Dell introuvable.");
        foreach (var (name, value) in WriteSteps(from, target))
        {
            using var input = bios.GetMethodParameters("SetAttribute");
            input["SecType"] = 0u;           // aucun mot de passe BIOS
            input["SecHndCount"] = 0u;
            input["SecHandle"] = Array.Empty<byte>();
            input["AttributeName"] = name;
            input["AttributeValue"] = value;
            using var output = bios.InvokeMethod("SetAttribute", input, null);
            var status = Convert.ToInt64(output["Status"]);
            if (status != 0)
                throw new InvalidOperationException($"Le BIOS Dell a refusé {name} = {value} (code {status}).");
        }
    }

    /// <summary>Mode de charge actuel, ou null si ce BIOS ne l'expose pas à Windows (modèle ancien ou grand public).</summary>
    private static DellChargeMode? ReadMode()
    {
        try
        {
            var mode = Attribute("EnumerationAttribute", ModeAttribute);
            if (mode is null) return null;
            return new DellChargeMode(mode, ToInt(Attribute("IntegerAttribute", StartAttribute)), ToInt(Attribute("IntegerAttribute", StopAttribute)));
        }
        catch (ManagementException) { return null; }   // espace WMI Dell absent
    }

    /// <summary>Mot de passe administrateur du BIOS posé ? null si illisible.</summary>
    private static bool? IsAdminPasswordSet()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(SecurityScope, "SELECT NameId, IsPasswordSet FROM PasswordObject");
            foreach (ManagementBaseObject password in searcher.Get())
                if (string.Equals(password["NameId"]?.ToString(), "Admin", StringComparison.OrdinalIgnoreCase))
                    return Convert.ToInt64(password["IsPasswordSet"]) == 1;
            return null;
        }
        catch (ManagementException) { return null; }
    }

    /// <summary>
    /// Attributs à écrire, un par un. Le BIOS vérifie « reprise ≤ arrêt − 5 » à chaque écriture : on baisse d'abord
    /// la reprise si c'est possible avec l'arrêt actuel, sinon on règle d'abord l'arrêt. Le mode passe en dernier,
    /// une fois les seuils en place. Les valeurs déjà bonnes ne sont pas réécrites.
    /// </summary>
    internal static IReadOnlyList<(string Name, string Value)> WriteSteps(DellChargeMode current, DellChargeMode target)
    {
        var steps = new List<(string, string)>();
        if (target.IsCustom)
        {
            Validate(target);
            var start = (StartAttribute, target.Start, current.Start);
            var stop = (StopAttribute, target.Stop, current.Stop);
            var order = target.Start <= current.Stop - MinGap ? new[] { start, stop } : [stop, start];
            steps.AddRange(order.Where(s => s.Item2 != s.Item3).Select(s => (s.Item1, s.Item2.ToString())));
        }
        if (!string.Equals(current.Mode, target.Mode, StringComparison.OrdinalIgnoreCase))
            steps.Add((ModeAttribute, target.Mode));
        return steps;
    }

    /// <summary>Plages admises par Dell : reprise de 50 à 95 %, arrêt de 55 à 100 %, au moins 5 points d'écart.</summary>
    internal static void Validate(DellChargeMode target)
    {
        if (target.Start is < 50 or > 95 || target.Stop is < 55 or > 100 || target.Start > target.Stop - MinGap)
            throw new ArgumentOutOfRangeException(nameof(target),
                $"Seuils refusés : reprise {target.Start} %, arrêt {target.Stop} % (reprise 50 à 95 %, arrêt 55 à 100 %, 5 points d'écart).");
    }

    private static string? Attribute(string className, string name)
    {
        using var searcher = new ManagementObjectSearcher(AttributesScope, $"SELECT AttributeName, CurrentValue FROM {className}");
        foreach (ManagementBaseObject attribute in searcher.Get())
        {
            if (!string.Equals(attribute["AttributeName"]?.ToString(), name, StringComparison.Ordinal)) continue;
            // CurrentValue est un tableau d'une valeur chez Dell
            return attribute["CurrentValue"] switch
            {
                Array { Length: > 0 } values => values.GetValue(0)?.ToString(),
                null => null,
                var value => value.ToString()
            };
        }
        return null;
    }

    private static int ToInt(string? value) => int.TryParse(value, out var number) ? number : 0;
}
