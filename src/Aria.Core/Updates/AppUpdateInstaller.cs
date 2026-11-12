using System.IO.Compression;
using Aria.Core.Backup;
using Aria.Core.Settings;

namespace Aria.Core.Updates;

/// <param name="Done">Vrai si tout est en place ; sinon rien n'a changé, et <paramref name="Error"/> dit pourquoi.</param>
public sealed record AppInstallResult(bool Done, string? Error = null);

/// <summary>
/// Installation d'une nouvelle version d'Aria Cockpit, à partir du zip déjà téléchargé et contrôlé
/// (<see cref="AppUpdatePackage"/>) : la version en cours est mise de côté dans « Sauvegardes\versions », puis ses
/// fichiers sont remplacés à côté de l'exe. Windows interdit d'écraser un exe qui tourne mais permet de le renommer :
/// l'exe en cours devient « .ancien », le nouveau prend son nom, et la version relancée efface l'ancien fichier.
/// Si une étape échoue, tout est remis comme avant. Une seule version est gardée : la précédente.
/// </summary>
public static class AppUpdateInstaller
{
    public const string ExeName = "Aria-Cockpit.exe";
    public const string BenchName = "Aria-Bench.exe";

    /// <summary>Suffixe du fichier remplacé, tant que la version relancée ne l'a pas effacé.</summary>
    public const string OldSuffix = ".ancien";
    private const string NewSuffix = ".nouveau";
    private const string TempSuffix = ".tmp";

    /// <summary>Sous-dossier des sauvegardes où la version précédente est gardée.</summary>
    public const string VersionsFolderName = "versions";

    /// <summary>Fichier du cache qui dit à la version relancée ce qui vient d'être installé (« 0.26.0&gt;0.27.0 »).</summary>
    public const string DoneMarker = "maj-faite.txt";

    /// <summary>Taille totale au-delà de laquelle un zip n'est pas décompressé (les deux exes font environ 260 Mo).</summary>
    internal const long MaxExtractedBytes = 1L << 30;

    /// <summary>Les deux programmes : sans eux, le zip n'est pas une version d'Aria Cockpit.</summary>
    internal static readonly string[] Executables = [ExeName, BenchName];

    /// <summary>Seuls fichiers sortis d'un zip. Tout autre contenu est ignoré, jamais écrit.</summary>
    internal static readonly string[] Expected = [ExeName, BenchName, "LICENSE", "THIRD-PARTY-NOTICES.txt"];

    public static string VersionsFolder => Path.Combine(BackupStore.Folder, VersionsFolderName);

    /// <summary>
    /// Pourquoi Aria ne peut pas se remplacer elle-même depuis cet exe ; null si elle le peut. Lancée par une tâche de
    /// démarrage, elle tourne depuis sa copie de Program Files : la remplacer laisserait l'exe de l'utilisateur à
    /// l'ancienne version, et son prochain lancement remettrait l'ancienne copie.
    /// </summary>
    public static string? WhyNotInstallable(string? exe) => WhyNotInstallable(exe, LogonExe.Folder);

