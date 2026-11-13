using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aria.Core.Updates;

/// <summary>Un fichier joint à une release d'Aria Cockpit, tel que GitHub le décrit.</summary>
/// <param name="Name">Nom du fichier (« Aria-Cockpit-0.27.0-autonome.zip »).</param>
/// <param name="Url">Adresse de téléchargement, sur la release de ce dépôt.</param>
/// <param name="Size">Taille annoncée, en octets.</param>
/// <param name="Sha256">Empreinte SHA-256 annoncée par GitHub, 64 chiffres hexadécimaux en minuscules.</param>
public sealed record AppUpdateAsset(string Name, string Url, long Size, string Sha256);

/// <summary>Une version d'Aria Cockpit plus récente que celle installée, publiée sur GitHub.</summary>
public sealed record AppUpdate(Version Latest, string PageUrl)
{
    /// <summary>Fichiers téléchargeables de la release : seulement ceux de ce dépôt, avec une empreinte.</summary>
    public IReadOnlyList<AppUpdateAsset> Assets { get; init; } = [];

    /// <summary>Premières lignes des nouveautés, reprises des notes de la release, sans mise en forme.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>Ce qu'une recherche de nouvelle version a trouvé.</summary>
public enum AppUpdateLookup
{
    /// <summary>Une version plus récente est publiée.</summary>
    Newer,
    /// <summary>GitHub a répondu, et la version installée est la dernière publiée (ou plus récente).</summary>
    UpToDate,
    /// <summary>Pas de réseau, GitHub indisponible ou réponse inattendue : on ne sait pas.</summary>
    Unreachable
}

/// <summary>
/// Vérifie s'il existe une release d'Aria Cockpit plus récente sur GitHub (dernière release publiée du dépôt).
/// Ne télécharge et n'installe rien : il renvoie la version, l'adresse de sa page et la liste de ses fichiers.
/// Toute erreur (pas de réseau, GitHub indisponible, réponse inattendue) donne « pas de mise à jour ».
/// </summary>
public static partial class AppUpdateChecker
{
    public const string RepositoryUrl = "https://github.com/ZedjeK/Aria-Cockpit";

    /// <summary>Début obligatoire de l'adresse d'un fichier : une release de ce dépôt, en HTTPS, sur github.com.</summary>
    public const string DownloadPrefix = RepositoryUrl + "/releases/download/";

    private const string LatestReleaseApi = "https://api.github.com/repos/ZedjeK/Aria-Cockpit/releases/latest";

    /// <summary>Nombre de lignes de nouveautés gardées, et longueur de chacune.</summary>
    internal const int MaxNotes = 5, MaxNoteLength = 160;

    private static readonly HttpClient Http = CreateClient();

    public static async Task<AppUpdate?> CheckAsync(Version current, CancellationToken cancel = default) =>
        (await LookAsync(current, cancel).ConfigureAwait(false)).Update;

