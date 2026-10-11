using System.Globalization;
using System.Text.RegularExpressions;

namespace Aria.Core.Updates;

/// <summary>Avancement d'une mise à jour winget, prêt à afficher.</summary>
public sealed record WingetProgress(string Text, double? Fraction);

/// <summary>
/// Lit au fil de l'eau ce que winget écrit pendant une installation : barre de téléchargement
/// (« ███▒▒▒  45.2 MB / 98.1 MB »), pourcentage (« ███▒▒▒  60% »), puis lancement de l'installateur.
/// Une instance par installation (elle retient la phase en cours).
/// </summary>
public sealed partial class WingetProgressParser
{
    private bool _downloadSeen;
    private bool _downloadDone;
    private bool _installAnnounced;

    public WingetProgress? Feed(string segment)
    {
        var s = WingetParser.StripVt(segment).Trim();
        if (s.Length <= 1) return null; // animation d'attente (- \ | /)

        var bytes = Bytes().Match(s);
        if (bytes.Success)
        {
            var done = ToBytes(bytes.Groups["a"].Value, bytes.Groups["ua"].Value);
            var total = ToBytes(bytes.Groups["b"].Value, bytes.Groups["ub"].Value);
            if (total <= 0) return null;

            var fraction = Math.Clamp(done / total, 0, 1);
            _downloadSeen = true;
            if (fraction >= 0.999) _downloadDone = true;
            return new WingetProgress($"Téléchargement · {fraction * 100:0} % · {Size(done)} / {Size(total)}", fraction);
        }

        var percent = Percent().Match(s);
        if (percent.Success && (s.Contains('█') || s.Contains('▒')))
        {
            var fraction = Math.Clamp(int.Parse(percent.Groups["p"].Value) / 100.0, 0, 1);
            if (!_downloadDone && fraction >= 0.999) _downloadDone = true;
            var phase = _downloadDone && _downloadSeen ? "Installation" : "Progression";
            return new WingetProgress($"{phase} · {fraction * 100:0} %", fraction);
        }

        // Une ligne de texte après le téléchargement : l'installateur démarre
        if (_downloadDone && !_installAnnounced && !s.Contains('█') && !s.Contains('▒'))
        {
            _installAnnounced = true;
            return new WingetProgress("Installation en cours…", null);
        }
        return null;
    }

    private static double ToBytes(string number, string unit)
    {
        var value = double.Parse(number.Replace(',', '.'), CultureInfo.InvariantCulture);
        return char.ToUpperInvariant(unit.FirstOrDefault()) switch
        {
            'K' => value * 1024,
            'M' => value * 1024 * 1024,
            'G' => value * 1024 * 1024 * 1024,
            _ => value
        };
    }

    private static string Size(double bytes) =>
        bytes >= 1024 * 1024 * 1024 ? $"{bytes / 1024 / 1024 / 1024:0.0} Go"
        : bytes >= 1024 * 1024 ? $"{bytes / 1024 / 1024:0} Mo"
        : $"{bytes / 1024:0} Ko";

    [GeneratedRegex(@"(?<a>\d+(?:[.,]\d+)?)\s*(?<ua>[KMG]?i?B)\s*/\s*(?<b>\d+(?:[.,]\d+)?)\s*(?<ub>[KMG]?i?B)", RegexOptions.IgnoreCase)]
    private static partial Regex Bytes();

    [GeneratedRegex(@"(?<p>\d{1,3})\s*%")]
    private static partial Regex Percent();
}
