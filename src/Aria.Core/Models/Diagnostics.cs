namespace Aria.Core.Models;

public enum Severity { Ok, Info, Warning, Critical }

/// <summary>Résultat d'un contrôle. Le diagnostic ne modifie jamais rien.</summary>
/// <param name="Advice">Action conseillée (BIOS, branchement…).</param>
/// <param name="Steps">Étapes précises, souvent les options BIOS selon la marque de la carte mère.</param>
/// <param name="NeedsBios">Vrai si la correction se fait dans le BIOS (déclenche la proposition de redémarrage).</param>
public sealed record DiagnosticFinding(
    string Id,
    string Title,
    Severity Severity,
    string Detail,
    string? Advice = null,
    IReadOnlyList<string>? Steps = null,
    bool NeedsBios = false);