    /// <summary>
    /// Comme <see cref="CheckAsync"/>, en disant aussi pourquoi aucune version n'est proposée : parce que celle-ci est
    /// la dernière, ou parce que GitHub n'a pas donné de réponse utilisable.
    /// </summary>
    public static async Task<(AppUpdateLookup Outcome, AppUpdate? Update)> LookAsync(Version current, CancellationToken cancel = default)
    {
        string json;
        try
        {
            json = await Http.GetStringAsync(LatestReleaseApi, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return (AppUpdateLookup.Unreachable, null);
        }
        return Classify(json, current);
    }

    /// <summary>Lit la réponse de GitHub : version plus récente, version à jour, ou réponse inutilisable.</summary>
    internal static (AppUpdateLookup Outcome, AppUpdate? Update) Classify(string json, Version current)
    {
        if (Parse(json, current) is { } update) return (AppUpdateLookup.Newer, update);
        return (HasReleaseTag(json) ? AppUpdateLookup.UpToDate : AppUpdateLookup.Unreachable, null);
    }

    /// <summary>La réponse décrit bien une release, avec un numéro de version lisible.</summary>
    private static bool HasReleaseTag(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("tag_name", out var tag)
                   && Text(tag) is { } name
                   && TryParseVersion(name, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Lit la réponse de l'API GitHub ; null si la release n'est pas plus récente ou si la réponse est inutilisable.</summary>
    internal static AppUpdate? Parse(string json, Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True) return null;
            if (!root.TryGetProperty("tag_name", out var tag) || Text(tag) is not { } tagName) return null;
            if (!TryParseVersion(tagName, out var latest) || latest <= Normalize(current)) return null;

            var url = root.TryGetProperty("html_url", out var page) ? Text(page) : null;
            // Seule une page de ce dépôt est proposée à l'ouverture
            if (url is null || !url.StartsWith(RepositoryUrl + "/", StringComparison.OrdinalIgnoreCase))
                url = $"{RepositoryUrl}/releases/latest";
            return new AppUpdate(latest, url)
            {
                Assets = ReadAssets(root),
                Notes = root.TryGetProperty("body", out var body) && Text(body) is { } text ? ReadNotes(text) : []
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fichiers de la release : une adresse de ce dépôt, une taille, et l'empreinte SHA-256 que GitHub calcule à
    /// l'envoi du fichier (champ « digest »). Un fichier sans empreinte est écarté : il ne pourrait pas être contrôlé.
    /// </summary>
    private static IReadOnlyList<AppUpdateAsset> ReadAssets(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return [];
        var found = new List<AppUpdateAsset>();
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind != JsonValueKind.Object) continue;
            if (!asset.TryGetProperty("name", out var n) || Text(n) is not { } name) continue;
            if (!asset.TryGetProperty("browser_download_url", out var u) || Text(u) is not { } url) continue;
            if (!url.StartsWith(DownloadPrefix, StringComparison.Ordinal)) continue;
            if (!asset.TryGetProperty("size", out var s) || !s.TryGetInt64(out var size) || size <= 0) continue;
            if (!asset.TryGetProperty("digest", out var d) || Text(d) is not { } digest) continue;
            if (ParseSha256(digest) is not { } sha) continue;
            found.Add(new AppUpdateAsset(name, url, size, sha));
        }
        return found;
    }

    /// <summary>« sha256:ABC… » → les 64 chiffres en minuscules ; null pour tout autre format.</summary>
    internal static string? ParseSha256(string digest)
    {
        const string prefix = "sha256:";
        if (!digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var hex = digest[prefix.Length..].Trim().ToLowerInvariant();
        return hex.Length == 64 && hex.All(Uri.IsHexDigit) ? hex : null;
    }

    /// <summary>
    /// Nouveautés à afficher : les puces de la section « Nouveautés » des notes (à défaut, les premières puces du
    /// texte), sans gras ni code, coupées à <see cref="MaxNoteLength"/> caractères.
    /// </summary>
    internal static IReadOnlyList<string> ReadNotes(string body)
    {
        var lines = body.Replace("\r", "").Split('\n');
        var start = Array.FindIndex(lines, l => l.StartsWith("## Nouveaut", StringComparison.OrdinalIgnoreCase));
        var notes = new List<string>();
        for (var i = start + 1; i < lines.Length && notes.Count < MaxNotes; i++)
        {
            var line = lines[i].Trim();
            // La section s'arrête au titre suivant
            if (start >= 0 && line.StartsWith('#')) break;
            if (!line.StartsWith("- ") && !line.StartsWith("* ")) continue;
            var text = Markup().Replace(line[2..], "").Trim();
            if (text.Length == 0) continue;
            notes.Add(text.Length <= MaxNoteLength ? text : text[..(MaxNoteLength - 1)].TrimEnd() + "…");
        }
        return notes;
    }

    [GeneratedRegex(@"\*\*|`")]
    private static partial Regex Markup();

    private static string? Text(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString() : null;

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
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aria-Cockpit");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}
