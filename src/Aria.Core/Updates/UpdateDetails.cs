using System.Text.RegularExpressions;

namespace Aria.Core.Updates;

/// <summary>Notes de version d'une application, lues dans son paquet winget.</summary>
/// <param name="Url">Page des notes de version (http ou https), ou null.</param>
/// <param name="Text">Texte des notes de version quand le paquet le fournit, raccourci, ou null.</param>
public sealed record PackageNotes(string? Url, string? Text)
{
    public static readonly PackageNotes None = new(null, null);
}

/// <summary>
/// Ce qu'on peut dire d'une mise à jour : description et liens de Windows Update, notes de version des paquets winget.
/// Toutes les adresses passent par <see cref="SafeUrl"/> : elles viennent d'éditeurs tiers et sont ouvertes par
/// l'Explorateur, qui lancerait aussi bien un programme local.
/// </summary>
public static partial class UpdateDetails
{
    /// <summary>Lignes et caractères gardés des notes de version : un aperçu, le lien donne le reste.</summary>
    internal const int MaxNoteLines = 8;
    internal const int MaxNoteChars = 600;

    /// <summary>Définitions Microsoft Defender (quotidiennes) et plateforme antivirus : ne sont pas des correctifs de Windows.</summary>
    internal static readonly HashSet<string> DefenderKbs = ["2267602", "4052623", "2311978", "915597"];

    /// <summary>
    /// Numéro d'article : « KB5127936 », ou « 2267602 Ko » sur un Windows en français (« KB » y est traduit).
    /// Les applications du Microsoft Store et les pilotes n'en ont pas.
    /// </summary>
    [GeneratedRegex(@"\bKB\s?(?<kb>\d{6,8})\b|\b(?<kb>\d{6,8})\s?Ko\b", RegexOptions.IgnoreCase)]
    private static partial Regex KbNumber();

    /// <summary>Mise à jour du Microsoft Store dans l'historique : « 9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2 ».</summary>
    [GeneratedRegex(@"^(?<store>[0-9A-Z]{12})-(?<name>\S.*)$")]
    private static partial Regex StoreTitle();

    /// <summary>Ligne « libellé : valeur » de winget show (libellé en début de ligne).</summary>
    [GeneratedRegex(@"^(?<label>[^\s:][^:]*?)\s*:\s*(?<value>.*)$")]
    private static partial Regex LabelLine();

    /// <summary>Numéro KB lu dans un titre, ou null.</summary>
    public static string? KbOf(string title) => KbNumber().Match(title) is { Success: true } m ? m.Groups["kb"].Value : null;

    /// <summary>
    /// Adresse absolue en http ou https, sinon null (chemin local, file:, commande…). Les entités HTML que Windows Update
    /// laisse dans certaines adresses (« &amp;amp; ») sont décodées.
    /// </summary>
    public static string? SafeUrl(string? text) =>
        Uri.TryCreate(System.Net.WebUtility.HtmlDecode(text?.Trim()), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? uri.AbsoluteUri
            : null;

    /// <summary>Article de Microsoft sur une mise à jour : support.microsoft.com/help/&lt;numéro KB&gt;.</summary>
    public static string KbArticle(string kb) => $"https://support.microsoft.com/help/{kb.Replace("KB", "", StringComparison.OrdinalIgnoreCase)}";

    /// <summary>
    /// Lien d'une mise à jour proposée par Windows Update : la page « plus d'infos » que Microsoft y attache, sinon
    /// l'article KB (sauf définitions Defender, qui n'ont pas d'article), sinon la page de support.
    /// </summary>
    internal static string? WindowsUpdateLink(IEnumerable<string?> moreInfo, string? kb, string? supportUrl) =>
        moreInfo.Select(SafeUrl).FirstOrDefault(u => u is not null)
        ?? (kb is not null && !DefenderKbs.Contains(kb) ? KbArticle(kb) : null)
        ?? SafeUrl(supportUrl);

    /// <summary>Description de Windows Update sur une ligne, ou null si elle est vide ou ne dit rien de plus que le titre.</summary>
    internal static string? CleanDescription(string? description, string title)
    {
        var text = Regex.Replace(description ?? "", @"\s+", " ").Trim();
        if (text.Length == 0 || text.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        // Mises à jour du Store : la description n'est qu'un identifiant technique (« 9NRZT3Q9R3DL-1152921505702239086 »)
        return StoreTitle().IsMatch(text) && !text.Contains(' ') ? null : text;
    }

    /// <summary>« Corrige 37 failles de sécurité (CVE) », ou null s'il n'y en a pas.</summary>
    public static string? CveSummary(int count) => count switch
    {
        <= 0 => null,
        1 => "Corrige 1 faille de sécurité (CVE)",
        _ => $"Corrige {count} failles de sécurité (CVE)"
    };

    /// <summary>Titre lisible dans l'historique : les mises à jour du Microsoft Store perdent leur identifiant technique.</summary>
    public static string TidyTitle(string title) =>
        StoreTitle().Match(title.Trim()) is { Success: true } m ? $"Microsoft Store · {m.Groups["name"].Value}" : title.Trim();

    /// <summary>
    /// Notes de version dans la sortie de « winget show » : lien (« Release Notes Url » ou « URL des notes de
    /// publication ») et texte (« Release Notes » ou « Notes de publication », lignes en retrait qui suivent).
    /// winget traduit ses libellés selon la langue de Windows : anglais et français sont reconnus.
    /// </summary>
    internal static PackageNotes ParseWingetShow(string output)
    {
        string? url = null;
        var text = new List<string>();
        var lines = output.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            // Barre de progression réécrite par « \r » : on garde le dernier état de la ligne
            var cr = line.LastIndexOf('\r');
            if (cr >= 0) line = line[(cr + 1)..];
            if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;
            if (LabelLine().Match(line) is not { Success: true } m) continue;

            var label = m.Groups["label"].Value.Trim().ToLowerInvariant();
            var value = m.Groups["value"].Value.Trim();
            if (label is "release notes url" or "url des notes de publication")
                url = SafeUrl(value);
            else if (label is "release notes" or "notes de publication")
            {
                if (value.Length > 0) text.Add(value);
                // Lignes en retrait qui suivent ; une ligne vide ne coupe le bloc que si la suite n'est plus en retrait
                for (var j = i + 1; j < lines.Length; j++)
                {
                    var next = lines[j].TrimEnd('\r');
                    if (next.Trim().Length == 0)
                    {
                        if (lines.Skip(j + 1).FirstOrDefault(l => l.Trim().Length > 0) is { Length: > 0 } after && char.IsWhiteSpace(after[0]))
                            continue;
                        break;
                    }
                    if (!char.IsWhiteSpace(next[0])) break;
                    text.Add(next.Trim());
                }
            }
        }
        return new PackageNotes(url, Shorten(text));
    }

    /// <summary>Aperçu : quelques lignes, quelques centaines de caractères, « … » si c'est coupé.</summary>
    internal static string? Shorten(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return null;
        var kept = string.Join("\n", lines.Take(MaxNoteLines));
        var cut = lines.Count > MaxNoteLines;
        if (kept.Length > MaxNoteChars)
        {
            kept = kept[..MaxNoteChars].TrimEnd();
            cut = true;
        }
        return cut ? kept + " …" : kept;
    }
}
