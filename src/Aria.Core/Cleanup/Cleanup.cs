using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Aria.Core.Cleanup;

public enum CleanupKind { TempFiles, DeliveryOptimization, ComponentStore, ShaderCaches, CrashDumps, PreviousWindows, OldShaderCaches, Thumbnails }

/// <summary>Un élément de nettoyage, avec ce que l'analyse a trouvé.</summary>
/// <param name="Bytes">Taille récupérable estimée, ou null si non estimable à l'avance.</param>
/// <param name="Warning">Conséquence à connaître, affichée dans la liste et la confirmation.</param>
/// <param name="IsLong">Peut prendre plusieurs minutes.</param>
public sealed record CleanupItem(
    CleanupKind Kind,
    string Title,
    string Description,
    bool SelectedByDefault,
    long? Bytes,
    int Files,
    string? Warning = null,
    bool IsLong = false)
{
    /// <summary>Rien à nettoyer (taille connue et nulle).</summary>
    public bool IsEmpty => Bytes == 0;
}

public enum CleanupStatus { Done, Partial, Failed, Declined }

/// <param name="Freed">Espace libéré (mesuré), en octets.</param>
/// <param name="Skipped">Fichiers ignorés car en cours d'utilisation ou protégés.</param>
public sealed record CleanupResult(CleanupItem Item, CleanupStatus Status, long Freed, int Files, int Skipped, string? Error = null);

/// <summary>Étape en cours, pour la progression.</summary>
public sealed record CleanupStep(int Done, int Total, string Text);

/// <summary>Emplacements autorisés. Rien d'autre n'est jamais supprimé.</summary>
public sealed class CleanupPaths
{
    public required IReadOnlyList<string> Temp { get; init; }
    public required IReadOnlyList<string> ShaderCaches { get; init; }
    public required IReadOnlyList<string> CrashDumpFolders { get; init; }
    public required IReadOnlyList<string> CrashDumpFiles { get; init; }
    public required string DeliveryOptimizationCache { get; init; }
    public required string PreviousWindows { get; init; }
    public required string SystemDrive { get; init; }

    /// <summary>Dossier où l'Explorateur garde ses miniatures (fichiers « thumbcache_*.db »). Mesuré, jamais vidé à la main.</summary>
    public required string ThumbnailCache { get; init; }

    public static CleanupPaths ForThisPc()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var drive = Path.GetPathRoot(windows) ?? @"C:\";
        var localLow = Path.Combine(Path.GetDirectoryName(local) ?? local, "LocalLow");
        return new CleanupPaths
        {
            // TEMP vient d'une variable d'environnement : accepté seulement s'il s'agit bien d'un dossier « Temp »
            Temp = new[] { Path.GetTempPath(), Path.Combine(windows, "Temp") }
                .Select(p => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)))
                .Where(p => Path.GetFileName(p).Equals("Temp", StringComparison.OrdinalIgnoreCase)
                            || Path.GetFileName(p).Equals("Tmp", StringComparison.OrdinalIgnoreCase))
                .Where(SafeDelete.IsSafeRoot)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ShaderCaches =
            [
                Path.Combine(local, "NVIDIA", "DXCache"),
                Path.Combine(local, "NVIDIA", "GLCache"),
                Path.Combine(local, "NVIDIA Corporation", "NV_Cache"),
                Path.Combine(local, "D3DSCache"),
                Path.Combine(local, "AMD", "DxCache"),
                Path.Combine(local, "AMD", "DxcCache"),
                Path.Combine(local, "AMD", "VkCache"),
                Path.Combine(localLow, "Intel", "ShaderCache")
            ],
            // LiveKernelReports : ce que Windows écrit quand un pilote se bloque sans écran bleu (le pilote graphique, surtout)
            CrashDumpFolders = [Path.Combine(windows, "Minidump"), Path.Combine(local, "CrashDumps"), Path.Combine(windows, "LiveKernelReports")],
            CrashDumpFiles = [Path.Combine(windows, "MEMORY.DMP")],
            DeliveryOptimizationCache = Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local",
                "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
            PreviousWindows = Path.Combine(drive, "Windows.old"),
            SystemDrive = drive,
            ThumbnailCache = Path.Combine(local, "Microsoft", "Windows", "Explorer")
        };
    }
}

