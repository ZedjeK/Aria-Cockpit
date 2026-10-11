using System.Globalization;
using Aria.Core.Models;
using Aria.Core.Settings;

namespace Aria.Core.Nvidia;

/// <summary>Réglages NVIDIA proposés, dans l'ordre d'affichage. Rien sans carte NVIDIA.</summary>
public static class NvidiaSettings
{
    public static IEnumerable<ISetting> Build(HardwareProfile hw)
    {
        if (!hw.HasNvidiaGpu) yield break;

        yield return new LowLatencySetting();
        yield return new VSyncSetting();
        yield return new FrameRateLimitSetting();
        yield return new PreferredRefreshSetting();
        yield return new ShaderCacheSetting();
        yield return new GpuPowerSetting();
        if (NvidiaRules.IsOptimusLaptop(hw)) yield return new PreferredGpuSetting();
        foreach (var display in hw.Displays.Where(NvidiaRules.IsNvidiaDriven))
            yield return new DynamicRangeSetting(display);
    }

    /// <summary>NVAPI joignable ? null si oui, sinon la raison.</summary>
    internal static string? WhyNoNvapi(HardwareProfile hw) =>
        !hw.HasNvidiaGpu ? "Pas de carte NVIDIA"
        : hw.Nvidia is null ? "Pilote NVIDIA non analysé"
        : hw.Nvidia.Error is { } error ? $"Pilote NVIDIA injoignable : {error}"
        : null;

    /// <summary>Réglages du pilote lisibles ? null si oui, sinon la raison.</summary>
    internal static string? WhyNoDriverSettings(HardwareProfile hw) =>
        WhyNoNvapi(hw) ?? (hw.Nvidia!.DriverSettingsError is { } error ? $"Réglages du pilote illisibles : {error}" : null);
}

/// <summary>
/// Réglage du profil global du pilote (les « Paramètres globaux » de l'application NVIDIA).
/// Valeur technique : « user:0x… » si choisie par l'utilisateur, « default:0x… » si c'est la valeur d'usine.
/// Restaurer une valeur « default » remet le réglage d'usine, pas seulement le même chiffre.
/// </summary>
public abstract class NvidiaDriverSetting(uint settingId, uint factoryValue) : SettingBase
{
    private const string UserPrefix = "user:";
    private const string DefaultPrefix = "default:";

    public override SettingCategory Category => SettingCategory.Nvidia;

    protected static IDriverSettings Store => NvApiDriverSettings.Instance;

    public sealed override string? WhyNotApplicable(HardwareProfile hw) => NvidiaSettings.WhyNoDriverSettings(hw) ?? WhyNot(hw);

    protected virtual string? WhyNot(HardwareProfile hw) => null;

    protected abstract string Describe(uint value);

    public override SettingValue ReadCurrent(HardwareProfile hw) => Read(settingId, factoryValue, Describe);

    public override bool IsAtTarget(SettingValue current, SettingValue target) =>
        Matches(Effective(current.Raw), Effective(target.Raw));

    /// <summary>La valeur actuelle produit-elle l'effet voulu ? Par défaut : valeurs identiques.</summary>
    protected virtual bool Matches(uint current, uint target) => current == target;

    public override void Apply(HardwareProfile hw, SettingValue value) => Write(settingId, factoryValue, value.Raw);

    protected SettingValue User(uint value) => UserValue(value, Describe);
    protected SettingValue Default(uint value) => DefaultValue(value, Describe);

    // ---- Outils partagés (aussi pour les réglages à plusieurs identifiants) ----

    internal static SettingValue Read(uint settingId, uint factoryValue, Func<uint, string> describe)
    {
        var value = Store.Read(settingId);
        return value is { Value: { } v, IsUserSet: true } ? UserValue(v, describe) : DefaultValue(value.Value ?? factoryValue, describe);
    }

    internal static void Write(uint settingId, uint factoryValue, string raw)
    {
        var wanted = Effective(raw);
        if (raw.StartsWith(DefaultPrefix, StringComparison.OrdinalIgnoreCase))
        {
            Store.RestoreDefault(settingId);
            if ((Store.Read(settingId).Value ?? factoryValue) == wanted) return;
        }
        Store.Write(settingId, wanted);
    }

