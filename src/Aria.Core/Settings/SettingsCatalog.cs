using Aria.Core.Models;
using Aria.Core.Nvidia;
using Aria.Core.Privacy;
using Aria.Core.Windows;

namespace Aria.Core.Settings;

/// <summary>Liste de tous les réglages gérés, dans l'ordre d'application (le plan avant le mode d'alimentation).</summary>
public static class SettingsCatalog
{
    public static IReadOnlyList<ISetting> Build(HardwareProfile hw)
    {
        var settings = new List<ISetting>();

        settings.AddRange(hw.Displays.Select(d => new DisplayRefreshSetting(d)));
        settings.Add(new PowerPlanSetting());
        settings.Add(new MinProcessorStateSetting());
        settings.Add(new PowerModeSetting());
        settings.Add(new GameModeSetting());
        settings.Add(new HardwareGpuSchedulingSetting());
        settings.Add(new WindowedGamesOptimizationSetting());
        settings.Add(new VariableRefreshRateSetting());
        settings.Add(new BackgroundRecordingSetting());
        settings.Add(new MouseAccelerationSetting());
        settings.Add(new AnimationsSetting());

        // Réglages du pilote NVIDIA (profil global) et plage dynamique de chaque écran NVIDIA
        settings.AddRange(NvidiaSettings.Build(hw));

        // Confidentialité : données envoyées à Microsoft, publicités, services inutiles selon le matériel
        settings.AddRange(PrivacySettings.Build(hw));
        return settings;
    }
}