/// <summary>Analyse (lecture seule) puis nettoyage des seuls éléments cochés.</summary>
public static class CleanupService
{
    /// <summary>Âge minimal des fichiers temporaires supprimés.</summary>
    public static readonly TimeSpan TempMinAge = TimeSpan.FromDays(7);

    /// <summary>Âge à partir duquel un fichier de cache de shaders compte comme ancien (ni créé ni modifié depuis).</summary>
    public static readonly TimeSpan OldShaderAge = TimeSpan.FromDays(30);

    private const string VolumeCaches = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches";
    private const string PreviousInstallationsHandler = "Previous Installations";
    private const string ThumbnailHandler = "Thumbnail Cache";
    private const string StateFlags = "StateFlags6060"; // numéro de profil de Nettoyage de disque réservé à Aria
    private const int SageRun = 6060;

    // ---------------------------------------------------------------- Analyse

    public static IReadOnlyList<CleanupItem> Scan(CleanupPaths paths)
    {
        var tempLimit = DateTime.Now - TempMinAge;
        var temp = Sum(paths.Temp.Select(p => SafeDelete.Measure(p, tempLimit)));
        var shaders = Sum(paths.ShaderCaches.Select(p => SafeDelete.Measure(p)));
        var oldShaders = Sum(paths.ShaderCaches.Select(p => SafeDelete.Measure(p, DateTime.Now - OldShaderAge)));
        var dumps = Sum(paths.CrashDumpFolders.Select(p => SafeDelete.Measure(p))).Add(MeasureFiles(paths.CrashDumpFiles));
        var delivery = SafeDelete.Measure(paths.DeliveryOptimizationCache);
        var thumbnails = MeasureThumbnails(paths.ThumbnailCache);
        var items = new List<CleanupItem>
        {
            new(CleanupKind.TempFiles, "Fichiers temporaires",
                "Fichiers temporaires de Windows et des applications, non modifiés depuis plus de 7 jours. Ceux en cours d'utilisation sont ignorés.",
                true, temp.Bytes, temp.Files),
            new(CleanupKind.DeliveryOptimization, "Cache d'Optimisation de la distribution",
                "Copies de mises à jour Windows gardées pour les partager. Vidé par la commande officielle de Windows.",
                true, delivery.Bytes, delivery.Files),
            new(CleanupKind.ComponentStore, "Anciens composants des mises à jour Windows",
                "Nettoyage officiel de Windows (DISM /StartComponentCleanup, sans /ResetBase) : les mises à jour installées restent désinstallables.",
                true, null, 0, IsLong: true),
            new(CleanupKind.OldShaderCaches, "Caches de shaders anciens (plus de 30 jours)",
                "Fichiers de cache ni créés ni modifiés depuis 30 jours : le plus souvent ceux de jeux que vous ne lancez plus, ou d'anciens pilotes. " +
                "Les caches récents sont gardés.",
                false, oldShaders.Bytes, oldShaders.Files,
                Warning: "Windows ne dit pas quand un cache a servi pour la dernière fois : un jeu lancé souvent mais dont le cache n'a pas changé " +
                         "depuis un mois recompilera ses shaders, avec quelques saccades à ses premières parties."),
            new(CleanupKind.ShaderCaches, "Caches de shaders (NVIDIA, DirectX…)",
                "Tous les caches, récents compris. À vider seulement en cas de saccades ou de bugs graphiques après une mise à jour du pilote.",
                false, shaders.Bytes, shaders.Files,
                Warning: "Les caches se reconstruisent : les premières parties de chaque jeu seront un peu saccadées."),
            new(CleanupKind.CrashDumps, "Rapports de plantage (minidumps)",
                "Fichiers créés lors d'un écran bleu, du plantage d'une application ou d'un blocage du pilote graphique.",
                false, dumps.Bytes, dumps.Files,
                Warning: "Utiles pour diagnostiquer un écran bleu : à garder si le PC plante."),
            new(CleanupKind.Thumbnails, "Miniatures de l'Explorateur",
                "Aperçus d'images, de vidéos et de documents que l'Explorateur garde en cache. Vidées par l'outil officiel Nettoyage de disque.",
                false, thumbnails.Bytes, thumbnails.Files,
                Warning: "Les miniatures se reconstruisent à l'ouverture de chaque dossier : les dossiers d'images et de vidéos " +
                         "s'afficheront plus lentement la première fois."),
        };

        if (Directory.Exists(paths.PreviousWindows))
        {
            var old = SafeDelete.Measure(paths.PreviousWindows);
            items.Add(new(CleanupKind.PreviousWindows, "Ancienne installation de Windows (Windows.old)",
                "Copie de la version précédente de Windows, gardée après une grosse mise à jour (Windows la supprime d'elle-même après quelques jours). " +
                "Supprimée par l'outil officiel Nettoyage de disque.",
                false, old.Bytes, old.Files,
                Warning: "Vous ne pourrez plus revenir à la version précédente de Windows.", IsLong: true));
        }
        return items;
    }

