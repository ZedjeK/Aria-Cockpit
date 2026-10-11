using System.Globalization;
using System.Text;

namespace Aria.Core.Updates;

public sealed record WingetRow(string Name, string Id, string Version, string Available, string? Source);

public sealed record WingetTable(IReadOnlyList<WingetRow> Rows, IReadOnlyList<string> Unreadable);

/// <summary>
/// Lecture du tableau texte de « winget upgrade ». winget n'offre pas de sortie structurée pour cette commande :
/// on repère les colonnes grâce à l'en-tête (quelle que soit la langue), en tenant compte des caractères
/// qui occupent deux colonnes à l'écran (chinois, japonais…). Une ligne douteuse n'est jamais proposée.
/// </summary>
public static partial class WingetParser
{
    public static WingetTable? ParseUpgrades(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n').Select(Clean).ToList();

        var separator = lines.FindIndex(l => l.Length >= 10 && l.All(c => c == '-'));
        if (separator < 1) return null;

        var starts = ColumnStarts(lines[separator - 1]);
        if (starts.Count is < 4 or > 5) return null;

        var rows = new List<WingetRow>();
        var unreadable = new List<string>();

        for (var i = separator + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) break; // fin du premier tableau

            var cells = Cut(line, starts);
            // Ligne de bilan (« 3 mises à niveau disponibles. ») : trop courte pour atteindre la colonne Id
            if (cells[1].Length == 0) break;

            var row = new WingetRow(cells[0], cells[1], cells[2], cells[3], starts.Count > 4 && cells[4].Length > 0 ? cells[4] : null);
            if (IsValid(row)) rows.Add(row);
            else unreadable.Add(string.IsNullOrEmpty(row.Name) ? line.Trim() : row.Name);
        }
        return new WingetTable(rows, unreadable);
    }

    private static bool IsValid(WingetRow row) =>
        row.Name.Length > 0
        && row.Id.Length > 0 && !row.Id.Any(char.IsWhiteSpace)
        && row.Version.Length > 0
        && row.Available.Length > 0 && row.Available.Any(char.IsDigit) && !row.Available.Any(char.IsWhiteSpace)
        && (row.Source is null || !row.Source.Any(char.IsWhiteSpace));

    /// <summary>winget réécrit sa ligne (animation, progression) avec « \r » : on garde le dernier état.</summary>
    private static string Clean(string line)
    {
        var last = line.LastIndexOf('\r');
        return StripVt((last >= 0 ? line[(last + 1)..] : line)).TrimEnd();
    }

    /// <summary>Retire les séquences de contrôle du terminal (couleurs, effacement de ligne) si winget en émet.</summary>
    internal static string StripVt(string text) => text.Contains('\u001b') ? VtSequence().Replace(text, "") : text;

    [System.Text.RegularExpressions.GeneratedRegex(@"\u001b\[[0-9;?]*[ -/]*[@-~]|\u001b\][^\u0007]*\u0007")]
    private static partial System.Text.RegularExpressions.Regex VtSequence();

    /// <summary>Colonne d'écran où commence chaque mot de l'en-tête.</summary>
    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        var column = 0;
        var previousSpace = true;
        foreach (var element in TextElements(header))
        {
            var isSpace = element == " ";
            if (!isSpace && previousSpace) starts.Add(column);
            previousSpace = isSpace;
            column += Width(element);
        }
        return starts;
    }

    /// <summary>Découpe une ligne selon les colonnes d'écran de l'en-tête.</summary>
    private static List<string> Cut(string line, IReadOnlyList<int> starts)
    {
        var cells = starts.Select(_ => new StringBuilder()).ToList();
        var column = 0;
        foreach (var element in TextElements(line))
        {
            var index = 0;
            while (index + 1 < starts.Count && column >= starts[index + 1]) index++;
            cells[index].Append(element);
            column += Width(element);
        }
        return cells.Select(c => c.ToString().Trim()).ToList();
    }

    private static IEnumerable<string> TextElements(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext()) yield return enumerator.GetTextElement();
    }

    /// <summary>Largeur à l'écran : 2 pour les caractères « larges » (Asie de l'Est, émojis), 1 sinon.</summary>
    internal static int Width(string element)
    {
        var rune = Rune.GetRuneAt(element, 0);
        var v = rune.Value;
        var wide = v is >= 0x1100 and <= 0x115F
                   or >= 0x2E80 and <= 0x303E
                   or >= 0x3041 and <= 0x33FF
                   or >= 0x3400 and <= 0x4DBF
                   or >= 0x4E00 and <= 0x9FFF
                   or >= 0xA000 and <= 0xA4CF
                   or >= 0xAC00 and <= 0xD7A3
                   or >= 0xF900 and <= 0xFAFF
                   or >= 0xFE30 and <= 0xFE4F
                   or >= 0xFF00 and <= 0xFF60
                   or >= 0xFFE0 and <= 0xFFE6
                   or >= 0x1F300 and <= 0x1FAFF
                   or >= 0x20000 and <= 0x3FFFD;
        return wide ? 2 : 1;
    }

    /// <summary>Dernière ligne utile de la sortie (souvent le message d'erreur de winget).</summary>
    public static string LastMessage(string output) =>
        output.Replace("\r\n", "\n").Split('\n').Select(Clean)
            .LastOrDefault(l => l.Trim().Length > 2 && !l.Trim().All(c => "-\\|/█▒ ".Contains(c)))?.Trim()
        ?? "aucun détail";

    /// <summary>Traduit le code de sortie de « winget upgrade » (codes officiels APPINSTALLER_CLI_ERROR_*).</summary>
    public static (InstallStatus Status, bool Reboot, string? Message) InterpretExitCode(int exitCode, string output) =>
        unchecked((uint)exitCode) switch
        {
            0 => (InstallStatus.Installed, false, null),
            0x8A150109 or 0x8A15010B => (InstallStatus.Installed, true, null),
            0x8A15002B => (InstallStatus.UpToDate, false, "Aucune mise à jour applicable : l'application s'est peut-être déjà mise à jour."),
            0x8A15010D => (InstallStatus.UpToDate, false, "Cette version est déjà installée."),
            0x8A150101 or 0x8A150103 or 0x8A150111 => (InstallStatus.Failed, false, "Application en cours d'utilisation : fermez-la puis réessayez."),
            0x8A150102 => (InstallStatus.Failed, false, "Une autre installation est en cours : réessayez dans quelques minutes."),
            0x8A150104 or 0x8A150110 => (InstallStatus.Failed, false, "Dépendance manquante ou impossible à installer."),
            0x8A150105 => (InstallStatus.Failed, false, "Espace disque insuffisant."),
            0x8A150106 => (InstallStatus.Failed, false, "Mémoire insuffisante : fermez d'autres applications."),
            0x8A150107 => (InstallStatus.Failed, false, "Connexion internet nécessaire."),
            0x8A15010A => (InstallStatus.Failed, false, "Un redémarrage est nécessaire avant cette installation."),
            0x8A15010C => (InstallStatus.Failed, false, "Installation annulée."),
            0x8A15010E => (InstallStatus.UpToDate, false, "Une version plus récente est déjà installée."),
            0x8A15010F or 0x8A15003A or 0x8A15001B or 0x8A15001C => (InstallStatus.Failed, false, "Bloqué par une stratégie de l'organisation."),
            0x8A150113 => (InstallStatus.Failed, false, "Ce paquet n'est pas compatible avec ce système."),
            0x8A150114 => (InstallStatus.Failed, false, "Ce programme ne permet pas la mise à jour automatique : passez par l'application elle-même."),
            0x8A15008E => (InstallStatus.Failed, false,
                "La nouvelle version utilise un autre type d'installateur : winget ne peut pas faire la mise à jour. " +
                "Utilisez la mise à jour intégrée à l'application, ou désinstallez-la puis installez la nouvelle version depuis le site de l'éditeur."),
            var code => (InstallStatus.Failed, false, $"winget a échoué (code 0x{code:X8}) : {LastMessage(output)}")
        };
}
