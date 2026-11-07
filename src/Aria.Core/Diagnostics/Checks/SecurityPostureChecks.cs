using Aria.Core.Models;

namespace Aria.Core.Diagnostics.Checks;

/// <summary>Pare-feu Windows actif sur les trois profils réseau (domaine, privé, public). Repris de CTI-Cockpit.</summary>
public sealed class FirewallCheck : IDiagnosticCheck
{
    public string Name => "pare-feu Windows";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var security = ctx.Hardware.Security;
        if (Evaluate(security.Firewall, security.ThirdPartyFirewall) is { } finding) yield return finding;
    }

    internal static DiagnosticFinding? Evaluate(IReadOnlyDictionary<string, bool> profiles, string? thirdParty)
    {
        if (profiles.Count == 0) return null;
        var off = profiles.Where(p => !p.Value).Select(p => p.Key.ToLowerInvariant()).ToList();
        if (off.Count == 0)
            return new("security.firewall", "Pare-feu Windows actif", Severity.Ok, "Actif sur les profils domaine, privé et public.");

        var list = string.Join(", ", off);
        // Un autre pare-feu déclaré à Windows prend le relais : Windows coupe alors le sien, c'est normal
        if (thirdParty is not null)
            return new("security.firewall", $"Pare-feu géré par {thirdParty}", Severity.Info,
                $"Le pare-feu Windows est coupé (profil {list}) : {thirdParty} protège le PC à sa place.");

        return new("security.firewall", $"Pare-feu Windows désactivé (profil {list})", Severity.Warning,
            "Sans pare-feu, le PC accepte les connexions entrantes du réseau auquel il est relié, y compris sur un réseau public " +
            "(hôtel, gare, LAN party).",
            "Sécurité Windows → Pare-feu et protection du réseau : activer le pare-feu pour chaque profil. Les jeux qui en ont " +
            "besoin demandent eux-mêmes l'autorisation au premier lancement.");
    }
}

/// <summary>Date du dernier correctif installé par Windows Update (hors définitions antivirus et pilotes). Repris de CTI-Cockpit.</summary>
public sealed class WindowsPatchCheck : IDiagnosticCheck
{
    /// <summary>Un mois de correctifs manqué (publiés chaque deuxième mardi), avec une marge de deux semaines.</summary>
    internal const int MaxDays = 45;

    public string Name => "correctifs Windows";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        yield return Evaluate(ctx.Hardware.Security.LastUpdateInstalled, DateTime.Today);
    }

    internal static DiagnosticFinding Evaluate(DateTime? lastInstalled, DateTime today)
    {
        if (lastInstalled is not { } last)
            return new("security.patches", "Date du dernier correctif Windows inconnue", Severity.Info,
                "L'historique de Windows Update n'indique aucune installation réussie.",
                "Lancer une recherche depuis la page Mises à jour.");

        var days = (int)(today.Date - last.Date).TotalDays;
        var when = $"Dernière installation le {last:dd/MM/yyyy}";
        return days > MaxDays
            ? new("security.patches", $"Correctifs Windows en retard ({days} jours)", Severity.Warning,
                $"{when}. Microsoft publie les correctifs de sécurité chaque mois : au moins une série a été manquée.",
                "Installer les mises à jour depuis la page Mises à jour, puis redémarrer.")
            : new("security.patches", "Correctifs Windows à jour", Severity.Ok, $"{when} (il y a {days} jour{(days > 1 ? "s" : "")}).");
    }
}
