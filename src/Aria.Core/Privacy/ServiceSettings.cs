using System.Diagnostics;
using Microsoft.Win32;
using Aria.Core.Models;
using Aria.Core.Settings;

namespace Aria.Core.Privacy;

/// <summary>
/// Mode de démarrage d'un service Windows. Valeur technique : « auto », « delayed », « manual » ou « disabled ».
/// Lu dans le registre (Start, DelayedAutostart), modifié par sc.exe, l'outil officiel ; un service désactivé est aussi arrêté.
/// </summary>
public class ServiceStartSetting(string serviceName, string displayName, bool selected, string reason,
    Func<HardwareProfile, string?>? whyNot = null) : SettingBase
{
    public const string Auto = "auto", Delayed = "delayed", Manual = "manual", Disabled = "disabled";

    public string ServiceName { get; } = serviceName;
    public override string Id => $"service.{ServiceName}";
    public override string DisplayName => displayName;
    public override SettingCategory Category => SettingCategory.Privacy;
    public override bool SelectedByDefault => selected;

    private string KeyPath => $@"SYSTEM\CurrentControlSet\Services\{ServiceName}";

    public override string? WhyNotApplicable(HardwareProfile hw)
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
        if (key?.GetValue("Start") is not int) return "Service absent de ce Windows";
        return whyNot?.Invoke(hw) ?? WhyNot(hw);
    }

    protected virtual string? WhyNot(HardwareProfile hw) => null;

    public override SettingValue ReadCurrent(HardwareProfile hw)
    {
        using var key = Registry.LocalMachine.OpenSubKey(KeyPath) ?? throw new InvalidOperationException("Service introuvable.");
        var start = key.GetValue("Start") as int? ?? 3;
        var delayed = key.GetValue("DelayedAutostart") as int? == 1;
        return Value(start switch
        {
            2 => delayed ? Delayed : Auto,
            4 => Disabled,
            _ => Manual // 3, et 0/1 réservés aux pilotes
        });
    }

    public override SettingTarget ComputeTarget(HardwareProfile hw) => new(Value(Disabled), reason);

    public override void Apply(HardwareProfile hw, SettingValue value)
    {
        var start = value.Raw switch
        {
            Auto => "auto",
            Delayed => "delayed-auto",
            Manual => "demand",
            Disabled => "disabled",
            _ => throw new FormatException($"Mode de démarrage inconnu : {value.Raw}")
        };
        var (code, output) = Sc($"config {ServiceName} start= {start}");
        if (code != 0) throw new InvalidOperationException($"sc.exe a refusé le changement (code {code}) : {output}".Trim());

        // Effet immédiat : arrêt si désactivé, démarrage si restauré en automatique (échecs sans gravité)
        if (value.Raw == Disabled) Sc($"stop {ServiceName}");
        else if (value.Raw is Auto or Delayed) Sc($"start {ServiceName}");
    }

    protected static SettingValue Value(string mode) => new(mode, mode switch
    {
        Auto => "Automatique",
        Delayed => "Automatique (différé)",
        Disabled => "Désactivé",
        _ => "Manuel"
    });

    private static (int Code, string Output) Sc(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("sc.exe introuvable.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(30_000);
        return (process.HasExited ? process.ExitCode : -1, string.Join(" ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
    }
}

/// <summary>Spouleur d'impression : proposé seulement si aucune imprimante réelle n'est installée.</summary>
public sealed class SpoolerSetting() : ServiceStartSetting("Spooler", "Spouleur d'impression", selected: true,
    "Aucune imprimante réelle installée : le spouleur reste une porte d'entrée connue pour des attaques (failles « PrintNightmare »). " +
    "Attention : « Imprimer en PDF » ne fonctionnera plus. À réactiver depuis Restauration si vous branchez une imprimante.")
{
    protected override string? WhyNot(HardwareProfile hw) => PrinterReason(hw.Peripherals.Printers);

    /// <summary>Raison de ne pas proposer le spouleur, ou null si aucune imprimante réelle n'est installée.</summary>
    internal static string? PrinterReason(IReadOnlyList<string>? printers) => printers switch
    {
        null => "Imprimantes illisibles : laissé tel quel par prudence",
        { Count: > 0 } => $"Imprimante installée : {string.Join(", ", printers)}",
        _ => null
    };
}

/// <summary>Services Xbox Live : proposés (décochés) seulement si ni l'application Xbox ni Game Pass ne sont installés.</summary>
public sealed class XboxServiceSetting(string name, string displayName) : ServiceStartSetting(name, displayName, selected: false,
    "Ni l'application Xbox ni Game Pass ne sont installés. Attention : certains jeux Steam liés à un compte Xbox (Forza, Halo…) en ont besoin.")
{
    protected override string? WhyNot(HardwareProfile hw) =>
        AppPackages.IsInstalled("Microsoft.GamingApp", "Microsoft.XboxApp") ? "Application Xbox installée : nécessaire au Game Pass" : null;
}