    internal static SettingValue UserValue(uint value, Func<uint, string> describe) => new($"{UserPrefix}0x{value:X8}", describe(value));

    internal static SettingValue DefaultValue(uint value, Func<uint, string> describe) =>
        new($"{DefaultPrefix}0x{value:X8}", $"{describe(value)} (par défaut)");

    /// <summary>Valeur numérique d'une valeur technique (« user:0x00000001 » → 1).</summary>
    public static uint Effective(string raw)
    {
        var hex = raw[(raw.IndexOf(':') + 1)..].Trim();
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex[2..];
        return uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}

public sealed class LowLatencySetting() : NvidiaDriverSetting(DrsIds.LowLatency, DrsIds.LowLatencyAppControlled)
{
    public override string Id => "nvidia.lowlatency";
    public override string DisplayName => "Mode faible latence";

    protected override string Describe(uint value) => value switch
    {
        0 => "Désactivé",
        1 => "Activé",
        var n => $"{n} images en attente"
    };

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(User(1), "Une seule image en attente au lieu de 1 à 3 : moins de latence dans les jeux DirectX 9 et 11. " +
                     "Les jeux avec NVIDIA Reflex utilisent leur propre réglage, plus efficace.");
}

public sealed class VSyncSetting() : NvidiaDriverSetting(DrsIds.VSync, DrsIds.VSyncPassive)
{
    public override string Id => "nvidia.vsync";
    public override string DisplayName => "Synchronisation verticale (pilote)";

    protected override string? WhyNot(HardwareProfile hw) => NvidiaRules.WhyNoGsync(hw);

    protected override string Describe(uint value) => value switch
    {
        DrsIds.VSyncPassive => "Choix du jeu",
        DrsIds.VSyncForceOff => "Désactivée",
        DrsIds.VSyncForceOn => "Activée",
        DrsIds.VSyncFast => "Rapide",
        DrsIds.VSyncHalf => "Demi-fréquence",
        DrsIds.VSyncThird => "Tiers de fréquence",
        DrsIds.VSyncQuarter => "Quart de fréquence",
        _ => $"Valeur 0x{value:X8}"
    };

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(User(DrsIds.VSyncForceOn), "Avec G-SYNC, la synchronisation du pilote supprime les dernières déchirures ; " +
                                       "la limite d'images par seconde évite sa latence. Désactivez la synchronisation verticale dans les jeux.");
}

public sealed class FrameRateLimitSetting() : NvidiaDriverSetting(DrsIds.FrameRateLimit, DrsIds.FrameRateLimitOff)
{
    public override string Id => "nvidia.framelimit";
    public override string DisplayName => "Nombre d'images par seconde maximal";

    protected override string? WhyNot(HardwareProfile hw) => NvidiaRules.WhyNoGsync(hw);

    protected override string Describe(uint value) => value == 0 ? "Aucune limite" : $"{value} images/s";

    public override SettingTarget ComputeTarget(HardwareProfile hw)
    {
        var hz = NvidiaRules.PrimaryNvidiaDisplay(hw)!.MaxRefreshHz;
        return new(User((uint)NvidiaRules.FrameCap(hz)),
            $"Écran G-SYNC à {hz} Hz : rester {NvidiaRules.GsyncMargin} images/s sous la fréquence maximale garde G-SYNC actif, " +
            "sans la latence de la synchronisation verticale. Les jeux avec NVIDIA Reflex se limitent d'eux-mêmes.");
    }
}

public sealed class PreferredRefreshSetting() : NvidiaDriverSetting(DrsIds.PreferredRefresh, DrsIds.RefreshAppControlled)
{
    public override string Id => "nvidia.preferredrefresh";
    public override string DisplayName => "Fréquence préférée";

    protected override string Describe(uint value) =>
        (value & DrsIds.RefreshHighest) != 0 ? "La plus élevée disponible" : "Choix de l'application";

