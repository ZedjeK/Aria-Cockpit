using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aria.Core.Backup;

namespace Aria.Core.Nvidia;

/// <summary>
/// Ce que l'utilisateur faisait au moment d'un incident du pilote (« en jeu sur tel titre », « IA locale », « au
/// repos »…) : le journal de Windows ne le dit pas, et c'est ce qui permet de relier un incident à un réglage.
/// Enregistré dans incidents-notes.json, une note par incident (repéré par l'heure de sa première ligne).
/// </summary>
public static class IncidentNotes
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public const int MaxLength = 500;

    public static string FilePath => Path.Combine(BackupStore.DataRoot, "incidents-notes.json");

    /// <summary>Clé d'un incident : l'heure de sa première ligne, à la seconde.</summary>
    public static string Key(DateTime at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    /// <summary>Notes enregistrées ; aucune si le fichier est absent ou illisible.</summary>
    public static IReadOnlyDictionary<string, string> Load()
    {
        try
        {
            return File.Exists(FilePath) ? FromJson(File.ReadAllText(FilePath)) : new Dictionary<string, string>();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>Enregistre la note d'un incident ; une note vide l'efface. Lève une exception d'écriture si le fichier est refusé.</summary>
    public static void Set(DateTime at, string note)
    {
        var notes = With(Load(), at, note);
        Directory.CreateDirectory(BackupStore.DataRoot);
        File.WriteAllText(FilePath, ToJson(notes));
    }

    internal static Dictionary<string, string> With(IReadOnlyDictionary<string, string> notes, DateTime at, string note)
    {
        var updated = new Dictionary<string, string>(notes);
        var text = note.Trim();
        if (text.Length > MaxLength) text = text[..MaxLength];
        if (text.Length == 0) updated.Remove(Key(at));
        else updated[Key(at)] = text;
        return updated;
    }

    internal static string ToJson(IReadOnlyDictionary<string, string> notes) => JsonSerializer.Serialize(notes, Json);

    internal static IReadOnlyDictionary<string, string> FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, Json) ?? [];
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return new Dictionary<string, string>();
        }
    }
}

/// <summary>Ce qu'il faut pour écrire le rapport d'incidents. Rien d'identifiant : ni nom du PC, ni utilisateur, ni numéro de carte.</summary>
/// <param name="TuningLog">Lignes de gpu-profile.log (« 2026-10-30 14:06:46 Appliqué : … »), les plus anciennes d'abord.</param>
/// <param name="Balanced">Résultat d'Équilibre, en une phrase ; null s'il n'y en a pas.</param>
/// <param name="FanCurve">Courbe de ventilation en cours ; null si le pilote a la main.</param>
/// <param name="Profiles">Profils enregistrés : nom et réglage.</param>
/// <param name="Days">Période couverte, en jours.</param>
public sealed record IncidentReportInput(
    DateTime GeneratedAt,
    string AppVersion,
    string GpuName,
    string DriverVersion,
    string Windows,
    GpuTuningState? Current,
    GpuTuningState? Origin,
    DateTime? AppliedAt,
    string? Balanced,
    string? FanCurve,
    IReadOnlyList<(string Name, string Tuning)> Profiles,
    IReadOnlyList<string> TuningLog,
    IReadOnlyList<DriverIncidentDetail> Incidents,
    IReadOnlyDictionary<string, string> Notes,
    int Days);

/// <summary>
/// Rapport d'incidents du pilote graphique, en Markdown, fait pour être collé dans une IA (ou envoyé à quelqu'un) :
/// la carte, le réglage en place, l'historique des réglages, et chaque incident avec ses lignes brutes, le réglage qui
/// était en place à ce moment-là et le commentaire de l'utilisateur.
/// </summary>
public static partial class IncidentReport
{
    [GeneratedRegex(@"^(?<at>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) (?<text>.*)$")]
    private static partial Regex LogLine();

