using System.Globalization;
using Aria.Core.Performance;

namespace Aria.Core.Nvidia;

public enum VerdictKind { Unknown, NotWorth, Costly, Worth }

/// <summary>
/// Avis d'Aria à la fin de Performance : le gain par rapport au réglage d'Équilibre vaut-il les watts et les degrés en plus ?
/// Fonction pure (testable) : elle compare deux mesures, sans rien lire ni modifier.
/// </summary>
public sealed record PerformanceVerdict(VerdictKind Kind, string Title, IReadOnlyList<string> Details)
{
    /// <summary>En dessous, le gain ne se ressent pas en jeu.</summary>
    public const double SmallGainPercent = 3;
    /// <summary>À partir de là, le gain est net.</summary>
    public const double WorthGainPercent = 8;
    /// <summary>Au-delà, chaque FPS gagné coûte trop cher en watts.</summary>
    public const double MaxWattsPerFps = 3;

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>Équilibre est le meilleur choix : l'interface met son bouton en avant.</summary>
    public bool RecommendsBalanced => Kind == VerdictKind.NotWorth;

    /// <param name="performance">Mesure du réglage retenu par Performance.</param>
    /// <param name="performanceFps">FPS du banc pour ce réglage (null en mode jeu).</param>
    /// <param name="balanced">Mesure du réglage d'Équilibre (null si equilibre.json ne l'a pas).</param>
    /// <param name="balancedFps">FPS du banc pour le réglage d'Équilibre, à la même définition.</param>
    public static PerformanceVerdict Judge(PerformanceStats performance, double? performanceFps, PerformanceStats? balanced, double? balancedFps)
    {
        if (balanced is null)
            return new(VerdictKind.Unknown, "Comparaison avec Équilibre impossible",
                ["Le résultat d'Équilibre enregistré ne contient pas encore ses mesures (enregistré avant cette version) : " +
                 "refaites Équilibre, ou sa validation de 15 minutes, puis relancez Performance pour obtenir l'avis."]);

        var details = new List<string>();
        var watts = performance.AvgPower is { } pw && balanced.AvgPower is { } bw ? pw - bw : (double?)null;
        var wattsPercent = watts is { } w && balanced.AvgPower is > 0 ? w / balanced.AvgPower.Value * 100 : (double?)null;
        bool notWorth, worth;

        if (performanceFps is { } pf && balancedFps is { } bf && bf > 0)
        {
            var fps = pf - bf;
            var gain = fps / bf * 100;
            details.Add($"{Signed(fps, "0")} FPS ({Signed(gain, "0")} %) par rapport à Équilibre.");
            var perFps = fps > 0 && watts is { } dw ? dw / fps : (double?)null;
            if (perFps is { } cost)
                details.Add($"Coût : {cost.ToString("0.#", Fr)} W par FPS gagné (au-delà de {MaxWattsPerFps.ToString(Fr)} W, c'est cher payé).");
            if (performance.AvgPower is > 0 && balanced.AvgPower is > 0)
                details.Add($"Efficacité : {(pf / performance.AvgPower.Value).ToString("0.00", Fr)} FPS/W, contre " +
                            $"{(bf / balanced.AvgPower.Value).ToString("0.00", Fr)} FPS/W avec Équilibre.");
            notWorth = gain < SmallGainPercent || perFps > MaxWattsPerFps;
            worth = gain >= WorthGainPercent && perFps is not > MaxWattsPerFps;
        }
        else
        {
            // Mode jeu : pas de FPS mesurés, la fréquence sert d'indice (les FPS progressent en général moins vite qu'elle)
            var gain = balanced.AvgClock > 0 ? (performance.AvgClock - balanced.AvgClock) / balanced.AvgClock * 100 : 0;
            details.Add($"Fréquence {Signed(gain, "0")} % par rapport à Équilibre (estimation sans FPS mesurés : les FPS montent en général moins que la fréquence).");
            notWorth = gain < SmallGainPercent || wattsPercent > gain;
            worth = gain >= WorthGainPercent && wattsPercent is not > 0;
        }

        var heat = $"{Signed((double)performance.MaxTemp - balanced.MaxTemp, "0")} °C";
        details.Insert(1, watts is { } extra && wattsPercent is { } percent
            ? $"Consommation {Signed(extra, "0")} W ({Signed(percent, "0")} %), température max {heat}."
            : $"Température max {heat}.");

        return notWorth
            ? new(VerdictKind.NotWorth, "Ne vaut pas le coup : Équilibre recommandé", details)
            : worth
                ? new(VerdictKind.Worth, "Vaut le coup : gain net pour un coût raisonnable", details)
                : new(VerdictKind.Costly, "Gain réel mais cher : à réserver aux jeux qui en ont besoin", details);
    }

    private static string Signed(double value, string format) =>
        value.ToString($"+{format};-{format};{format}", Fr);
}
