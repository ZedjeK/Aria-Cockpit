using Aria.Core.Models;

namespace Aria.Core.Settings;

/// <summary>Valeurs par défaut communes à la plupart des réglages.</summary>
public abstract class SettingBase : ISetting
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract SettingCategory Category { get; }
    public virtual bool RequiresReboot => false;
    public virtual bool SelectedByDefault => true;

    public virtual string? WhyNotApplicable(HardwareProfile hardware) => null;
    public abstract SettingValue ReadCurrent(HardwareProfile hardware);
    public abstract SettingTarget ComputeTarget(HardwareProfile hardware);
    public virtual bool IsAtTarget(SettingValue current, SettingValue target) => current.SameAs(target);
    public abstract void Apply(HardwareProfile hardware, SettingValue value);
}
