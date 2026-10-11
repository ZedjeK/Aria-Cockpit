using System.Diagnostics;

namespace Aria.Gui.Services;

public static class Shell
{
    /// <summary>
    /// Ouvre une adresse web ou un programme SANS les droits administrateur : Aria tourne en administrateur,
    /// et passer par l'Explorateur évite de lancer le navigateur avec ces droits.
    /// </summary>
    public static void OpenUnelevated(string target) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false });

    /// <summary>Ouvre un fichier local (rapport…) avec l'application associée.</summary>
    public static void OpenFile(string path) =>
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
}
