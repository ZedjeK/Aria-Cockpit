using Microsoft.Win32;
using Aria.Core.Models;
using Aria.Core.Startup;

namespace Aria.Core.Diagnostics.Checks;

/// <summary>Jeux installés sur disque dur classique et espace libre du disque système.</summary>
public sealed class StorageCheck : IDiagnosticCheck
{
    public string Name => "stockage";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var disks = ctx.Hardware.Disks;

        // Bibliothèques de jeux situées sur un disque dur
        foreach (var (launcher, path) in GameLibraries.Find())
        {
            var letter = char.ToUpperInvariant(path.FirstOrDefault());
            var disk = disks.FirstOrDefault(d => d.DriveLetters.Contains(letter));
            if (disk?.Media == StorageMedia.Hdd)
                yield return new("storage.games", $"Jeux {launcher} sur disque dur ({letter}:)", Severity.Warning,
                    $"Bibliothèque : {path}. Les chargements et l'apparition des textures sont nettement plus lents que sur SSD.",
                    "Déplacer les jeux les plus joués vers un SSD (les lanceurs proposent une fonction « Déplacer »).");
        }

        // Espace libre du disque système (cache des shaders, mises à jour…)
        var root = Path.GetPathRoot(Environment.SystemDirectory);
        if (root is null) yield break;
        var drive = new DriveInfo(root);
        // Go au sens de l'Explorateur (1024³ octets), pour afficher le même chiffre que lui
        var freeGb = drive.AvailableFreeSpace / 1073741824.0;
        var freeRatio = (double)drive.AvailableFreeSpace / drive.TotalSize;

        if (NearlyFull(freeGb, freeRatio))
            yield return new("storage.free", $"Disque système presque plein ({freeGb:0} Go libres)", Severity.Warning,
                "Windows et le pilote graphique ont besoin d'espace (cache des shaders, mises à jour).",
                "Libérer de l'espace : Paramètres → Système → Stockage → Recommandations de nettoyage.");
        else
            yield return new("storage.free", $"Disque système : {freeGb:0} Go libres", Severity.Ok, "");
    }

    /// <summary>
    /// Moins de 20 Go, ou moins de 10 % s'il reste aussi moins de 50 Go : sur un disque de 2 To, 190 Go libres
    /// (moins de 10 %) laissent largement la place au cache des shaders et aux mises à jour.
    /// </summary>
    internal static bool NearlyFull(double freeGb, double freeRatio) => freeGb < 20 || (freeRatio < 0.10 && freeGb < 50);
}

/// <summary>Repère les dossiers de jeux de Steam, Epic Games et de l'application Xbox.</summary>
internal static class GameLibraries
{
    public static IEnumerable<(string Launcher, string Path)> Find()
    {
        var found = new List<(string, string)>();

        // Steam : libraryfolders.vdf liste toutes les bibliothèques
        if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is string steam)
        {
            var vdf = Path.Combine(steam.Replace('/', '\\'), "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdf))
                found.AddRange(GameLibraryParser.SteamPaths(File.ReadAllText(vdf)).Select(p => ("Steam", p)));
        }

        // Epic : un manifeste .item (JSON) par jeu installé
        var epicManifests = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        if (Directory.Exists(epicManifests))
        {
            foreach (var file in Directory.GetFiles(epicManifests, "*.item"))
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("InstallLocation", out var loc) && loc.GetString() is { Length: > 0 } p)
                        found.Add(("Epic", p));
                }
                catch (Exception e) when (e is System.Text.Json.JsonException or IOException) { }
            }
        }

        // Xbox / Game Pass : dossier "XboxGames" à la racine d'un lecteur
        foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            var xbox = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (Directory.Exists(xbox)) found.Add(("Xbox", xbox));
        }

        // Un seul signalement par lanceur et par lecteur
        return found.DistinctBy(f => (f.Item1, char.ToUpperInvariant(f.Item2.FirstOrDefault())));
    }
}

/// <summary>Programmes lancés au démarrage de Windows (information).</summary>
public sealed class StartupCheck : IDiagnosticCheck
{
    public string Name => "programmes au démarrage";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var list = StartupApps.Scan().Where(e => e.Enabled).Select(e => e.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        if (list.Count == 0) yield break;

        yield return new("startup", $"{list.Count} programmes au démarrage", list.Count > 15 ? Severity.Info : Severity.Ok,
            string.Join(", ", list),
            list.Count > 15
                ? "Désactiver ceux dont vous n'avez pas besoin : Réglages → Démarrage (réversible). Garder les pilotes (audio, NVIDIA…)."
                : null);
    }
}