    /// <summary>
    /// Place récupérable connue d'une sélection. Les caches de shaders anciens font partie de « tous les caches » : cochés
    /// ensemble, ils ne sont comptés qu'une fois.
    /// </summary>
    public static long Recoverable(IReadOnlyList<CleanupItem> chosen)
    {
        var all = chosen.Any(i => i.Kind == CleanupKind.ShaderCaches);
        return chosen.Where(i => i.Bytes is not null && !(all && i.Kind == CleanupKind.OldShaderCaches)).Sum(i => i.Bytes!.Value);
    }

    private static FolderTally Sum(IEnumerable<FolderTally> tallies) => tallies.Aggregate(FolderTally.Empty, (a, b) => a.Add(b));

    /// <summary>
    /// Taille des fichiers de miniatures (« thumbcache_*.db ») posés directement dans le dossier. Les autres fichiers du
    /// dossier (cache d'icônes, journaux de l'Explorateur) ne sont pas comptés.
    /// </summary>
    internal static FolderTally MeasureThumbnails(string folder)
    {
        try
        {
            return Directory.Exists(folder)
                ? MeasureFiles(Directory.EnumerateFiles(folder, "thumbcache_*.db", SearchOption.TopDirectoryOnly).ToList())
                : FolderTally.Empty;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return FolderTally.Empty;
        }
    }

