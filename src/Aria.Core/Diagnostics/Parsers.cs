using System.Text.RegularExpressions;

namespace Aria.Core.Diagnostics;

/// <summary>Déductions sur la RAM à partir des informations brutes de Windows.</summary>
internal static partial class MemoryParser
{
    /// <summary>Vitesses commerciales connues (MT/s), pour valider ce qu'on lit dans une référence.</summary>
    private static readonly HashSet<int> KnownSpeeds =
    [
        2133, 2400, 2666, 2800, 2933, 3000, 3200, 3333, 3466, 3600, 3733, 3800, 3866, 4000, 4133, 4266, 4400, 4600,
        4800, 5200, 5600, 6000, 6200, 6400, 6600, 6800, 7000, 7200, 7600, 7800, 8000, 8200, 8400, 8800
    ];

    /// <summary>
    /// Vitesse pour laquelle le kit est vendu, déduite de sa référence. Exemples :
    /// CMK32GX5M2B6000C36 → 6000 · F5-6000J3038F16G → 6000 · KF560C36BBE-16 → 6000 · CP16G60C36U5B → 6000.
    /// Renvoie null si rien de fiable n'est trouvé.
    /// </summary>
    public static int? KitSpeedFromPartNumber(string partNumber)
    {
        var pn = partNumber.Trim().ToUpperInvariant();
        if (pn.Length == 0) return null;

        // Nombre à 4 chiffres isolé qui correspond à une vitesse connue
        foreach (Match m in FourDigits().Matches(pn))
            if (KnownSpeeds.Contains(int.Parse(m.Value))) return int.Parse(m.Value);

        // Kingston (KF560…, KF432…, KVR32…, HX436…) et Crucial (…G60C36…) : 2 chiffres × 100
        var m2 = TwoDigitCodes().Match(pn);
        if (m2.Success)
        {
            var speed = int.Parse(m2.Groups["s"].Value) * 100;
            if (KnownSpeeds.Contains(speed)) return speed;
            if (KnownSpeeds.Contains(speed + 66)) return speed + 66; // ex. 26 → 2666
            if (KnownSpeeds.Contains(speed + 33)) return speed + 33; // ex. 29 → 2933
        }
        return null;
    }

    /// <summary>
    /// Nombre de barrettes du kit d'origine, déduit de la référence, ou null si inconnu.
    /// Corsair CMT32GX5M2B5200C40 → 2 · G.Skill F5-6000J3038F16GX2-TZ5RK → 2 · Kingston KF560C36BBEK2-32 → 2.
    /// </summary>
    public static int? KitSizeFromPartNumber(string partNumber)
    {
        var m = KitSize().Match(partNumber.Trim().ToUpperInvariant());
        return m.Success ? int.Parse(m.Groups["n"].Value) : null;
    }

    /// <summary>
    /// Certains BIOS rapportent la fréquence réelle (MHz) au lieu du débit (MT/s) : on corrige selon la génération
    /// (DDR4 sous 2133 et DDR5 sous 3200 ne sont pas des débits réels : DDR4-3200 lue « 1600 » → 3200), puis on arrondit
    /// à la vitesse commerciale la plus proche (1066 × 2 = 2132 → 2133).
    /// </summary>
    public static int NormalizeSpeed(int value, string generation = "")
    {
        if (value <= 0) return value;
        var floor = generation switch { "DDR4" => 2133, "DDR5" => 3200, _ => 1400 };
        var speed = value < floor ? value * 2 : value;
        var nearest = KnownSpeeds.MinBy(k => Math.Abs(k - speed));
        return Math.Abs(nearest - speed) <= 40 ? nearest : speed;
    }

    /// <summary>
    /// Barrettes sans profil XMP/EXPO (vitesse JEDEC seulement) : Kingston ValueRAM (KVR), Crucial standard (CT),
    /// Samsung (M3xx), SK hynix (HMA, HMC…), Micron (MTA, MTC). Un écart avec la vitesse du kit vient alors du processeur
    /// ou de la carte mère, pas d'un profil oublié.
    /// </summary>
    public static bool IsJedecOnly(string partNumber) => JedecOnly().IsMatch(partNumber.Trim().ToUpperInvariant());

    [GeneratedRegex(@"^(KVR|CT\d|M3[2-9]\d|HM[AC][A-Z0-9]|MT[AC]\d)")]
    private static partial Regex JedecOnly();

    /// <summary>
    /// Identifiant du canal mémoire d'une barrette, ou null si l'emplacement n'est pas interprétable.
    /// DIMM_A2 → "A" · ChannelB-DIMM0 → "B" · Controller1-ChannelA-DIMM0 → "1A" (le contrôleur compte).
    /// </summary>
    public static string? ChannelKey(string deviceLocator, string bankLabel)
    {
        foreach (var text in new[] { deviceLocator, bankLabel })
        {
            var t = text.ToUpperInvariant();
            var controller = Controller().Match(t);
            var prefix = controller.Success ? controller.Groups["c"].Value : "";

            var channel = ChannelWord().Match(t);
            if (channel.Success) return prefix + channel.Groups["ch"].Value;

            var dimm = DimmLetter().Match(t);
            if (dimm.Success) return prefix + dimm.Groups["ch"].Value;
        }
        return null;
    }

    [GeneratedRegex(@"(?<!\d)\d{4}(?!\d)")]
    private static partial Regex FourDigits();

    [GeneratedRegex(@"(?:KF[45]|HX[45]|KVR|\dG)(?<s>\d{2})(?:C|[A-Z]\d)")]
    private static partial Regex TwoDigitCodes();

    [GeneratedRegex(@"X[45]M(?<n>[1-8])[A-Z]|\dGX(?<n>[1-8])\b|K(?<n>[1-8])-\d+$")]
    private static partial Regex KitSize();

    [GeneratedRegex(@"CONTROLLER\s*(?<c>\d)")]
    private static partial Regex Controller();

    [GeneratedRegex(@"CHANNEL[\s_-]*(?<ch>[A-H])(?![A-Z])")]
    private static partial Regex ChannelWord();

    [GeneratedRegex(@"DIMM[\s_-]*(?<ch>[A-H])\d|(?<![A-Z])(?<ch>[A-H])\d[\s_-]*DIMM")]
    private static partial Regex DimmLetter();
}

/// <summary>Emplacements des bibliothèques de jeux (Steam, Epic, Xbox).</summary>
internal static partial class GameLibraryParser
{
    /// <summary>Extrait les chemins "path" d'un fichier libraryfolders.vdf de Steam.</summary>
    public static IEnumerable<string> SteamPaths(string vdf) =>
        SteamPath().Matches(vdf).Select(m => m.Groups["p"].Value.Replace(@"\\", @"\"));

    [GeneratedRegex("\"path\"\\s+\"(?<p>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamPath();
}
