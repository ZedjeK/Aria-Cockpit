using Aria.Core.Models;

namespace Aria.Core.Settings;

/// <summary>Une ligne du plan : état actuel, cible et raison d'un réglage.</summary>
public sealed record PlanItem(
    ISetting Setting,
    SettingValue? Current,
    SettingTarget? Target,
    string? NotApplicableReason,
    string? ReadError)
{
    public bool NeedsChange =>
        NotApplicableReason is null && ReadError is null && Current is not null && Target is not null
        && !Setting.IsAtTarget(Current, Target.Value);

    /// <summary>Résultat final pour une ligne qui n'a pas été appliquée.</summary>
    public SettingResult ToResult(SettingStatus status, string? error = null) => new(
        Setting.Id, Setting.DisplayName, Setting.Category, Current, Target?.Value,
        NotApplicableReason ?? Target?.Reason ?? "", status, Setting.RequiresReboot, error ?? ReadError);
}

public static class SettingsPlanner
{
    /// <summary>Lit l'état actuel de chaque réglage et calcule sa cible selon le matériel.</summary>
    public static IReadOnlyList<PlanItem> Build(HardwareProfile hw, IEnumerable<ISetting> settings) =>
        settings.Select(s => Plan(hw, s, () => s.ComputeTarget(hw)))
            .Where(p => !(p.NotApplicableReason is not null && p.Setting.HiddenWhenNotApplicable))
            .ToList();

    /// <summary>Plan de restauration : la cible est la valeur sauvegardée.</summary>
    public static PlanItem PlanRestore(HardwareProfile hw, ISetting setting, SettingValue saved, string reason) =>
        Plan(hw, setting, () => new SettingTarget(saved, reason), ignoreApplicability: true);

    private static PlanItem Plan(HardwareProfile hw, ISetting s, Func<SettingTarget> target, bool ignoreApplicability = false)
    {
        try
        {
            var why = ignoreApplicability ? null : s.WhyNotApplicable(hw);
            if (why is not null) return new PlanItem(s, null, null, why, null);
            return new PlanItem(s, s.ReadCurrent(hw), target(), null, null);
        }
        catch (Exception e)
        {
            return new PlanItem(s, null, null, null, $"Lecture impossible : {e.Message}");
        }
    }

    /// <summary>Applique une ligne puis relit la valeur pour confirmer.</summary>
    public static SettingResult Apply(HardwareProfile hw, PlanItem item)
    {
        if (item.Target is null) return item.ToResult(SettingStatus.Failed, "Aucune cible");
        try
        {
            item.Setting.Apply(hw, item.Target.Value);
            var after = item.Setting.ReadCurrent(hw);
            return item.Setting.IsAtTarget(after, item.Target.Value)
                ? item.ToResult(SettingStatus.Changed)
                : item.ToResult(SettingStatus.Failed, $"Valeur relue après modification : {after.Display}");
        }
        catch (Exception e)
        {
            return item.ToResult(SettingStatus.Failed, e.Message);
        }
    }
}
