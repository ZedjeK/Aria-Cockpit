using Aria.Core.Models;

namespace Aria.Cli;

internal sealed record CliOptions(RunMode Mode, string? BackupPath);

internal static class CommandLine
{
    public const string Help = """
        Aria (version console) — optimisation de Windows 11 et des réglages NVIDIA selon votre matériel

        Utilisation :
          Aria.Cli                    Diagnostic, état actuel, liste à cocher, puis application des choix validés
          Aria.Cli --simulation       Affiche tout ce qui serait proposé, sans rien modifier
          Aria.Cli --restaurer [fichier]  Revient aux valeurs d'une sauvegarde (liste à cocher)
          Aria.Cli --aide                 Affiche cette aide

        Sauvegardes et rapports : %USERPROFILE%\.Aria-Cockpit
        """;

    /// <returns>null si l'aide doit être affichée.</returns>
    public static CliOptions? Parse(string[] args)
    {
        if (args.Length == 0) return new CliOptions(RunMode.Interactive, null);

        return args[0].ToLowerInvariant() switch
        {
            "--simulation" or "-s" when args.Length == 1 => new CliOptions(RunMode.Simulate, null),
            "--restaurer" or "-r" when args.Length <= 2 => new CliOptions(RunMode.Restore, args.ElementAtOrDefault(1)),
            _ => null
        };
    }
}
