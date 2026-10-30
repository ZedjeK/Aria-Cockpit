using System.Diagnostics.Eventing.Reader;

namespace Aria.Core.Nvidia;

/// <summary>Une ligne du journal Système écrite par le pilote graphique (Display ou nvlddmkm).</summary>
public sealed record DriverEvent(DateTime At, string Provider, int Id, string Data);

/// <summary>Nature d'un incident, de la plus grave à la moins grave.</summary>
public enum DriverIncidentKind
{
    /// <summary>Erreur du pilote sans autre précision (nvlddmkm 153 seule, par exemple).</summary>
    DriverError,
    /// <summary>Exception d'un calcul sur la carte (Xid 13) d'un autre type que l'accès mémoire.</summary>
    GraphicsException,
    /// <summary>Accès mémoire invalide (Xid 13 « Out Of Range Address », « illegal memory access » côté CUDA).</summary>
    MemoryAccess,
    /// <summary>Le pilote a cessé de répondre et Windows l'a relancé (Display 4101 : écran noir quelques secondes).</summary>
    Reset
}

/// <summary>Un incident : les lignes que le pilote écrit d'un coup pour un même problème.</summary>
public sealed record DriverIncident(DateTime At, DriverIncidentKind Kind, int Events);

/// <summary>
/// Incidents du pilote graphique d'après le journal Système. Pour un seul problème, le pilote écrit souvent 5 à 7 lignes
/// dans la même seconde (exception, puis plusieurs erreurs 153) : les compter une par une affichait « 25 plantages » pour 4.
/// </summary>
public static class DriverIncidents
{
    /// <summary>Lignes séparées de moins que cela : même incident.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    /// <summary>Incidents depuis une date ; vide si le journal est illisible (pas d'alerte inventée).</summary>
    public static IReadOnlyList<DriverIncident> Since(DateTime since) => Group(ReadEvents(since));

    /// <summary>Regroupe les lignes proches dans le temps ; la nature de l'incident est la plus grave de ses lignes.</summary>
    public static IReadOnlyList<DriverIncident> Group(IEnumerable<DriverEvent> events)
    {
        var incidents = new List<DriverIncident>();
        DateTime? last = null;
        foreach (var e in events.OrderBy(e => e.At))
        {
            var kind = Classify(e);
            if (last is { } previous && e.At - previous <= Window && incidents.Count > 0)
            {
                var current = incidents[^1];
                incidents[^1] = current with { Kind = kind > current.Kind ? kind : current.Kind, Events = current.Events + 1 };
            }
            else
                incidents.Add(new DriverIncident(e.At, kind, 1));
            last = e.At;
        }
        return incidents;
    }

    public static DriverIncidentKind Classify(DriverEvent e) => e switch
    {
        { Provider: "Display", Id: 4101 } => DriverIncidentKind.Reset,
        { Provider: "nvlddmkm", Id: 13 } when e.Data.Contains("Out Of Range Address", StringComparison.OrdinalIgnoreCase)
                                          || e.Data.Contains("Illegal", StringComparison.OrdinalIgnoreCase) => DriverIncidentKind.MemoryAccess,
        { Provider: "nvlddmkm", Id: 13 } => DriverIncidentKind.GraphicsException,
        _ => DriverIncidentKind.DriverError
    };

    /// <summary>« accès mémoire invalide », pour les messages.</summary>
    public static string Describe(DriverIncidentKind kind) => kind switch
    {
        DriverIncidentKind.Reset => "pilote relancé par Windows",
        DriverIncidentKind.MemoryAccess => "accès mémoire invalide",
        DriverIncidentKind.GraphicsException => "exception d'un calcul sur la carte",
        _ => "erreur du pilote"
    };

    /// <summary>
    /// Message du garde-fou : incidents depuis le réglage, détaillés par nature, et conseil. Un accès mémoire invalide
    /// (« illegal memory access » sous CUDA) vient le plus souvent d'un cœur qui calcule faux, pas de la mémoire : sur la tour,
    /// l'undervolt d'Équilibre donnait 17 130 calculs faux à pleine puissance, et la mémoire à +1000 MHz aucune donnée relue
    /// fausse. Le conseil est donc de revenir d'origine et de refaire l'assistant, dont les paliers passent désormais un calcul
    /// vérifié (et un test de la mémoire vidéo). Des erreurs 153 seules existent aussi sans aucun réglage.
    /// </summary>
    public static string Advise(IReadOnlyList<DriverIncident> incidents, DateTime appliedAt, GpuTuningState current)
    {
        var detail = string.Join(", ", incidents.GroupBy(i => i.Kind).OrderByDescending(g => g.Key)
            .Select(g => $"{g.Count()} × {Describe(g.Key)}"));
        var text = $"{incidents.Count} incident{(incidents.Count > 1 ? "s" : "")} du pilote NVIDIA depuis le réglage du " +
                   $"{appliedAt:dd/MM à HH:mm} ({detail}), le dernier le {incidents[^1].At:dd/MM à HH:mm}. ";
        if (incidents.All(i => i.Kind == DriverIncidentKind.DriverError))
            return text + "Erreurs isolées du pilote, qui existent aussi sans aucun réglage : si elles se répètent en jeu, " +
                   "revenez au réglage d'origine pour comparer.";
        var cause = incidents.Any(i => i.Kind == DriverIncidentKind.MemoryAccess)
            ? "Un accès mémoire invalide (« illegal memory access » sous CUDA) vient le plus souvent d'un cœur graphique qui calcule faux " +
              "(décalage ou undervolt trop poussé), plus rarement de la mémoire. "
            : "Ce réglage est probablement instable, au moins sous un calcul intensif (IA locale, rendu). ";
        var checks = current.MemoryOffsetMHz > 0
            ? "un calcul vérifié à pleine puissance et un test de la mémoire vidéo"
            : "un calcul vérifié à pleine puissance";
        return text + cause + $"Revenez au réglage d'origine, puis relancez l'assistant avec le banc d'essai : ses paliers passent désormais {checks}.";
    }

    private static IReadOnlyList<DriverEvent> ReadEvents(DateTime since)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var ms = (long)Math.Max(0, (DateTime.Now - since).TotalMilliseconds);
        var query = new EventLogQuery("System", PathType.LogName,
            "*[System[((Provider[@Name='Display'] and EventID=4101) or (Provider[@Name='nvlddmkm'] and (Level=1 or Level=2)))" +
            $" and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
        var found = new List<DriverEvent>();
        try
        {
            using var reader = new EventLogReader(query);
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
                using (e)
                    if (e.TimeCreated is { } at)
                        found.Add(new DriverEvent(at, e.ProviderName, e.Id,
                            string.Join(" | ", e.Properties.Select(p => p.Value?.ToString() ?? ""))));
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException)
        {
            // Journal illisible : pas de garde-fou, mais pas d'alerte inventée
        }
        return found;
    }
}
