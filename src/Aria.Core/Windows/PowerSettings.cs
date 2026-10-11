using Aria.Core.Models;
using Aria.Core.Settings;

namespace Aria.Core.Windows;

/// <summary>
/// PC fixe : plan Performances élevées (associé à un état minimal du processeur réduit, voir ci-dessous).
/// Portable : seul le plan Économie d'énergie est corrigé (vers Équilibré), pour préserver batterie et chauffe.
/// </summary>
public sealed class PowerPlanSetting : SettingBase
{
    public override string Id => "windows.powerplan";
    public override string DisplayName => "Plan d'alimentation";
    public override SettingCategory Category => SettingCategory.Windows;

    public override string? WhyNotApplicable(HardwareProfile hw) =>
        !hw.IsLaptop && !NativePower.PlanExists(NativePower.HighPerformance)
            ? "Plan Performances élevées absent de ce PC (Windows en veille moderne)"
            : null;

    public override SettingValue ReadCurrent(HardwareProfile hw) => Value(NativePower.GetActivePlan());

    public override SettingTarget ComputeTarget(HardwareProfile hw)
    {
        var current = NativePower.GetActivePlan();

        if (!hw.IsLaptop)
        {
            return current == NativePower.Ultimate
                ? new(Value(current), "Plan Performances optimales conservé : il est encore plus agressif que Performances élevées.")
                : new(Value(NativePower.HighPerformance),
                    "Processeur toujours prêt à répondre. L'état minimal réduit (réglage suivant) lui permet quand même de ralentir au repos.");
        }

        return current == NativePower.PowerSaver
            ? new(Value(NativePower.Balanced), "Le plan Économie d'énergie bride le processeur. Équilibré est très réactif sur les processeurs récents.")
            : new(Value(current), "Portable : plan conservé pour préserver la batterie et limiter la chauffe.");
    }

    public override void Apply(HardwareProfile hw, SettingValue value) => NativePower.SetActivePlan(Guid.Parse(value.Raw));

    private static SettingValue Value(Guid plan) => new(plan.ToString(), NativePower.PlanName(plan));
}

/// <summary>
/// "État minimal du processeur" du plan Performances élevées, sur secteur (100 % par défaut).
/// Réglé directement sur ce plan, et non sur le plan actif : il reste juste même si le changement de plan est décoché.
/// </summary>
public sealed class MinProcessorStateSetting : SettingBase
{
    private const int TargetPercent = 20;

    public override string Id => "windows.cpuminstate";
    public override string DisplayName => "État minimal du processeur (Performances élevées)";
    public override SettingCategory Category => SettingCategory.Windows;

    public override string? WhyNotApplicable(HardwareProfile hw) =>
        hw.IsLaptop ? "PC portable : le plan Performances élevées n'est pas utilisé"
        : !NativePower.PlanExists(NativePower.HighPerformance) ? "Plan Performances élevées absent de ce PC"
        : null;

    public override SettingValue ReadCurrent(HardwareProfile hw) => Value(NativePower.GetMinProcessorState(NativePower.HighPerformance));

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(TargetPercent), "Le processeur peut baisser sa fréquence au repos (moins de chaleur et de consommation) sans perdre en réactivité.");

    public override void Apply(HardwareProfile hw, SettingValue value) =>
        NativePower.SetMinProcessorState(NativePower.HighPerformance, int.Parse(value.Raw));

    private static SettingValue Value(int percent) => new(percent.ToString(), $"{percent} %");
}

/// <summary>Curseur "Mode d'alimentation" de Windows 11 (plan Équilibré) : portables uniquement, sur secteur.</summary>
public sealed class PowerModeSetting : SettingBase
{
    public override string Id => "windows.powermode";
    public override string DisplayName => "Mode d'alimentation (sur secteur)";
    public override SettingCategory Category => SettingCategory.Windows;

    public override string? WhyNotApplicable(HardwareProfile hw)
    {
        if (!hw.IsLaptop) return "PC fixe : remplacé par le plan Performances élevées";

        var plan = NativePower.GetActivePlan();
        if (plan != NativePower.Balanced && plan != NativePower.PowerSaver)
            return $"Plan « {NativePower.PlanName(plan)} » : ce mode ne s'applique qu'au plan Équilibré";

        // Le mode se règle pour la source d'alimentation actuelle : on ne touche jamais au réglage sur batterie.
        if (!NativePower.IsOnAcPower())
            return "Portable sur batterie : le brancher sur secteur puis relancer Aria";

        return null;
    }

    public override SettingValue ReadCurrent(HardwareProfile hw) => Value(NativePower.GetMode());

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(NativePower.ModeBestPerformance), "Le processeur réagit plus vite à chaque sollicitation, en jeu comme au bureau.");

    public override void Apply(HardwareProfile hw, SettingValue value)
    {
        if (NativePower.GetActivePlan() != NativePower.Balanced)
            throw new InvalidOperationException("Le plan actif n'est pas Équilibré (changement de plan non retenu ?).");
        NativePower.SetMode(Guid.Parse(value.Raw));
    }

    private static SettingValue Value(Guid mode) => new(mode.ToString(), NativePower.ModeName(mode));
}
