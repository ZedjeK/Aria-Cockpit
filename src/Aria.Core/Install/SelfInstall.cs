using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using Aria.Core.Backup;
using Aria.Core.Settings;
using Aria.Core.Updates;

namespace Aria.Core.Install;

/// <summary>Où tourne l'exe : chez lui, dans la copie de démarrage, ou ailleurs (zip décompressé).</summary>
public enum InstallPlace
{
    /// <summary>Chemin illisible : on ne propose rien.</summary>
    Unknown,
    /// <summary>Dans <c>.Aria-Cockpit\programme</c> : installée.</summary>
    Home,
    /// <summary>Dans la copie de Program Files, lancée par les tâches de démarrage.</summary>
    ProtectedCopy,
    /// <summary>Téléchargements, Bureau, clé USB… : l'exe peut s'installer.</summary>
    Elsewhere
}

/// <param name="Done">Vrai si les fichiers sont en place ; sinon rien n'a changé, et <paramref name="Error"/> dit pourquoi.</param>
/// <param name="Exe">Exe installé, à lancer.</param>
/// <param name="BenchMissing">Aria-Bench.exe n'était pas à côté de l'exe : l'assistant de Réglage GPU ne pourra pas mesurer.</param>
/// <param name="ShortcutDone">Le raccourci du Bureau a été créé ou mis à jour.</param>
/// <param name="ShortcutError">Le raccourci demandé n'a pas pu être créé (l'installation, elle, est faite).</param>
public sealed record SelfInstallResult(
    bool Done,
    string? Error = null,
    string? Exe = null,
    bool BenchMissing = false,
    bool ShortcutDone = false,
    string? ShortcutError = null);

/// <summary>
/// Installation d'Aria Cockpit par son propre exe : lancé depuis le zip décompressé, l'exe propose de se ranger dans
/// <c>%USERPROFILE%\.Aria-Cockpit\programme</c> avec son banc d'essai, et de créer un raccourci sur le Bureau (case
/// décochée d'office). Ce n'est pas un installeur : rien n'est écrit en dehors de ce dossier et du raccourci
/// demandé, et l'exe reste utilisable là où il est (« Lancer sans installer »).
///
/// Ce dossier est celui des données, que seuls les administrateurs peuvent modifier : c'est ce qui rend sûr d'y
/// lancer un exe élevé, là où un exe resté dans Téléchargements pourrait être remplacé par n'importe quel programme.
/// L'installation vérifie donc cette protection avant de copier, et refuse si elle manque.
/// </summary>
public static class SelfInstall
{
    public const string FolderName = "programme";

    /// <summary>Argument qui dispense Aria de proposer l'installation, pour ce lancement.</summary>
    public const string PortableArgument = "--portable";

    public static string Folder => Path.Combine(BackupStore.DataRoot, FolderName);

    public static string HomeExe => Path.Combine(Folder, AppUpdateInstaller.ExeName);

    // ------------------------------------------------------------------ Où est l'exe

    public static InstallPlace PlaceOf(string? exe) => PlaceOf(exe, Folder, LogonExe.Folder);

    internal static InstallPlace PlaceOf(string? exe, string home, string protectedFolder)
    {
        if (string.IsNullOrEmpty(exe)) return InstallPlace.Unknown;
        var folder = Path.GetDirectoryName(Path.GetFullPath(exe));
        if (folder is null) return InstallPlace.Unknown;
        if (SameFolder(folder, home)) return InstallPlace.Home;
        if (SameFolder(folder, protectedFolder)) return InstallPlace.ProtectedCopy;
        return InstallPlace.Elsewhere;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Proposer l'installation : exe lancé ailleurs que chez lui, proposition non coupée, pas de « --portable ».</summary>
    public static bool ShouldOffer(string? exe, AppPreferences preferences, IEnumerable<string> args) =>
        preferences.OfferInstall
        && !args.Contains(PortableArgument, StringComparer.OrdinalIgnoreCase)
        && PlaceOf(exe) == InstallPlace.Elsewhere;

    /// <summary>Version installée dans le dossier du programme ; null s'il n'y en a pas ou si elle est illisible.</summary>
    public static Version? InstalledVersion() => InstalledVersion(Folder);

    internal static Version? InstalledVersion(string home)
    {
        try
        {
            var exe = Path.Combine(home, AppUpdateInstaller.ExeName);
            return File.Exists(exe) && Version.TryParse(FileVersionInfo.GetVersionInfo(exe).ProductVersion, out var version) ? version : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ Droits du dossier

    /// <summary>
    /// Pourquoi le dossier ne convient pas pour y lancer un exe élevé ; null s'il est sûr. Sûr : seuls le système et
    /// les administrateurs peuvent y écrire, et l'un d'eux en est le propriétaire (un propriétaire peut toujours
    /// changer les droits).
    /// </summary>
    public static string? WhyUnsafe(string folder)
    {
        try
        {
            return WhyUnsafe(new DirectoryInfo(folder).GetAccessControl());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"ses droits n'ont pas pu être lus ({e.Message})";
        }
    }

    private static readonly FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    internal static string? WhyUnsafe(DirectorySecurity security)
    {
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || !IsTrusted(owner)) return $"son propriétaire est {Describe(owner)}, qui peut en changer les droits";

        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference is SecurityIdentifier who && IsTrusted(who)) continue;
            if ((rule.FileSystemRights & WriteRights) != 0)
                return $"{Describe(rule.IdentityReference as SecurityIdentifier)} peut y écrire";
        }
        return null;
    }

