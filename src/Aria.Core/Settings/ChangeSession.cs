using Aria.Core.Backup;
using Aria.Core.Models;

namespace Aria.Core.Settings;

/// <summary>Étape en cours, pour afficher une progression.</summary>
public sealed record ApplyStep(int Done, int Total, string Text);

/// <summary>Bilan d'une série de modifications.</summary>
/// <param name="Error">Renseigné si rien n'a été modifié (sauvegarde impossible).</param>
/// <param name="Warning">Avertissement après application (ex. Panneau de configuration NVIDIA ouvert).</param>
public sealed record ApplyOutcome(
    IReadOnlyList<SettingResult> Results,
    string? BackupPath,
    bool RestorePointCreated,
    string RestorePointMessage,
    string? Error,
    string? Warning = null);

/// <summary>
/// Séquence commune à l'interface et à la console :
/// sauvegarde obligatoire → point de restauration Windows → application, dans l'ordre du catalogue.
/// </summary>
public static class ChangeSession
{
    public static ApplyOutcome Apply(
        HardwareProfile hw,
        IReadOnlyList<PlanItem> items,
        string reason,
        IProgress<ApplyStep>? progress = null)
    {
        var total = items.Count + 2;

        // 1. Sauvegarde obligatoire : sans elle, on ne touche à rien
        progress?.Report(new ApplyStep(0, total, "Sauvegarde des valeurs d'origine…"));
        string backupPath;
        try
        {
            backupPath = BackupStore.Save(reason, items.Select(i => new BackupEntry(
                i.Setting.Id, i.Setting.DisplayName, i.Setting.Category, i.Current!.Raw, i.Current.Display)));
        }
        catch (Exception e)
        {
            return new ApplyOutcome(
                items.Select(i => i.ToResult(SettingStatus.Failed, "Annulé : sauvegarde impossible")).ToList(),
                null, false, "", $"Sauvegarde impossible ({e.Message}) : aucun réglage n'a été modifié.");
        }

        // 2. Point de restauration (filet de sécurité supplémentaire, non bloquant)
        progress?.Report(new ApplyStep(1, total, "Création d'un point de restauration Windows…"));
        var (created, message) = RestorePoint.TryCreate($"Aria - {reason}");

        // 3. Application, puis vérification de chaque réglage
        var results = new List<SettingResult>();
        for (var i = 0; i < items.Count; i++)
        {
            progress?.Report(new ApplyStep(2 + i, total, $"{items[i].Setting.DisplayName}…"));
            results.Add(SettingsPlanner.Apply(hw, items[i]));
        }
        progress?.Report(new ApplyStep(total, total, "Terminé"));

        return new ApplyOutcome(results, backupPath, created, message, null, DriverWarning(results));
    }

    /// <summary>
    /// Le Panneau de configuration NVIDIA garde sa propre copie des réglages : s'il était ouvert pendant l'application,
    /// un clic sur « Appliquer » de sa part pourrait remettre ses anciennes valeurs.
    /// </summary>
    private static string? DriverWarning(IEnumerable<SettingResult> results)
    {
        if (!results.Any(r => r.Category == SettingCategory.Nvidia && r.Status == SettingStatus.Changed)) return null;
        try
        {
            var processes = System.Diagnostics.Process.GetProcessesByName("nvcplui");
            var open = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            return open
                ? "Le Panneau de configuration NVIDIA est ouvert : fermez-le sans cliquer sur « Appliquer », sinon il pourrait remettre ses anciennes valeurs."
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Résultat final pour chaque ligne du plan : appliquée, refusée, ou laissée telle quelle.</summary>
    public static IReadOnlyList<SettingResult> Merge(IReadOnlyList<PlanItem> plan, IReadOnlyCollection<PlanItem> selected, ApplyOutcome? outcome)
    {
        var applied = outcome?.Results.ToDictionary(r => r.Id) ?? [];
        return plan.Select(p =>
            selected.Contains(p) && applied.TryGetValue(p.Setting.Id, out var result) ? result
            : p.NeedsChange ? p.ToResult(SettingStatus.Declined)
            : p.ReadError is not null ? p.ToResult(SettingStatus.Failed)
            : p.NotApplicableReason is not null ? p.ToResult(SettingStatus.NotApplicable)
            : p.ToResult(SettingStatus.Unchanged)).ToList();
    }
}

/// <summary>Progression signalée immédiatement, dans le même fil (pour la console).</summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