    /// <summary>Chemin sous un profil d'utilisateur : le nom de l'utilisateur n'a rien à faire dans un rapport partagé.</summary>
    [GeneratedRegex(@"\\Users\\[^\\|""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UserFolder();

    public static string Build(IncidentReportInput input)
    {
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var log = ParseLog(input.TuningLog);
        var since = input.GeneratedAt.AddDays(-input.Days);
        var b = new StringBuilder();

        b.AppendLine("# Incidents du pilote graphique : rapport Aria Cockpit");
        b.AppendLine();
        b.AppendLine("Je règle ma carte graphique (limite de puissance, plafond et décalages de fréquence) avec Aria Cockpit. " +
                     "Voici les incidents que le pilote NVIDIA a notés dans le journal Système de Windows, avec le réglage en place à chaque fois " +
                     "et ce que je faisais. Aide-moi à comprendre s'ils viennent de mon réglage, et quoi changer pour le rendre stable.");
        b.AppendLine();
        b.AppendLine($"Rapport du {input.GeneratedAt.ToString("dd/MM/yyyy 'à' HH:mm", fr)}, sur les {input.Days} derniers jours. Aria Cockpit {input.AppVersion}.");
        b.AppendLine();

        b.AppendLine("## Matériel");
        b.AppendLine();
        b.AppendLine($"- Carte graphique : {input.GpuName}");
        b.AppendLine($"- Pilote NVIDIA : {input.DriverVersion}");
        b.AppendLine($"- Système : {input.Windows}");
        b.AppendLine();

        b.AppendLine("## Réglage");
        b.AppendLine();
        b.AppendLine($"- En place : {input.Current?.Describe() ?? "illisible"}" +
                     (input.AppliedAt is { } applied ? $" (posé le {applied.ToString("dd/MM/yyyy 'à' HH:mm", fr)})" : ""));
        b.AppendLine($"- Valeurs d'usine : {input.Origin?.Describe() ?? "aucun réglage d'Aria enregistré"}");
        if (input.Balanced is not null) b.AppendLine($"- Assistant Équilibre : {input.Balanced}");
        b.AppendLine($"- Ventilateurs : {(input.FanCurve is null ? "pilote NVIDIA (automatique)" : $"courbe « {input.FanCurve} » pilotée par Aria")}");
        foreach (var (name, tuning) in input.Profiles) b.AppendLine($"- Profil enregistré « {name} » : {tuning}");
        b.AppendLine();
        b.AppendLine("Lecture : « GPU +145 MHz » décale la courbe fréquence/tension (undervolt ou overclock selon le plafond), " +
                     "« plafond » borne la fréquence, « mémoire +750 MHz » pousse la mémoire vidéo.");
        b.AppendLine();

        b.AppendLine("## Historique des réglages");
        b.AppendLine();
        var changes = Changes(log.Where(l => l.At >= since));
        var reapplied = log.Count(l => l.At >= since && l.Text.StartsWith("Réappliqué", StringComparison.Ordinal));
        if (changes.Count == 0) b.AppendLine("Aucun changement de réglage sur la période.");
        if (changes.Count > MaxChanges)
            b.AppendLine($"- ({changes.Count - MaxChanges} changements plus anciens non listés : paliers d'essai de l'assistant, pour la plupart.)");
        foreach (var line in changes.TakeLast(MaxChanges)) b.AppendLine($"- {line.At.ToString("dd/MM HH:mm", fr)} : {line.Text}");
        if (reapplied > 0) b.AppendLine($"- Le réglage gardé a été réappliqué {reapplied} fois à l'ouverture de session.");
        b.AppendLine();

        b.AppendLine($"## Incidents ({input.Incidents.Count})");
        b.AppendLine();
        if (input.Incidents.Count == 0) b.AppendLine("Aucun incident du pilote sur la période.");
        var number = 0;
        foreach (var detail in input.Incidents.OrderBy(d => d.Incident.At))
        {
            var incident = detail.Incident;
            b.AppendLine($"### {++number}. {incident.At.ToString("dddd dd/MM/yyyy 'à' HH:mm:ss", fr)} : {DriverIncidents.Describe(incident.Kind)}");
            b.AppendLine();
            b.AppendLine($"- Réglage en place à ce moment-là : {TuningAt(log, incident.At) ?? "inconnu (aucun réglage d'Aria noté avant)"}");
            b.AppendLine($"- Ce que je faisais : {(input.Notes.TryGetValue(IncidentNotes.Key(incident.At), out var note) && note.Length > 0 ? note : "non précisé")}");
            b.AppendLine($"- Lignes du pilote ({detail.Lines.Count}) :");
            foreach (var line in detail.Lines)
                b.AppendLine($"  - {line.At:HH:mm:ss} {line.Provider} {line.Id} : {Compact(line.Data)}");
            b.AppendLine();
        }

        b.AppendLine("## Ce que j'attends");
        b.AppendLine();
        b.AppendLine("1. Pour chaque incident : est-il plausiblement lié au réglage en place, ou est-ce une erreur qui existe aussi sans réglage ?");
        b.AppendLine("2. Quel réglage essayer ensuite (quel paramètre baisser, de combien), et comment vérifier qu'il tient.");
        b.AppendLine("3. Ce qu'il te manque pour conclure, si ces informations ne suffisent pas.");
        return b.ToString();
    }

    /// <summary>Changements de réglage listés au plus : l'assistant en fait des dizaines par passage, seuls les derniers éclairent un incident.</summary>
    public const int MaxChanges = 40;

    /// <summary>
    /// Changements de réglage, sans le bruit : ni les réapplications à l'ouverture de session (comptées à part), ni le
    /// rappel « (avant : …) » de chaque ligne, ni une ligne qui repose le réglage déjà en place.
    /// </summary>
    private static List<(DateTime At, string Text)> Changes(IEnumerable<(DateTime At, string Text)> log)
    {
        var kept = new List<(DateTime, string)>();
        string? last = null;
        foreach (var (at, text) in log.OrderBy(l => l.At))
        {
            if (text.StartsWith("Réappliqué", StringComparison.Ordinal)) continue;
            var cut = text.IndexOf(" (avant : ", StringComparison.Ordinal);
            var shown = cut < 0 ? text : text[..cut];
            if (shown == last) continue;
            kept.Add((at, shown));
            last = shown;
        }
        return kept;
    }

    /// <summary>Réglage en place à un instant : celui de la dernière ligne « Appliqué » ou « Réappliqué » du journal avant lui.</summary>
    public static string? TuningAt(IReadOnlyList<string> tuningLog, DateTime at) => TuningAt(ParseLog(tuningLog), at);

    private static string? TuningAt(IReadOnlyList<(DateTime At, string Text)> log, DateTime at)
    {
        foreach (var (_, text) in log.Where(l => l.At <= at).OrderByDescending(l => l.At))
        {
            var start = text.StartsWith("Appliqué : ", StringComparison.Ordinal) ? "Appliqué : ".Length
                : text.StartsWith("Réappliqué : ", StringComparison.Ordinal) ? "Réappliqué : ".Length
                : -1;
            if (start < 0) continue;
            var end = text.IndexOf(" (avant : ", start, StringComparison.Ordinal);
            return (end < 0 ? text[start..] : text[start..end]).Trim();
        }
        return null;
    }

    private static List<(DateTime At, string Text)> ParseLog(IReadOnlyList<string> lines)
    {
        var parsed = new List<(DateTime, string)>();
        foreach (var line in lines)
        {
            var match = LogLine().Match(line);
            if (match.Success && DateTime.TryParseExact(match.Groups["at"].Value, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var at))
                parsed.Add((at, match.Groups["text"].Value));
        }
        return parsed;
    }

    /// <summary>
    /// Données d'une ligne du pilote, lisibles : les longues suites de zéros sont résumées (« 0 ×28 »), et un chemin
    /// sous un profil d'utilisateur perd le nom de l'utilisateur.
    /// </summary>
    public static string Compact(string data)
    {
        var fields = data.Split(" | ");
        var kept = new List<string>();
        for (var i = 0; i < fields.Length; i++)
        {
            var run = 1;
            while (fields[i] == "0" && i + run < fields.Length && fields[i + run] == "0") run++;
            if (fields[i] == "0" && run >= 4)
            {
                kept.Add($"0 ×{run}");
                i += run - 1;
            }
            else kept.Add(fields[i]);
        }
        return UserFolder().Replace(string.Join(" | ", kept), @"\Users\…");
    }
}
