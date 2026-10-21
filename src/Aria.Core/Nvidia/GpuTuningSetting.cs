using Aria.Core.Models;
using Aria.Core.Settings;

namespace Aria.Core.Nvidia;

/// <summary>
/// Le réglage GPU vu par la page Restauration : une seule valeur (limite, plafond, décalages, démarrage),
/// restaurée d'un bloc. Il se règle dans l'onglet Réglage GPU, jamais depuis la liste de Réglages :
/// il est donc toujours « non applicable » et masqué du plan, mais restaurable (la restauration ignore l'applicabilité).
/// </summary>
public sealed class GpuTuningSetting(string exePath) : SettingBase
{
    public override string Id => GpuProfile.SettingId;
    public override string DisplayName => GpuProfile.SettingName;
    public override SettingCategory Category => SettingCategory.Nvidia;
    public override bool SelectedByDefault => false;
    public override bool HiddenWhenNotApplicable => true;

    public override string? WhyNotApplicable(HardwareProfile hardware) => "Se règle dans l'onglet Réglage GPU";

    public override SettingValue ReadCurrent(HardwareProfile hardware)
    {
        var state = GpuProfile.ReadCurrent(Tuner());
        return new SettingValue(state.ToRaw(), state.Describe());
    }

    public override SettingTarget ComputeTarget(HardwareProfile hardware) =>
        new(ReadCurrent(hardware), "Réglé dans l'onglet Réglage GPU");

    public override void Apply(HardwareProfile hardware, SettingValue value)
    {
        if (!GpuTuningState.TryParse(value.Raw, out var state))
            throw new FormatException($"Réglage GPU illisible : {value.Raw}");
        GpuProfile.Apply(Tuner(), state, exePath, saveBackup: false);
    }

    private static GpuTuner Tuner() =>
        GpuProfile.Tuner ?? throw new GpuTuningException("Carte NVIDIA inaccessible (pilote ou NVML absent).");
}