    /// <summary>Système, administrateurs, TrustedInstaller : ceux à qui Windows réserve déjà Program Files.</summary>
    private static bool IsTrusted(SecurityIdentifier sid) =>
        sid.IsWellKnown(WellKnownSidType.LocalSystemSid)
        || sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid)
        || sid.Value.StartsWith("S-1-5-80-", StringComparison.Ordinal);

    private static string Describe(SecurityIdentifier? sid)
    {
        if (sid is null) return "inconnu";
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }

    // ------------------------------------------------------------------ Installer

    /// <summary>
    /// Range l'exe, son banc d'essai et les licences dans le dossier du programme, en gardant la version qui s'y
    /// trouvait. Ne lève jamais : à la moindre erreur, rien n'a changé et le résultat dit pourquoi.
    /// </summary>
    public static SelfInstallResult Install(string exe, bool shortcut) =>
        Install(exe, shortcut, BackupStore.DataRoot, Folder, AppUpdateInstaller.VersionsFolder, DesktopShortcut.DefaultPath, DataCache.Folder, WhyUnsafe);

    /// <param name="whyUnsafe">Contrôle des droits d'un dossier (<see cref="WhyUnsafe(string)"/>), remplaçable dans les tests.</param>
    internal static SelfInstallResult Install(string exe, bool shortcut, string dataRoot, string home, string versions, string shortcutPath, string cache,
        Func<string, string?> whyUnsafe)
    {
        var source = Path.GetFullPath(exe);
        if (!File.Exists(source)) return Failed("L'exe d'Aria est introuvable.");
        if (whyUnsafe(dataRoot) is { } reason)
            return Failed($"Le dossier des données n'est pas protégé : {reason}. Aria ne se range pas dans un dossier qu'un programme sans droits pourrait modifier.");

        var staging = Path.Combine(cache, "installation");
        try
        {
            Stage(source, staging);
            Directory.CreateDirectory(home);
            // Le dossier du programme hérite des droits des données : on le vérifie, il ne suffit pas de le supposer
            if (whyUnsafe(home) is { } homeReason)
                return Failed($"Le dossier du programme n'est pas protégé : {homeReason}.");

            if (InstalledVersion(home) is { } installed)
            {
                try
                {
                    AppUpdateInstaller.Keep(home, installed, versions);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return Failed($"La version installée n'a pas pu être mise de côté : {e.Message}");
                }
            }

            var swapped = AppUpdateInstaller.Swap(staging, home);
            if (!swapped.Done) return Failed(swapped.Error ?? "Les fichiers n'ont pas pu être copiés.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Failed($"Les fichiers n'ont pas pu être copiés : {e.Message}");
        }
        finally
        {
            DeleteFolder(staging);
        }

        var installedExe = Path.Combine(home, AppUpdateInstaller.ExeName);
        var benchMissing = !File.Exists(Path.Combine(home, AppUpdateInstaller.BenchName));
        if (!shortcut) return new SelfInstallResult(true, Exe: installedExe, BenchMissing: benchMissing);

        try
        {
            DesktopShortcut.Create(shortcutPath, installedExe);
            return new SelfInstallResult(true, Exe: installedExe, BenchMissing: benchMissing, ShortcutDone: true);
        }
        catch (IOException e)
        {
            return new SelfInstallResult(true, Exe: installedExe, BenchMissing: benchMissing, ShortcutError: e.Message);
        }
    }

    private static SelfInstallResult Failed(string error) => new(false, error);

    /// <summary>
    /// Prépare les fichiers sous leur nom d'origine, quel que soit celui que le navigateur a donné à l'exe
    /// (« Aria-Cockpit (1).exe »). Le contenu est recopié sans la marque « téléchargé depuis Internet » : l'utilisateur
    /// a lancé cet exe et accepté l'installation, et la mise à jour intégrée écrit de même des fichiers sans cette marque.
    /// </summary>
    private static void Stage(string source, string staging)
    {
        DeleteFolder(staging);
        Directory.CreateDirectory(staging);
        CopyContent(source, Path.Combine(staging, AppUpdateInstaller.ExeName));
        var beside = Path.GetDirectoryName(source)!;
        foreach (var name in AppUpdateInstaller.Expected.Where(n => n != AppUpdateInstaller.ExeName))
        {
            var file = Path.Combine(beside, name);
            if (File.Exists(file)) CopyContent(file, Path.Combine(staging, name));
        }
    }

    private static void CopyContent(string from, string to)
    {
        using (var input = File.OpenRead(from))
        using (var output = File.Create(to))
            input.CopyTo(output);
        File.SetLastWriteTimeUtc(to, File.GetLastWriteTimeUtc(from));
    }

    private static void DeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Dossier de travail resté : le ménage du cache le retirera
        }
    }
}
