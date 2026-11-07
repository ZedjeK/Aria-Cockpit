using System.Globalization;
using System.Text.RegularExpressions;

namespace Aria.Core.Backup;

/// <summary>Durées de conservation du dossier des données.</summary>
/// <param name="ReportsMonths">Rapports et fiches PC : supprimés au-delà de ce nombre de mois.</param>
/// <param name="BackupsMonths">Sauvegardes : supprimées au-delà de ce nombre de mois…</param>
/// <param name="KeepBackups">… sauf les plus récentes, toujours gardées quel que soit leur âge.</param>
public sealed record RetentionSettings(int ReportsMonths, int BackupsMonths, int KeepBackups)
{
    public static readonly RetentionSettings Default = new(6, 12, 10);
}

/// <summary>
/// Ménage du dossier des données, une fois par jour au plus : rapports et fiches PC anciens supprimés, sauvegardes
/// anciennes supprimées en gardant toujours les plus récentes, journaux basculés au-delà de 1 Mo.
/// Seuls les fichiers créés par Aria Cockpit (reconnus à leur nom) sont concernés ; leur âge se lit dans le nom.
/// Les réglages en place (gpu-profile.json, equilibre.json, profils.json, ventilateur*.json) ne sont jamais touchés.
/// Repris de CTI-Cockpit.
/// </summary>
public static partial class DataRetention
{
    internal const long MaxLogBytes = 1024 * 1024;
    internal const string LastRunName = "menage.txt";

    /// <summary>Journaux tournants : au-delà de 1 Mo, renommés en « nom-precedent.log » (qui remplace l'archive précédente).</summary>
    internal static readonly string[] Logs = ["erreurs.log", "gpu-profile.log", "limite-charge.log"];

    /// <summary>Rapports (réglages, restaurations, mises à jour, nettoyages, réglage GPU) et fiches PC.</summary>
    [GeneratedRegex(@"^(rapport|mises-a-jour|nettoyage|reglage-gpu|fiche-pc)_(?<date>\d{4}-\d{2}-\d{2}_\d{6})\.(txt|html)$", RegexOptions.IgnoreCase)]
    private static partial Regex ReportName();

    [GeneratedRegex(@"^sauvegarde_(?<date>\d{4}-\d{2}-\d{2}_\d{6})\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex BackupName();

    /// <summary>Lance le ménage s'il n'a pas déjà eu lieu aujourd'hui. Renvoie un résumé pour le journal, ou null s'il n'y avait rien à faire.</summary>
    public static string? RunDaily() => RunDaily(BackupStore.DataRoot, RetentionSettings.Default, DateTime.Now);

    internal static string? RunDaily(string root, RetentionSettings settings, DateTime now)
    {
        if (!Directory.Exists(root)) return null;
        var lastRun = Path.Combine(root, LastRunName);
        if (File.Exists(lastRun) && File.ReadAllText(lastRun).Trim() == now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) return null;

        var problems = new List<string>();

        // Rapports et fiches PC : au-delà de la durée de conservation
        var reportLimit = now.AddMonths(-settings.ReportsMonths);
        var reports = Dated(Path.Combine(root, "Rapports"), ReportName())
            .Where(f => f.Date < reportLimit)
            .Count(f => Delete(f.Path, problems));

        // Sauvegardes : les plus récentes toujours gardées, les autres supprimées au-delà de la durée de conservation
        var backupLimit = now.AddMonths(-settings.BackupsMonths);
        var backups = Dated(Path.Combine(root, "Sauvegardes"), BackupName())
            .OrderByDescending(f => f.Date)
            .Skip(settings.KeepBackups)
            .Where(f => f.Date < backupLimit)
            .Count(f => Delete(f.Path, problems));

        var rotated = Logs.Where(log => RotateLog(root, log, problems)).ToList();

        File.WriteAllText(lastRun, now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        if (reports == 0 && backups == 0 && rotated.Count == 0 && problems.Count == 0) return null;
        var done = new List<string>();
        if (reports > 0) done.Add($"{reports} rapport(s) de plus de {settings.ReportsMonths} mois supprimé(s)");
        if (backups > 0) done.Add($"{backups} sauvegarde(s) de plus de {settings.BackupsMonths} mois supprimée(s)");
        done.AddRange(rotated.Select(log => $"{log} archivé dans {PreviousName(log)}"));
        return $"Ménage du dossier des données : {(done.Count > 0 ? string.Join(", ", done) : "rien supprimé")}." +
               (problems.Count > 0 ? $" Non supprimés : {string.Join(" ; ", problems)}" : "");
    }

    /// <summary>Nom de l'archive d'un journal : « erreurs.log » → « erreurs-precedent.log ».</summary>
    internal static string PreviousName(string log) => $"{Path.GetFileNameWithoutExtension(log)}-precedent{Path.GetExtension(log)}";

    /// <summary>Fichiers du dossier dont le nom suit le modèle, avec la date lue dans ce nom.</summary>
    private static IEnumerable<(string Path, DateTime Date)> Dated(string folder, Regex pattern)
    {
        if (!Directory.Exists(folder)) yield break;
        foreach (var path in Directory.EnumerateFiles(folder))
        {
            var match = pattern.Match(Path.GetFileName(path));
            if (match.Success && DateTime.TryParseExact(match.Groups["date"].Value, "yyyy-MM-dd_HHmmss",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                yield return (path, date);
        }
    }

    /// <summary>Journal au-delà de 1 Mo : renommé en « nom-precedent.log » (qui remplace l'archive précédente).</summary>
    private static bool RotateLog(string root, string name, List<string> problems)
    {
        var log = new FileInfo(Path.Combine(root, name));
        if (!log.Exists || log.Length <= MaxLogBytes) return false;
        try
        {
            File.Move(log.FullName, Path.Combine(root, PreviousName(name)), overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{name} : {e.Message}");
            return false;
        }
    }

    private static bool Delete(string path, List<string> problems)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problems.Add($"{Path.GetFileName(path)} : {e.Message}");
            return false;
        }
    }
}
