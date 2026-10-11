using Aria.Core.Diagnostics;
using Aria.Core.Models;

namespace Aria.Core.Reporting;

/// <summary>Affichage console du diagnostic : problèmes d'abord, points conformes ensuite.</summary>
public static class DiagnosticPrinter
{
    public static void Print(IReadOnlyList<DiagnosticFinding> findings, BiosGuide bios, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine("== DIAGNOSTIC MATÉRIEL ==");

        var toFix = findings.Where(f => f.Severity >= Severity.Warning).OrderByDescending(f => f.Severity).ToList();
        var info = findings.Where(f => f.Severity == Severity.Info).ToList();
        var ok = findings.Where(f => f.Severity == Severity.Ok).ToList();

        if (toFix.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("  À corriger");
            foreach (var f in toFix) Detailed(output, f);
        }

        if (info.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("  Pour information");
            foreach (var f in info) Detailed(output, f);
        }

        if (ok.Count > 0)
        {
            output.WriteLine();
            output.WriteLine("  Conforme");
            foreach (var f in ok) output.WriteLine($"  [OK] {f.Title}");
        }

        if (findings.Any(f => f.NeedsBios && f.Severity >= Severity.Info))
        {
            output.WriteLine();
            output.WriteLine($"== ACCÈS AU BIOS ({bios.Brand.Brand}) ==");
            output.WriteLine($"  Touche au démarrage : {bios.Brand.SetupKey}");
            if (bios.Brand.AdvancedModeKey is { } key)
                output.WriteLine($"  Mode avancé         : {key}");
            output.WriteLine("  Les chemins peuvent varier selon la version du BIOS : chercher aussi les autres noms indiqués.");
            output.WriteLine("  Modifier un réglage à la fois. En cas de problème : « Load Optimized Defaults » remet tout par défaut.");
        }

        output.WriteLine();
        output.WriteLine($"  Bilan : {toFix.Count} à corriger · {info.Count} pour information · {ok.Count} conformes");
    }

    private static void Detailed(TextWriter output, DiagnosticFinding f)
    {
        var tag = f.Severity switch
        {
            Severity.Critical => "[X]",
            Severity.Warning => "[!]",
            _ => "[i]"
        };

        output.WriteLine();
        output.WriteLine($"  {tag} {f.Title}");
        if (!string.IsNullOrWhiteSpace(f.Detail)) Wrap(output, f.Detail, "      ");
        if (f.Advice is not null) Wrap(output, $"→ {f.Advice}", "      ");
        if (f.Steps is { Count: > 0 })
            foreach (var step in f.Steps) output.WriteLine($"        {step}");
    }

    /// <summary>Coupe les lignes longues pour rester lisible dans une console de 100 colonnes.</summary>
    private static void Wrap(TextWriter output, string text, string indent, int width = 100)
    {
        var line = indent;
        foreach (var word in text.Split(' '))
        {
            if (line.Length + word.Length + 1 > width && line.Trim().Length > 0)
            {
                output.WriteLine(line.TrimEnd());
                line = indent + "  ";
            }
            line += word + " ";
        }
        if (line.Trim().Length > 0) output.WriteLine(line.TrimEnd());
    }
}
