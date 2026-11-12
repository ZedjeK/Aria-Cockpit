using System.Text.Json;
using System.Text.Json.Serialization;
using Aria.Core.Backup;

namespace Aria.Core.Reporting;

/// <summary>Une opération du journal : quand, quoi, son bilan, et le rapport qui la détaille.</summary>
public sealed record JournalEntry(
    [property: JsonPropertyName("date")] DateTime At,
    [property: JsonPropertyName("type")] string Kind,
    [property: JsonPropertyName("bilan")] string Summary,
    [property: JsonPropertyName("rapport")] string Report);

/// <summary>
/// journal.json, dans le dossier des données : les 500 dernières opérations (optimisation, restauration, mises à
/// jour, nettoyage, réglage GPU), de la plus ancienne à la plus récente. Il s'ajoute aux rapports, il ne les remplace
/// pas : chaque ligne nomme le rapport qui la détaille. Une fiche PC n'est pas une opération, elle n'y figure pas.
/// </summary>
public static class OperationJournal
{
    public const string FileName = "journal.json";
    public const int MaxEntries = 500;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(BackupStore.DataRoot, FileName);

    /// <summary>
    /// Ajoute au journal l'opération que décrit ce rapport, tout juste écrit. Ne lève jamais : un journal qui ne
    /// s'écrit pas ne doit pas faire échouer l'opération, dont le rapport existe déjà.
    /// </summary>
    public static void Record(string reportPath) => Record(BackupStore.DataRoot, reportPath);

    internal static void Record(string folder, string reportPath)
    {
        try
        {
            if (ReportIndex.Describe(reportPath, File.ReadAllLines(reportPath)) is not { } info) return;
            if (info.Kind == ReportKind.PcSheet) return;
            var entry = new JournalEntry(info.At, KindName(info.Kind), info.Summary, Path.GetFileName(reportPath));
            lock (Gate)
            {
                var entries = Load(folder).ToList();
                entries.Add(entry);
                if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(entries, Json));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // Dossier en lecture seule, fichier verrouillé : l'opération a eu lieu et son rapport est là
        }
    }

    /// <summary>Opérations enregistrées, de la plus ancienne à la plus récente ; aucune si le fichier est absent ou illisible.</summary>
    public static IReadOnlyList<JournalEntry> Load() => Load(BackupStore.DataRoot);

    internal static IReadOnlyList<JournalEntry> Load(string folder)
    {
        try
        {
            var file = Path.Combine(folder, FileName);
            if (!File.Exists(file)) return [];
            return JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(file), Json) ?? [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    internal static string KindName(ReportKind kind) => kind switch
    {
        ReportKind.Simulation => "simulation",
        ReportKind.Restore => "restauration",
        ReportKind.Updates => "mises-a-jour",
        ReportKind.Cleanup => "nettoyage",
        ReportKind.GpuTuning => "reglage-gpu",
        ReportKind.PcSheet => "fiche-pc",
        _ => "optimisation"
    };
}