    private static FolderTally MeasureFiles(IEnumerable<string> files)
    {
        long bytes = 0;
        var count = 0;
        foreach (var f in files.Where(File.Exists))
        {
            try { bytes += new FileInfo(f).Length; count++; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return new FolderTally(bytes, count, 0);
    }

    // ---------------------------------------------------------------- Nettoyage

    public static IReadOnlyList<CleanupResult> Run(CleanupPaths paths, IReadOnlyList<CleanupItem> chosen, IProgress<CleanupStep>? progress = null)
    {
        var results = new List<CleanupResult>();
        for (var i = 0; i < chosen.Count; i++)
        {
            var item = chosen[i];
            progress?.Report(new CleanupStep(i, chosen.Count, $"{item.Title}…"));
            try
            {
                results.Add(item.Kind switch
                {
                    CleanupKind.TempFiles => FromTally(item, Sum(paths.Temp.Select(p => SafeDelete.Delete(p, DateTime.Now - TempMinAge)))),
                    CleanupKind.OldShaderCaches => FromTally(item, Sum(paths.ShaderCaches.Select(p => SafeDelete.Delete(p, DateTime.Now - OldShaderAge)))),
                    CleanupKind.ShaderCaches => FromTally(item, Sum(paths.ShaderCaches.Select(p => SafeDelete.Delete(p)))),
                    CleanupKind.CrashDumps => FromTally(item, Sum(paths.CrashDumpFolders.Select(p => SafeDelete.Delete(p))).Add(DeleteFiles(paths.CrashDumpFiles))),
                    CleanupKind.DeliveryOptimization => RunDeliveryOptimization(paths, item),
                    CleanupKind.ComponentStore => RunComponentCleanup(paths, item,
                        percent => progress?.Report(new CleanupStep(i, chosen.Count, $"{item.Title}… {percent:0} %"))),
                    CleanupKind.PreviousWindows => RunPreviousWindows(paths, item),
                    CleanupKind.Thumbnails => RunThumbnails(paths, item),
                    _ => new CleanupResult(item, CleanupStatus.Failed, 0, 0, 0, "Élément inconnu")
                });
            }
            catch (Exception e)
            {
                results.Add(new CleanupResult(item, CleanupStatus.Failed, 0, 0, 0, e.Message));
            }
        }
        progress?.Report(new CleanupStep(chosen.Count, chosen.Count, "Terminé"));
        return results;
    }

    private static CleanupResult FromTally(CleanupItem item, FolderTally tally) =>
        new(item, tally.Skipped > 0 && tally.Files == 0 ? CleanupStatus.Failed
                  : tally.Skipped > 0 ? CleanupStatus.Partial : CleanupStatus.Done,
            tally.Bytes, tally.Files, tally.Skipped,
            tally.Skipped > 0 && tally.Files == 0 ? "Fichiers en cours d'utilisation ou protégés" : null);

    private static FolderTally DeleteFiles(IEnumerable<string> files)
    {
        long bytes = 0;
        int count = 0, skipped = 0;
        foreach (var f in files.Where(File.Exists))
        {
            try
            {
                var size = new FileInfo(f).Length;
                File.Delete(f);
                bytes += size;
                count++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; }
        }
        return new FolderTally(bytes, count, skipped);
    }

    private static CleanupResult RunDeliveryOptimization(CleanupPaths paths, CleanupItem item)
    {
        var before = SafeDelete.Measure(paths.DeliveryOptimizationCache).Bytes;
        var (code, output) = RunProcess("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"Delete-DeliveryOptimizationCache -Force\"", TimeSpan.FromMinutes(10));
        var freed = Math.Max(0, before - SafeDelete.Measure(paths.DeliveryOptimizationCache).Bytes);
        return code == 0
            ? new CleanupResult(item, CleanupStatus.Done, freed, 0, 0)
            : new CleanupResult(item, CleanupStatus.Failed, freed, 0, 0, $"Commande refusée (code {code}) {LastLine(output)}".Trim());
    }

    /// <summary>DISM : l'espace libéré se mesure à l'espace libre du disque système, avant et après.</summary>
    private static CleanupResult RunComponentCleanup(CleanupPaths paths, CleanupItem item, Action<double> onPercent)
    {
        var before = FreeSpace(paths.SystemDrive);
        var (code, output) = RunProcess("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup /English", TimeSpan.FromHours(2),
            line => { if (ParseDismPercent(line) is { } p) onPercent(p); });
        var freed = Math.Max(0, FreeSpace(paths.SystemDrive) - before);
        return code is 0 or 3010
            ? new CleanupResult(item, CleanupStatus.Done, freed, 0, 0)
            : new CleanupResult(item, CleanupStatus.Failed, freed, 0, 0, $"DISM a échoué (code {code}) {LastLine(output)}".Trim());
    }

    /// <summary>
    /// Windows.old appartient au système (TrustedInstaller) : il est supprimé par Nettoyage de disque, l'outil officiel,
    /// avec un profil réservé qui ne coche que « Installations précédentes de Windows ».
    /// </summary>
    private static CleanupResult RunPreviousWindows(CleanupPaths paths, CleanupItem item)
    {
        var before = FreeSpace(paths.SystemDrive);
        if (RunDiskCleanup(PreviousInstallationsHandler, "Installations précédentes de Windows") is { } error)
            return new CleanupResult(item, CleanupStatus.Failed, 0, 0, 0, error);

        var freed = Math.Max(0, FreeSpace(paths.SystemDrive) - before);
        return Directory.Exists(paths.PreviousWindows)
            ? new CleanupResult(item, freed > 0 ? CleanupStatus.Partial : CleanupStatus.Failed, freed, 0, 0,
                "Windows.old est toujours présent : relancez Nettoyage de disque après un redémarrage.")
            : new CleanupResult(item, CleanupStatus.Done, freed, 0, 0);
    }

    /// <summary>
    /// L'Explorateur garde ses fichiers de miniatures ouverts : c'est Nettoyage de disque qui les vide, avec un profil
    /// réservé qui ne coche que « Miniatures ». Aria ne supprime elle-même aucun de ces fichiers.
    /// </summary>
    private static CleanupResult RunThumbnails(CleanupPaths paths, CleanupItem item)
    {
        var before = MeasureThumbnails(paths.ThumbnailCache).Bytes;
        if (RunDiskCleanup(ThumbnailHandler, "Miniatures") is { } error)
            return new CleanupResult(item, CleanupStatus.Failed, 0, 0, 0, error);

        var freed = Math.Max(0, before - MeasureThumbnails(paths.ThumbnailCache).Bytes);
        return freed > 0 || before == 0
            ? new CleanupResult(item, CleanupStatus.Done, freed, 0, 0)
            : new CleanupResult(item, CleanupStatus.Failed, 0, 0, 0, "Nettoyage de disque n'a rien libéré.");
    }

    /// <summary>
    /// Lance Nettoyage de disque (cleanmgr.exe, l'outil officiel) avec un profil réservé à Aria où un seul élément est coché.
    /// Le profil est remis à zéro avant, et la coche retirée après. Renvoie null si l'outil a tourné, sinon ce qui l'en a empêché.
    /// </summary>
    private static string? RunDiskCleanup(string handler, string handlerTitle)
    {
        var cleanmgr = Path.Combine(Environment.SystemDirectory, "cleanmgr.exe");
        if (!File.Exists(cleanmgr)) return "Nettoyage de disque (cleanmgr.exe) est absent de ce Windows.";
        if (!OperatingSystem.IsWindows()) return "Réservé à Windows.";

        using var root = Registry.LocalMachine.OpenSubKey(VolumeCaches, writable: true);
        if (root is null) return "Réglages de Nettoyage de disque introuvables.";
        // Profil propre : seul notre élément est coché
        foreach (var name in root.GetSubKeyNames())
        {
            using var other = root.OpenSubKey(name, writable: true);
            other?.DeleteValue(StateFlags, throwOnMissingValue: false);
        }
        using (var chosen = root.OpenSubKey(handler, writable: true))
        {
            if (chosen is null) return $"Élément « {handlerTitle} » introuvable dans Nettoyage de disque.";
            chosen.SetValue(StateFlags, 2, RegistryValueKind.DWord);
        }

        try
        {
            RunProcess(cleanmgr, $"/sagerun:{SageRun}", TimeSpan.FromHours(2));
        }
        finally
        {
            using var chosen = root.OpenSubKey(handler, writable: true);
            chosen?.DeleteValue(StateFlags, throwOnMissingValue: false);
        }
        return null;
    }

    // ---------------------------------------------------------------- Outils

    private static long FreeSpace(string drive)
    {
        try { return new DriveInfo(drive).AvailableFreeSpace; }
        catch (Exception) { return 0; }
    }

    /// <summary>Pourcentage d'une ligne de progression DISM : « [=====      10.0%      ] ».</summary>
    internal static double? ParseDismPercent(string line)
    {
        var match = Regex.Match(line, @"(\d{1,3}(?:[.,]\d)?)\s*%");
        return match.Success && double.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p <= 100
            ? p : null;
    }

    private static string LastLine(string output) =>
        output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";

    private static (int Code, string Output) RunProcess(string exe, string arguments, TimeSpan timeout, Action<string>? onLine = null)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(exe, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        var output = new System.Text.StringBuilder();
        void Collect(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null) return;
            lock (output) output.AppendLine(e.Data);
            onLine?.Invoke(e.Data);
        }
        process.OutputDataReceived += Collect;
        process.ErrorDataReceived += Collect;
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { }
            return (-1, "Délai dépassé");
        }
        process.WaitForExit();
        lock (output) return (process.ExitCode, output.ToString());
    }
}
