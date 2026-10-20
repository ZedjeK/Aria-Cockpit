using Aria.Core.Models;
using Aria.Core.Settings;

namespace Aria.Core.Startup;

/// <summary>
/// Un programme lancé à l'ouverture de session. Valeur technique : « enabled » ou « disabled ».
/// Décoché par défaut : c'est l'utilisateur qui juge de ce dont il a besoin.
/// </summary>
public sealed class StartupAppSetting(StartupEntry entry) : SettingBase
{
    public const string Enabled = "enabled", Disabled = "disabled";

    public StartupEntry Entry { get; } = entry;

    public override string Id => $"startup.{SourceKey(Entry.Source)}.{Entry.Name}";
    public override string DisplayName => Entry.DisplayName;
    public override SettingCategory Category => SettingCategory.Startup;
    public override bool SelectedByDefault => false;

    public override SettingValue ReadCurrent(HardwareProfile hardware) =>
        Value(StartupApps.IsEnabled(Entry.Source, Entry.Name) ? Enabled : Disabled);

    /// <summary>Sous le nom, comme Windows : l'éditeur, puis le compte qui lance le programme.</summary>
    public override SettingTarget ComputeTarget(HardwareProfile hardware) => new(Value(Disabled),
        Entry.Publisher is { } publisher ? $"{publisher} · {Where(Entry.Source)}" : Where(Entry.Source));

    /// <summary>Explication affichée une fois sous le titre du groupe Démarrage.</summary>
    public const string GroupNote =
        "Désactiver ne désinstalle rien : le programme reste utilisable et se réactive depuis Restauration ou le " +
        "Gestionnaire des tâches. Gardez les utilitaires de pilotes (audio, souris, clavier, NVIDIA…).";

    public override void Apply(HardwareProfile hardware, SettingValue value)
    {
        var enabled = value.Raw switch
        {
            Enabled => true,
            Disabled => false,
            _ => throw new FormatException($"État de démarrage inconnu : {value.Raw}")
        };
        StartupApps.SetEnabled(Entry.Source, Entry.Name, enabled);
    }

    private static SettingValue Value(string state) => new(state, state == Enabled ? "Activé" : "Désactivé");

    private static string SourceKey(StartupSource source) => source switch
    {
        StartupSource.UserRun => "user",
        StartupSource.MachineRun => "machine",
        StartupSource.MachineRun32 => "machine32",
        StartupSource.UserFolder => "userfolder",
        _ => "commonfolder"
    };

    private static string Where(StartupSource source) =>
        source is StartupSource.UserRun or StartupSource.UserFolder ? "votre compte" : "tous les comptes";
}