    internal static string? WhyNotInstallable(string? exe, string protectedFolder)
    {
        if (string.IsNullOrEmpty(exe)) return "Aria ne retrouve pas le chemin de son propre exe.";
        var full = Path.GetFullPath(exe);
        if (!string.Equals(Path.GetFileName(full), ExeName, StringComparison.OrdinalIgnoreCase))
            return $"L'exe a été renommé : Aria ne se remplace que si son fichier s'appelle {ExeName}.";
        var folder = Path.GetFullPath(protectedFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (full.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            return "Aria a été lancée par le démarrage automatique, depuis sa copie de Program Files. " +
                   "Quitte-la, ouvre-la depuis ton exe, puis relance la mise à jour.";
        return null;
    }

    // ------------------------------------------------------------------ Installer

    /// <summary>
    /// Installe la version contenue dans <paramref name="zip"/> à la place de celle qui tourne. Ne lève jamais.
    /// Le zip doit déjà avoir été contrôlé ; il l'est de nouveau ici, juste avant d'être ouvert.
    /// </summary>
    public static AppInstallResult Install(AppUpdateAsset asset, Version latest, Version current, string? exe)
    {
        if (WhyNotInstallable(exe) is { } reason) return new AppInstallResult(false, reason);
        if (AppUpdatePackage.AlreadyDownloaded(asset) is not { } zip)
            return new AppInstallResult(false, "Le fichier téléchargé n'est plus là, ou il a changé depuis son contrôle. Télécharge-le de nouveau.");
        var staging = Path.Combine(DataCache.Folder, $"{AppUpdatePackage.FilePrefix}{latest.ToString(3)}");
        try
        {
            var extracted = Extract(zip, staging);
            if (!extracted.Done) return extracted;
            var result = Install(staging, Path.GetDirectoryName(Path.GetFullPath(exe!))!, current, VersionsFolder);
            if (result.Done) WriteMarker(DataCache.Folder, current, latest);
            return result;
        }
        finally
        {
            DeleteFolder(staging);
        }
    }

    /// <summary>Met de côté la version en cours, puis remplace ses fichiers par ceux de <paramref name="staging"/>.</summary>
    internal static AppInstallResult Install(string staging, string exeFolder, Version current, string versionsFolder)
    {
        try
        {
            Keep(exeFolder, current, versionsFolder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new AppInstallResult(false, $"La version actuelle n'a pas pu être mise de côté : {e.Message}");
        }
        return Swap(staging, exeFolder);
    }

    /// <summary>
    /// Sort du zip les fichiers attendus, et eux seuls, dans <paramref name="staging"/> (vidé d'abord). Le nom écrit
    /// est celui de la liste, jamais celui que le zip annonce : un zip piégé ne peut rien écrire ailleurs.
    /// </summary>
    internal static AppInstallResult Extract(string zip, string staging)
    {
        try
        {
            DeleteFolder(staging);
            Directory.CreateDirectory(staging);
            using var archive = ZipFile.OpenRead(zip);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                var name = Expected.FirstOrDefault(n => string.Equals(n, entry.FullName, StringComparison.Ordinal));
                if (name is null) continue;
                if (!seen.Add(name)) return Refuse(staging, $"Le zip contient deux fois « {name} ».");
                total += entry.Length;
                if (total > MaxExtractedBytes) return Refuse(staging, "Le zip est anormalement gros une fois décompressé.");
                entry.ExtractToFile(Path.Combine(staging, name), overwrite: false);
            }
            var missing = Executables.Where(n => !seen.Contains(n)).ToList();
            return missing.Count == 0
                ? new AppInstallResult(true)
                : Refuse(staging, $"Le zip ne contient pas {string.Join(" ni ", missing)}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Refuse(staging, $"Le zip n'a pas pu être ouvert : {e.Message}");
        }
    }

    private static AppInstallResult Refuse(string staging, string error)
    {
        DeleteFolder(staging);
        return new AppInstallResult(false, error);
    }

    /// <summary>
    /// Copie les programmes de la version en cours dans <paramref name="versionsFolder"/>, sous son numéro, et retire
    /// les versions gardées avant elle. La copie se fait à côté, puis prend sa place : une copie interrompue ne
    /// remplace jamais une version déjà gardée.
    /// </summary>
    internal static void Keep(string exeFolder, Version current, string versionsFolder)
    {
        var name = current.ToString(3);
        var temp = Path.Combine(versionsFolder, name + TempSuffix);
        DeleteFolder(temp);
        Directory.CreateDirectory(temp);
        foreach (var file in Executables)
        {
            var source = Path.Combine(exeFolder, file);
            // Aria-Bench.exe peut manquer (exe copié seul) : la version gardée est alors l'exe seul
            if (File.Exists(source)) File.Copy(source, Path.Combine(temp, file), overwrite: true);
        }
        if (!File.Exists(Path.Combine(temp, ExeName)))
        {
            DeleteFolder(temp);
            throw new FileNotFoundException($"{ExeName} introuvable dans « {exeFolder} ».");
        }
        foreach (var other in Directory.GetDirectories(versionsFolder))
            if (!string.Equals(other, temp, StringComparison.OrdinalIgnoreCase)) Directory.Delete(other, recursive: true);
        Directory.Move(temp, Path.Combine(versionsFolder, name));
    }

    /// <summary>
    /// Remplace, dans <paramref name="exeFolder"/>, chaque fichier attendu par celui de <paramref name="source"/>.
    /// Le fichier en place est renommé en « .ancien » (un exe qui tourne se renomme, il ne s'écrase pas). À la
    /// première erreur, tout ce qui a été fait est défait.
    /// </summary>
    internal static AppInstallResult Swap(string source, string exeFolder)
    {
        var done = new List<(string Target, bool HadOld)>();
        foreach (var name in Expected)
        {
            var from = Path.Combine(source, name);
            if (!File.Exists(from)) continue;
            var target = Path.Combine(exeFolder, name);
            var fresh = target + NewSuffix;
            var old = target + OldSuffix;
            var hadOld = false;
            try
            {
                File.Copy(from, fresh, overwrite: true);
                if (File.Exists(target))
                {
                    File.Delete(old);
                    File.Move(target, old);
                    hadOld = true;
                }
                File.Move(fresh, target);
                done.Add((target, hadOld));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                TryDelete(fresh);
                // Le fichier en cours avait déjà été mis de côté : il reprend sa place
                if (hadOld && !File.Exists(target)) TryMove(old, target);
                for (var i = done.Count - 1; i >= 0; i--)
                {
                    TryDelete(done[i].Target);
                    if (done[i].HadOld) TryMove(done[i].Target + OldSuffix, done[i].Target);
                }
                return new AppInstallResult(false, $"« {name} » n'a pas pu être remplacé : {e.Message} Rien n'a été changé.");
            }
        }
        return done.Count > 0 ? new AppInstallResult(true) : new AppInstallResult(false, "Aucun fichier à installer.");
    }

    // ------------------------------------------------------------------ Revenir à la version gardée

    /// <summary>Version gardée de côté ; null s'il n'y en a pas.</summary>
    public static Version? KeptVersion() => KeptVersion(VersionsFolder);

    internal static Version? KeptVersion(string versionsFolder)
    {
        try
        {
            if (!Directory.Exists(versionsFolder)) return null;
            return Directory.GetDirectories(versionsFolder)
                .Where(d => File.Exists(Path.Combine(d, ExeName)))
                .Select(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : null)
                .Where(v => v is not null)
                .OrderDescending()
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Remet la version gardée à la place de celle qui tourne, et garde à son tour celle qu'on quitte. Ne lève jamais.
    /// </summary>
    public static AppInstallResult Restore(Version current, string? exe)
    {
        if (WhyNotInstallable(exe) is { } reason) return new AppInstallResult(false, reason);
        var kept = KeptVersion();
        var result = Restore(Path.GetDirectoryName(Path.GetFullPath(exe!))!, current, VersionsFolder);
        if (result.Done && kept is not null) WriteMarker(DataCache.Folder, current, kept);
        return result;
    }

    internal static AppInstallResult Restore(string exeFolder, Version current, string versionsFolder)
    {
        if (KeptVersion(versionsFolder) is not { } kept) return new AppInstallResult(false, "Aucune version n'est gardée de côté.");
        var keptFolder = Path.Combine(versionsFolder, kept.ToString(3));
        var temp = Path.Combine(versionsFolder, current.ToString(3) + TempSuffix);
        try
        {
            // La version qu'on quitte est copiée d'abord : si la suite échoue, la version gardée n'a pas bougé
            DeleteFolder(temp);
            Directory.CreateDirectory(temp);
            foreach (var file in Executables)
            {
                var source = Path.Combine(exeFolder, file);
                if (File.Exists(source)) File.Copy(source, Path.Combine(temp, file), overwrite: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            DeleteFolder(temp);
            return new AppInstallResult(false, $"La version actuelle n'a pas pu être mise de côté : {e.Message}");
        }

        var swapped = Swap(keptFolder, exeFolder);
        if (!swapped.Done)
        {
            DeleteFolder(temp);
            return swapped;
        }
        try
        {
            Directory.Delete(keptFolder, recursive: true);
            var target = Path.Combine(versionsFolder, current.ToString(3));
            DeleteFolder(target);
            Directory.Move(temp, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // L'ancienne version est en place ; seule la mise de côté de celle qu'on quitte est incomplète
        }
        return new AppInstallResult(true);
    }

    // ------------------------------------------------------------------ Après la relance

    private static void WriteMarker(string cache, Version from, Version to)
    {
        try
        {
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, DoneMarker), $"{from.ToString(3)}>{to.ToString(3)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Sans ce fichier, la version relancée ne dit pas qu'elle vient d'être installée : rien de plus
        }
    }

    /// <summary>
    /// Au lancement : ce qui vient d'être installé (version quittée, version en place), lu une seule fois ; null si
    /// Aria n'a pas été relancée par une mise à jour.
    /// </summary>
    public static (Version From, Version To)? TakeDoneMarker() => TakeDoneMarker(DataCache.Folder);

    internal static (Version From, Version To)? TakeDoneMarker(string cache)
    {
        var file = Path.Combine(cache, DoneMarker);
        try
        {
            if (!File.Exists(file)) return null;
            var parts = File.ReadAllText(file).Trim().Split('>');
            File.Delete(file);
            return parts.Length == 2 && Version.TryParse(parts[0], out var from) && Version.TryParse(parts[1], out var to)
                ? (from, to)
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Au lancement : efface les fichiers « .ancien » laissés à côté de l'exe par le dernier remplacement, et les
    /// dossiers de décompression restés dans le cache. Un fichier encore tenu partira au lancement suivant.
    /// </summary>
    public static void CleanUp(string? exe)
    {
        if (!string.IsNullOrEmpty(exe) && Path.GetDirectoryName(Path.GetFullPath(exe)) is { } folder) CleanUp(folder, DataCache.Folder);
    }

    internal static void CleanUp(string exeFolder, string cache)
    {
        foreach (var name in Expected)
        {
            TryDelete(Path.Combine(exeFolder, name + OldSuffix));
            TryDelete(Path.Combine(exeFolder, name + NewSuffix));
        }
        try
        {
            if (!Directory.Exists(cache)) return;
            foreach (var staging in Directory.GetDirectories(cache, AppUpdatePackage.FilePrefix + "*")) DeleteFolder(staging);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            if (File.Exists(from)) File.Move(from, to);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
