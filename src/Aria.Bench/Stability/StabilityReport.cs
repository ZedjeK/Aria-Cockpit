namespace Aria.Bench.Stability;

/// <summary>
/// Résultat d'un contrôle de stabilité (calcul vérifié, ou mémoire vidéo et calcul), écrit dans le journal du banc
/// (« stabilite ») et lu par Aria Cockpit. Statut : « reussi », « attention » (bande passante qui ne monte plus ou qui
/// baisse en chauffant) ou « echec » (donnée relue fausse, calcul faux, ou pilote qui lâche).
/// </summary>
public sealed class StabilityReport
{
    public const string Passed = "reussi", Warning = "attention", Failed = "echec";

    public string Mode { get; set; } = "";
    public bool AutoControle { get; set; }
    public string Statut { get; set; } = Failed;
    public List<string> Raisons { get; set; } = [];
    public List<string> Avertissements { get; set; } = [];
    public double DureeS { get; set; }
    public double PhaseCalculSeulS { get; set; }

    public double MemoireTesteeGio { get; set; }
    public int Passes { get; set; }
    public long MotsFaux { get; set; }
    public long BitsFaux { get; set; }
    public double? BandePassanteLectureMedianeGoS { get; set; }
    public double? BandePassanteEcritureMedianeGoS { get; set; }
    public double? BandePassanteDebutGoS { get; set; }
    public double? BandePassanteFinGoS { get; set; }
    public double? BandePassantePrecedenteGoS { get; set; }
    /// <summary>Plus de fréquence mémoire, pas plus de bande passante : la GDDR6X corrige au lieu d'accélérer.</summary>
    public bool PasPlusRapide { get; set; }

    public long ExecutionsCalcul { get; set; }
    public long EcartsCalcul { get; set; }
    public uint IterationsCalcul { get; set; }
    public int FilsFautifsDistincts { get; set; }
    public List<WrongWord> PremieresErreurs { get; set; } = [];
    public List<WrongWord> PremiersEcartsCalcul { get; set; } = [];

    /// <summary>Gain minimal de bande passante pour qu'un palier mémoire compte comme « plus rapide » que le précédent.</summary>
    public const double MinBandwidthGain = 0.005;

    /// <summary>
    /// Le verdict : échec au moindre signe d'instabilité, attention si la bande passante n'augmente pas ou recule en
    /// chauffant. Fonction pure (testable) : elle ne lit que ce qui a été mesuré.
    /// </summary>
    public static StabilityReport Conclude(string mode, bool selfCheck, bool memoryTested, bool burnTested,
        IReadOnlyList<MemoryPass> passes, long wrongWords, long flippedBits, long dispatches, long mismatches,
        IReadOnlyList<string> failures, double? previousReadGoS, double requestedSeconds, double realSeconds)
    {
        var r = new StabilityReport { Mode = mode, AutoControle = selfCheck, DureeS = Math.Round(realSeconds, 1) };
        var reasons = new List<string>();
        var warnings = new List<string>();
        foreach (var f in failures) reasons.Add($"Le test s'est arrêté : {f}. Le pilote a lâché sous la charge : réglage instable.");

        if (memoryTested)
        {
            r.Passes = passes.Count;
            r.MotsFaux = wrongWords;
            r.BitsFaux = flippedBits;
            if (wrongWords > 0)
                reasons.Add($"Mémoire : {wrongWords} mot(s) relu(s) faux ({flippedBits} bit(s) inversé(s)) sur {passes.Count} passes. " +
                            "La mémoire vidéo n'est pas stable avec ce réglage.");

            var linear = passes.Where(p => !p.Melange).ToList();
            if (linear.Count > 0)
            {
                r.BandePassanteLectureMedianeGoS = Median(linear.Select(p => p.LectureGoS));
                r.BandePassanteEcritureMedianeGoS = Median(linear.Select(p => p.EcritureGoS));
            }
            // En chauffant : la fin du test comparée au début (la GDDR6X renvoie ses erreurs, ce qui coûte de la bande passante)
            if (linear.Count >= 8)
            {
                var quarter = linear.Count / 4;
                var start = Median(linear.Take(quarter).Select(p => p.LectureGoS));
                var end = Median(linear.Skip(linear.Count - quarter).Select(p => p.LectureGoS));
                r.BandePassanteDebutGoS = start;
                r.BandePassanteFinGoS = end;
                if (end < start * 0.97)
                    warnings.Add($"Bande passante en baisse en chauffant : {start:0} Go/s au début, {end:0} Go/s à la fin " +
                                 $"(−{(1 - end / start) * 100:0} %). Signe possible d'erreurs corrigées par renvoi (GDDR6X).");
            }

            // Plus de fréquence mémoire doit donner plus de bande passante que le palier précédent
            if (previousReadGoS is { } before && r.BandePassanteLectureMedianeGoS is { } now)
            {
                r.BandePassantePrecedenteGoS = before;
                if (now <= before * (1 + MinBandwidthGain))
                {
                    r.PasPlusRapide = true;
                    warnings.Add($"Ce réglage n'est pas plus rapide que le précédent ({now:0} Go/s contre {before:0} Go/s) : " +
                                 "au-delà d'un certain point, la GDDR6X corrige des erreurs au lieu d'aller plus vite. Garder le palier précédent.");
                }
            }
        }

        if (burnTested)
        {
            r.ExecutionsCalcul = dispatches;
            r.EcartsCalcul = mismatches;
            if (mismatches > 0)
                reasons.Add($"Calcul : {mismatches} résultat(s) différent(s) sur {dispatches} exécutions d'un calcul identique. " +
                            "Le cœur graphique fait des erreurs avec ce réglage (décalage de fréquence ou undervolt).");
        }

        if (realSeconds < Math.Min(requestedSeconds, 600) * 0.9 && failures.Count == 0)
            warnings.Add($"Test arrêté avant la fin ({realSeconds:0} s sur {requestedSeconds:0} s) : verdict moins sûr.");

        if (selfCheck)
        {
            // Les erreurs ont été mises exprès : le test réussit s'il les retrouve toutes, et rien d'autre
            var missed = new List<string>();
            if (memoryTested && wrongWords != MemoryTest.InjectedWords)
                missed.Add($"Mémoire : {wrongWords} mot(s) faux trouvés au lieu des {MemoryTest.InjectedWords} faussés exprès.");
            if (burnTested && mismatches < BurnTest.Threads * 0.99)
                missed.Add($"Calcul : {mismatches} écart(s) trouvés pour {BurnTest.Threads} résultats faussés exprès.");
            missed.AddRange(reasons.Where(x => !x.StartsWith("Mémoire", StringComparison.Ordinal) && !x.StartsWith("Calcul", StringComparison.Ordinal)));
            r.Statut = missed.Count == 0 ? Passed : Failed;
            r.Raisons = missed;
            return r;
        }

        r.Statut = reasons.Count > 0 ? Failed : warnings.Count > 0 ? Warning : Passed;
        r.Raisons = reasons;
        r.Avertissements = warnings;
        return r;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Where(v => !double.IsNaN(v)).OrderBy(v => v).ToArray();
        return sorted.Length == 0 ? double.NaN : Math.Round(sorted[sorted.Length / 2], 1);
    }
}