    protected override bool Matches(uint current, uint target) => (current & 1) == (target & 1);

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(User(DrsIds.RefreshHighest), "Les jeux en plein écran exclusif démarrent à la fréquence maximale de l'écran au lieu de 60 Hz.");
}

public sealed class ShaderCacheSetting() : NvidiaDriverSetting(DrsIds.ShaderCacheSize, DrsIds.ShaderCacheDefault)
{
    public override string Id => "nvidia.shadercache";
    public override string DisplayName => "Taille du cache des shaders";

    protected override string Describe(uint value) => NvidiaRules.DescribeCacheSize(value);

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(User(DrsIds.ShaderCacheUnlimited), "Un cache plein oblige à recompiler les shaders, d'où des saccades en jeu. " +
                                               "Il ne grossit qu'au besoin, dans le dossier DXCache du disque système.");
}

public sealed class GpuPowerSetting() : NvidiaDriverSetting(DrsIds.PowerMode, DrsIds.PowerOptimal)
{
    public override string Id => "nvidia.power";
    public override string DisplayName => "Gestion de l'alimentation (carte graphique)";

    protected override string Describe(uint value) => value switch
    {
        DrsIds.PowerAdaptive => "Adaptatif",
        DrsIds.PowerPreferMax => "Performances maximales",
        DrsIds.PowerDriverControlled => "Contrôlé par le pilote",
        DrsIds.PowerConsistent => "Performances constantes",
        DrsIds.PowerPreferMin => "Puissance minimale",
        DrsIds.PowerOptimal => "Normal",
        _ => $"Valeur {value}"
    };

    /// <summary>
    /// Vers « Normal » : seul « Performances maximales » est corrigé, les autres modes sont laissés tels quels.
    /// Pour une restauration (autre cible), la valeur doit être identique.
    /// </summary>
    protected override bool Matches(uint current, uint target) =>
        target == DrsIds.PowerOptimal ? current != DrsIds.PowerPreferMax : current == target;

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Default(DrsIds.PowerOptimal), "« Performances maximales » garde la carte à haute fréquence dès qu'une application 3D est ouverte : " +
                                          "plus chaude et plus bruyante, sans gain mesuré en jeu.");
}

/// <summary>
/// « Processeur graphique préféré » des portables Optimus. Deux réglages du SDK NVIDIA (SHIM_MCCOMPAT et SHIM_RENDERING_MODE),
/// lus et écrits ensemble. Valeur technique : « valeur1;valeur2 ».
/// </summary>
public sealed class PreferredGpuSetting : SettingBase
{
    public override string Id => "nvidia.optimus";
    public override string DisplayName => "Processeur graphique préféré";
    public override SettingCategory Category => SettingCategory.Nvidia;
    public override bool SelectedByDefault => false;

    public override string? WhyNotApplicable(HardwareProfile hw) =>
        NvidiaSettings.WhyNoDriverSettings(hw) ?? (NvidiaRules.IsOptimusLaptop(hw) ? null : "Réservé aux portables à deux puces graphiques");

    private static string Describe(uint value) =>
        (value & DrsIds.OptimusAutoSelect) != 0 ? "Sélection automatique"
        : (value & 0x3) == DrsIds.OptimusNvidia ? "Processeur NVIDIA haute performance"
        : "Graphique intégré";

    public override SettingValue ReadCurrent(HardwareProfile hw)
    {
        var compat = NvidiaDriverSetting.Read(DrsIds.OptimusCompat, DrsIds.OptimusAutoSelect, Describe);
        var rendering = NvidiaDriverSetting.Read(DrsIds.OptimusRendering, DrsIds.OptimusAutoSelect, Describe);
        return new($"{compat.Raw};{rendering.Raw}", compat.Display);
    }

    public override SettingTarget ComputeTarget(HardwareProfile hw)
    {
        var nvidia = NvidiaDriverSetting.UserValue(DrsIds.OptimusNvidia, Describe);
        return new(new($"{nvidia.Raw};{nvidia.Raw}", nvidia.Display),
            "Tous les logiciels utilisent la carte NVIDIA : plus de performances, mais nettement moins d'autonomie sur batterie. " +
            "Effet au prochain lancement de chaque application.");
    }

