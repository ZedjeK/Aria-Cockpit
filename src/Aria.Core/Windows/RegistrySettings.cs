using Microsoft.Win32;
using Aria.Core.Models;
using Aria.Core.Settings;

namespace Aria.Core.Windows;

/// <summary>Réglage stocké dans une valeur DWORD du registre. "absent" = valeur non définie (défaut de Windows).</summary>
public abstract class RegistryDwordSetting(RegistryKey hive, string keyPath, string valueName, int? valueWhenMissing) : SettingBase
{
    protected const string Absent = "absent";

    public override SettingCategory Category => SettingCategory.Windows;

    protected abstract string Describe(int value);

    public override SettingValue ReadCurrent(HardwareProfile hardware)
    {
        using var key = hive.OpenSubKey(keyPath);
        if (key?.GetValue(valueName) is int value) return Value(value);
        return valueWhenMissing is int fallback ? Value(fallback) : new SettingValue(Absent, "Par défaut de Windows");
    }

    public override void Apply(HardwareProfile hardware, SettingValue value)
    {
        using var key = hive.CreateSubKey(keyPath, writable: true);
        if (value.Raw == Absent) key.DeleteValue(valueName, throwOnMissingValue: false);
        else key.SetValue(valueName, int.Parse(value.Raw), RegistryValueKind.DWord);
    }

    protected SettingValue Value(int value) => new(value.ToString(), Describe(value));
}

public sealed class GameModeSetting() : RegistryDwordSetting(
    Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", valueWhenMissing: 1)
{
    public override string Id => "windows.gamemode";
    public override string DisplayName => "Mode Jeu";
    protected override string Describe(int value) => value == 1 ? "Activé" : "Désactivé";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(1), "Donne la priorité au jeu et suspend les mises à jour Windows pendant les parties.");
}

public sealed class HardwareGpuSchedulingSetting() : RegistryDwordSetting(
    Registry.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", valueWhenMissing: null)
{
    public override string Id => "windows.hags";
    public override string DisplayName => "Planification GPU accélérée";
    public override bool RequiresReboot => true;
    protected override string Describe(int value) => value == 2 ? "Activée" : "Désactivée";

    public override string? WhyNotApplicable(HardwareProfile hw) =>
        hw.HasNvidiaGpu ? null : "Réservé aux cartes NVIDIA dans Aria";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(2), "Réduit la latence et est indispensable à la génération d'images DLSS (Frame Generation).");
}

public sealed class BackgroundRecordingSetting() : RegistryDwordSetting(
    Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled", valueWhenMissing: 0)
{
    public override string Id => "windows.backgroundrecording";
    public override string DisplayName => "Enregistrement en arrière-plan (Xbox Game Bar)";
    protected override string Describe(int value) => value == 1 ? "Activé" : "Désactivé";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(0), "Enregistre la partie en continu : coûte des performances. Les captures manuelles restent possibles.");
}

/// <summary>
/// Options DirectX de Windows, toutes stockées dans une même chaîne "Cle1=1;Cle2=1;".
/// Chaque réglage ne touche que sa propre clé et conserve les autres.
/// </summary>
public abstract class DirectXGlobalSetting(string optionName) : SettingBase
{
    private const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string ValueName = "DirectXUserGlobalSettings";
    private const string Absent = "absent";

    public override SettingCategory Category => SettingCategory.Windows;

    public override SettingValue ReadCurrent(HardwareProfile hardware)
    {
        var options = ReadOptions();
        return options.TryGetValue(optionName, out var v)
            ? new SettingValue(v, v == "1" ? "Activé" : "Désactivé")
            : new SettingValue(Absent, "Par défaut de Windows");
    }

    public override void Apply(HardwareProfile hardware, SettingValue value)
    {
        var options = ReadOptions();
        if (value.Raw == Absent) options.Remove(optionName);
        else options[optionName] = value.Raw;

        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        if (options.Count == 0) key.DeleteValue(ValueName, throwOnMissingValue: false);
        else key.SetValue(ValueName, string.Concat(options.Select(o => $"{o.Key}={o.Value};")), RegistryValueKind.String);
    }

    protected static SettingValue Enabled => new("1", "Activé");

    private static Dictionary<string, string> ReadOptions()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var raw = key?.GetValue(ValueName) as string ?? "";
        return raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0], StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.OrdinalIgnoreCase);
    }
}

public sealed class WindowedGamesOptimizationSetting() : DirectXGlobalSetting("SwapEffectUpgradeEnable")
{
    public override string Id => "windows.windowedoptimizations";
    public override string DisplayName => "Optimisations des jeux en mode fenêtré";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Enabled, "Réduit la latence des jeux DirectX 10/11 en fenêtré et sans bordure, et y permet le VRR.");
}

/// <summary>
/// Pilotes livrés par Windows Update avec les mises à jour de qualité. Stratégie « Ne pas inclure les pilotes avec les
/// mises à jour Windows » (Policy CSP Update/ExcludeWUDriversInQualityUpdate, WindowsUpdate.admx) : 0 ou absente,
/// Windows Update livre aussi des pilotes ; 1, il n'en livre plus. Décoché : c'est un choix, pas un gain pour tous.
/// </summary>
public sealed class WindowsUpdateDriversSetting() : RegistryDwordSetting(
    Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", valueWhenMissing: 0)
{
    public override string Id => "windows.wudrivers";
    public override string DisplayName => "Pilotes livrés par Windows Update";
    public override bool SelectedByDefault => false;
    protected override string Describe(int value) => value == 1 ? "Exclus" : "Inclus";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(1), "Windows Update ne remplace plus vos pilotes de lui-même, dont celui de la carte graphique : un pilote changé " +
                      "sans vous peut défaire un réglage GPU validé. Contrepartie : les autres pilotes (réseau, audio, chipset…) " +
                      "ne sont plus mis à jour par Windows Update non plus, c'est à vous de les suivre.");
}

public sealed class VariableRefreshRateSetting() : DirectXGlobalSetting("VRROptimizeEnable")
{
    public override string Id => "windows.vrr";
    public override string DisplayName => "Taux de rafraîchissement variable (VRR)";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Enabled, "Étend G-Sync / FreeSync aux jeux qui ne le gèrent pas nativement. Sans effet si l'écran n'est pas compatible.");
}
