namespace Aria.Core.Updates;

public enum HistoryResult { Succeeded, SucceededWithErrors, Failed, Aborted, InProgress }

/// <summary>Une ligne de l'historique de Windows Update.</summary>
/// <param name="ByAria">Installée depuis Aria Cockpit (et non par Windows lui-même, en arrière-plan).</param>
/// <param name="Url">Article ou page d'information (http ou https), ou null.</param>
public sealed record UpdateHistoryEntry(DateTime Date, string Title, bool IsUninstall, HistoryResult Result, bool ByAria, string? Url);

/// <summary>Historique prêt à afficher.</summary>
/// <param name="GroupedDefinitions">Mises à jour des définitions Defender masquées : seule la plus récente est gardée.</param>
public sealed record HistoryView(IReadOnlyList<UpdateHistoryEntry> Entries, int GroupedDefinitions);

/// <summary>
/// Historique de Windows Update : tout ce que Windows a installé ou désinstallé, y compris ce qu'il fait seul en
/// arrière-plan (définitions Defender, mises à jour du Microsoft Store…). Lecture seule, par l'interface officielle.
/// </summary>
public static class WindowsUpdateHistory
{
    /// <summary>Définitions antivirus de Defender (KB2267602), installées plusieurs fois par jour.</summary>
    private const string DefinitionsKb = "2267602";

    /// <summary>
    /// Les <paramref name="max"/> dernières opérations, définitions Defender regroupées : elles arrivent plusieurs fois
    /// par jour et noieraient le reste. Lève une exception si Windows Update est illisible.
    /// </summary>
    public static HistoryView ReadView(int max) => Group(Read(max * 4), max);

    internal static HistoryView Group(IReadOnlyList<UpdateHistoryEntry> entries, int max)
    {
        var kept = new List<UpdateHistoryEntry>();
        var grouped = 0;
        var definitionsShown = false;
        foreach (var entry in entries)
        {
            if (kept.Count == max) break;
            if (UpdateDetails.KbOf(entry.Title) == DefinitionsKb && !entry.IsUninstall)
            {
                if (definitionsShown)
                {
                    grouped++;
                    continue;
                }
                definitionsShown = true;
            }
            kept.Add(entry);
        }
        return new HistoryView(kept, grouped);
    }

    /// <summary>
    /// Identifiants qu'Aria donne à ses sessions Windows Update : « Aria Cockpit » (voir <see cref="WindowsUpdateService"/>),
    /// « Aria » pour les versions 0.13 et 0.14. Windows lui-même signe « MoUpdateOrchestrator », Defender « Windows Defender ».
    /// </summary>
    internal static readonly string[] AriaClientIds = ["Aria Cockpit", "Aria"];

    /// <summary>Les dernières opérations, la plus récente en premier. Lève une exception si Windows Update est illisible.</summary>
    public static IReadOnlyList<UpdateHistoryEntry> Read(int max)
    {
        var session = Com.Create("Microsoft.Update.Session");
        var searcher = Com.Call(session, "CreateUpdateSearcher")!;
        var total = Convert.ToInt32(Com.Call(searcher, "GetTotalHistoryCount"));
        if (total == 0) return [];
        var history = Com.Call(searcher, "QueryHistory", 0, Math.Min(total, max));
        return Com.Items(history)
            .Select(e => Map(
                operation: Convert.ToInt32(Com.Get(e, "Operation")),
                resultCode: Convert.ToInt32(Com.Get(e, "ResultCode")),
                date: Com.Get(e, "Date") is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc).ToLocalTime() : DateTime.MinValue,
                title: Com.Str(e, "Title"),
                clientId: Com.Str(e, "ClientApplicationID"),
                supportUrl: Com.Str(e, "SupportUrl")))
            .Where(e => e.Title.Length > 0)
            .OrderByDescending(e => e.Date)
            .ToList();
    }

    internal static UpdateHistoryEntry Map(int operation, int resultCode, DateTime date, string title, string clientId, string? supportUrl)
    {
        var kb = UpdateDetails.KbOf(title);
        var url = kb is not null && !UpdateDetails.DefenderKbs.Contains(kb) ? UpdateDetails.KbArticle(kb) : UpdateDetails.SafeUrl(supportUrl);
        var result = resultCode switch
        {
            2 => HistoryResult.Succeeded,
            3 => HistoryResult.SucceededWithErrors,
            4 => HistoryResult.Failed,
            5 => HistoryResult.Aborted,
            _ => HistoryResult.InProgress
        };
        return new UpdateHistoryEntry(date, UpdateDetails.TidyTitle(title), operation == 2, result,
            AriaClientIds.Contains(clientId.Trim(), StringComparer.OrdinalIgnoreCase), url);
    }

    public static string Describe(HistoryResult result) => result switch
    {
        HistoryResult.Succeeded => "réussie",
        HistoryResult.SucceededWithErrors => "réussie avec des erreurs",
        HistoryResult.Failed => "échouée",
        HistoryResult.Aborted => "interrompue",
        _ => "en cours"
    };
}