    public override bool IsAtTarget(SettingValue current, SettingValue target) =>
        current.Raw.Split(';').Select(Mode).SequenceEqual(target.Raw.Split(';').Select(Mode));

    private static uint Mode(string raw)
    {
        var value = NvidiaDriverSetting.Effective(raw);
        return (value & DrsIds.OptimusAutoSelect) != 0 ? DrsIds.OptimusAutoSelect : value & 0x3;
    }

    public override void Apply(HardwareProfile hw, SettingValue value)
    {
        var parts = value.Raw.Split(';');
        if (parts.Length != 2) throw new FormatException($"Valeur inattendue : {value.Raw}");
        NvidiaDriverSetting.Write(DrsIds.OptimusCompat, DrsIds.OptimusAutoSelect, parts[0]);
        NvidiaDriverSetting.Write(DrsIds.OptimusRendering, DrsIds.OptimusAutoSelect, parts[1]);
    }
}

/// <summary>
/// Plage dynamique de sortie d'un écran (« Complète » 0-255 ou « Limitée » 16-235).
/// Valeur technique : « politique:plage », ex. « driver:limited » (choix du pilote) ou « user:full ».
/// </summary>
public sealed class DynamicRangeSetting(DisplayInfo display) : SettingBase
{
    public override string Id => $"nvidia.range.{display.DeviceName}";
    public override string DisplayName => $"Plage dynamique : {display.MonitorName}";
    public override SettingCategory Category => SettingCategory.Nvidia;

    private static INvidiaDisplays Api => NvApiDisplays.Instance;

    public override string? WhyNotApplicable(HardwareProfile hw)
    {
        if (NvidiaSettings.WhyNoNvapi(hw) is { } why) return why;
        if (display.HdrEnabled == true) return "HDR actif : couleurs gérées par Windows";
        var color = hw.Nvidia!.Display(display.DeviceName)?.Color;
        if (color is null) return "Couleurs de l'écran illisibles via NVAPI";
        return color.IsRgb ? null : "Sortie en YCbCr : plage gérée par l'écran";
    }

    public override SettingValue ReadCurrent(HardwareProfile hw)
    {
        var color = Api.ReadColor(display.DeviceName);
        return Value(color.Policy, color.Range);
    }

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(ColorPolicy.User, DynamicRange.Full),
            "En plage limitée, les noirs paraissent gris et l'image voilée. Si l'écran a un réglage « niveau de noir HDMI » " +
            "ou « plage RGB », mettez-le sur Complet ou Élevé.");

    /// <summary>Vers « Complète » : seule la plage limitée est corrigée. Pour une restauration, la plage doit être identique.</summary>
    public override bool IsAtTarget(SettingValue current, SettingValue target)
    {
        var wanted = Parse(target.Raw).Range;
        var actual = Parse(current.Raw).Range;
        return wanted == DynamicRange.Full ? actual != DynamicRange.Limited : actual == wanted;
    }

    public override void Apply(HardwareProfile hw, SettingValue value)
    {
        var (policy, range) = Parse(value.Raw);
        Api.SetColor(display.DeviceName, policy, range);
    }

    private static SettingValue Value(ColorPolicy policy, DynamicRange range)
    {
        var text = range switch
        {
            DynamicRange.Full => "Complète (0-255)",
            DynamicRange.Limited => "Limitée (16-235)",
            _ => "Automatique"
        };
        return new($"{(policy == ColorPolicy.User ? "user" : "driver")}:{range.ToString().ToLowerInvariant()}",
            policy == ColorPolicy.Driver ? $"{text} (par défaut)" : text);
    }

    internal static (ColorPolicy Policy, DynamicRange Range) Parse(string raw)
    {
        var parts = raw.Split(':');
        if (parts.Length != 2) throw new FormatException($"Valeur inattendue : {raw}");
        var policy = parts[0].Equals("user", StringComparison.OrdinalIgnoreCase) ? ColorPolicy.User : ColorPolicy.Driver;
        var range = Enum.Parse<DynamicRange>(parts[1], ignoreCase: true);
        return (policy, range);
    }
}
