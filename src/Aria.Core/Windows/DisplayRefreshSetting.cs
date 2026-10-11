using Aria.Core.Inventory;
using Aria.Core.Models;
using Aria.Core.Settings;
using static Aria.Core.Inventory.NativeDisplay;

namespace Aria.Core.Windows;

/// <summary>Fréquence de rafraîchissement d'un écran, réglée au maximum disponible à la résolution actuelle.</summary>
public sealed class DisplayRefreshSetting(DisplayInfo display) : SettingBase
{
    private const int DmDisplayFrequency = 0x400000;
    private const uint CdsUpdateRegistry = 0x01;
    private const uint CdsTest = 0x02;

    public override string Id => $"windows.refresh.{display.DeviceName}";
    public override string DisplayName => $"Fréquence : {display.MonitorName}";
    public override SettingCategory Category => SettingCategory.Windows;

    public override string? WhyNotApplicable(HardwareProfile hw) =>
        display.MaxRefreshHz > 0 ? null : "Fréquences de l'écran illisibles";

    public override SettingValue ReadCurrent(HardwareProfile hw) => Value(CurrentMode().dmDisplayFrequency);

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(display.MaxRefreshHz),
            $"Fréquence maximale de l'écran à {display.Width}x{display.Height} : plus fluide et moins de latence.");

    public override void Apply(HardwareProfile hw, SettingValue value)
    {
        var mode = CurrentMode();
        mode.dmDisplayFrequency = int.Parse(value.Raw);
        mode.dmFields = DmDisplayFrequency;

        // Test d'abord : si le pilote refuse ce mode, rien n'est modifié.
        var test = ChangeDisplaySettingsEx(display.DeviceName, ref mode, IntPtr.Zero, CdsTest, IntPtr.Zero);
        if (test != 0) throw new InvalidOperationException($"Mode {value.Raw} Hz refusé par le pilote (code {test}).");

        var result = ChangeDisplaySettingsEx(display.DeviceName, ref mode, IntPtr.Zero, CdsUpdateRegistry, IntPtr.Zero);
        if (result != 0) throw new InvalidOperationException($"Changement de fréquence refusé (code {result}).");
    }

    private DEVMODE CurrentMode()
    {
        var mode = DEVMODE.Create();
        if (!EnumDisplaySettings(display.DeviceName, EnumCurrentSettings, ref mode))
            throw new InvalidOperationException("Écran introuvable (débranché ?).");
        return mode;
    }

    private static SettingValue Value(int hz) => new(hz.ToString(), $"{hz} Hz");
}
