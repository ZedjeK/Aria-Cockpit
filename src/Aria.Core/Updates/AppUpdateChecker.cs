using System.Text.Json;

namespace Aria.Core.Updates;

/// <summary>Une version d'Aria plus récente que celle installée, publiée sur GitHub.</summary>
public sealed record AppUpdate(Version Latest, string PageUrl);

/// <summary>
/// Vérifie s'il existe une release d'Aria plus récente sur GitHub (dernière release publiée du dépôt).
/// Ne télécharge et n'installe rien : il renvoie seulement la version et l'adresse de sa page.
/// Toute erreur (pas de réseau, GitHub indisponible, réponse inattendue) donne « pas de mise à jour ».
/// </summary>
public static class AppUpdateChecker
{
    public const string RepositoryUrl = "https://github.com/ZedjeK/Aria-PC-Tune";
    private const string LatestReleaseApi = "https://api.github.com/repos/ZedjeK/Aria-PC-Tune/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    public static async Task<AppUpdate?> CheckAsync(Version current, CancellationToken cancel = default)
    {
        try
        {
            var json = await Http.GetStringAsync(LatestReleaseApi, cancel).ConfigureAwait(false);
            return Parse(json, current);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Lit la réponse de l'API GitHub ; null si la release n'est pas plus récente ou si la réponse est inutilisable.</summary>
    internal static AppUpdate? Parse(string json, Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } tagName) return null;
            if (!TryParseVersion(tagName, out var latest) || latest <= Normalize(current)) return null;

            var url = root.TryGetProperty("html_url", out var page) ? page.GetString() : null;
            // Seule une page de ce dépôt est proposée à l'ouverture
            if (url is null || !url.StartsWith(RepositoryUrl + "/", StringComparison.OrdinalIgnoreCase))
                url = $"{RepositoryUrl}/releases/latest";
            return new AppUpdate(latest, url);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>« v0.15.0 » ou « 0.15.0 » → 0.15.0 (3 composants, pour comparer proprement).</summary>
    public static bool TryParseVersion(string text, out Version version)
    {
        var trimmed = text.Trim().TrimStart('v', 'V');
        var plus = trimmed.IndexOfAny(['+', '-']);
        if (plus >= 0) trimmed = trimmed[..plus];
        if (Version.TryParse(trimmed, out var parsed))
        {
            version = Normalize(parsed);
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    private static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0));

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aria-PC-Tune");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
