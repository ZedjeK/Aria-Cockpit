using System.Diagnostics;
using Microsoft.Win32;

namespace Aria.Core.Startup;

/// <summary>Où Windows trouve un programme à lancer à l'ouverture de session.</summary>
public enum StartupSource
{
    /// <summary>HKCU\…\Run : pour l'utilisateur courant.</summary>
    UserRun,
    /// <summary>HKLM\…\Run : pour tous les utilisateurs.</summary>
    MachineRun,
    /// <summary>HKLM\WOW6432Node\…\Run : programmes 32 bits, tous les utilisateurs.</summary>
    MachineRun32,
    /// <summary>Dossier Démarrage de l'utilisateur.</summary>
    UserFolder,
    /// <summary>Dossier Démarrage commun à tous les utilisateurs.</summary>
    CommonFolder
}

/// <param name="Name">Nom de la valeur du registre, ou nom du fichier dans le dossier Démarrage.</param>
/// <param name="Command">Commande lancée (registre) ou chemin du raccourci (dossier).</param>
/// <param name="DisplayName">Nom lisible : description de l'exécutable si disponible, sinon <paramref name="Name"/>.</param>
/// <param name="Publisher">Éditeur inscrit dans l'exécutable (« Discord Inc. »), comme l'affiche Windows ; null s'il n'y en a pas.</param>
public sealed record StartupEntry(StartupSource Source, string Name, string Command, string DisplayName, bool Enabled, string? Publisher = null)
{
    /// <summary>Composants de sécurité de Windows : jamais proposés à la désactivation.</summary>
    public bool IsProtected => StartupApps.IsProtected(this);
}

/// <summary>
/// Programmes lancés à l'ouverture de session et leur état, avec le mécanisme du Gestionnaire des tâches :
/// la désactivation est notée dans Explorer\StartupApproved, sans toucher à l'entrée elle-même.
/// Rien n'est supprimé : l'entrée reste en place et se réactive à tout moment.
/// </summary>
public static class StartupApps
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    /// <summary>Toutes les entrées de démarrage, activées ou non.</summary>
    public static IReadOnlyList<StartupEntry> Scan()
    {
        var entries = new List<StartupEntry>();
        if (!OperatingSystem.IsWindows()) return entries;
        foreach (var source in Enum.GetValues<StartupSource>())
        {
            try
            {
                entries.AddRange(source is StartupSource.UserFolder or StartupSource.CommonFolder ? ScanFolder(source) : ScanRun(source));
            }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                // Une source illisible n'empêche pas de lire les autres
            }
        }
        return entries;
    }

    /// <summary>État actuel d'une entrée (relu à chaque fois : la valeur a pu changer depuis l'analyse).</summary>
    public static bool IsEnabled(StartupSource source, string name)
    {
        using var approved = Hive(source).OpenSubKey($@"{ApprovedKey}\{ApprovedSubKey(source)}");
        return IsEnabled(approved?.GetValue(name));
    }

    /// <summary>Active ou désactive une entrée, comme le Gestionnaire des tâches.</summary>
    public static void SetEnabled(StartupSource source, string name, bool enabled)
    {
        using var approved = Hive(source).CreateSubKey($@"{ApprovedKey}\{ApprovedSubKey(source)}", writable: true)
                             ?? throw new InvalidOperationException("Clé StartupApproved inaccessible.");
        approved.SetValue(name, ApprovedValue(enabled, DateTime.UtcNow), RegistryValueKind.Binary);
    }

    /// <summary>
    /// Valeur notée par le Gestionnaire des tâches : 12 octets, le 1er pair = activé (02), impair = désactivé (03) ;
    /// les 8 derniers donnent la date de désactivation (FILETIME).
    /// </summary>
    internal static byte[] ApprovedValue(bool enabled, DateTime utcNow)
    {
        var value = new byte[12];
        value[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled) BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(value, 4);
        return value;
    }

    /// <summary>Absente ou 1er octet pair = activée.</summary>
    internal static bool IsEnabled(object? approvedValue) =>
        approvedValue is not byte[] { Length: > 0 } state || (state[0] & 1) == 0;

    internal static bool IsProtected(StartupEntry entry)
    {
        if (entry.Name.Equals("SecurityHealth", StringComparison.OrdinalIgnoreCase)) return true;
        var exe = ExecutablePath(entry.Command) ?? "";
        return exe.Contains(@"\Windows Defender\", StringComparison.OrdinalIgnoreCase)
               || Path.GetFileName(exe).Equals("SecurityHealthSystray.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Chemin de l'exécutable dans une commande : « "C:\a b\x.exe" -arg » ou « C:\x.exe /arg ».</summary>
    internal static string? ExecutablePath(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.Length == 0) return null;
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }
        var exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return text[..(exe + 4)];
        var space = text.IndexOf(' ');
        return space > 0 ? text[..space] : text;
    }

    private static IEnumerable<StartupEntry> ScanRun(StartupSource source)
    {
        using var key = Hive(source).OpenSubKey(source == StartupSource.MachineRun32 ? Run32Key : RunKey);
        if (key is null) yield break;
        using var approved = Hive(source).OpenSubKey($@"{ApprovedKey}\{ApprovedSubKey(source)}");
        foreach (var name in key.GetValueNames().Where(n => n.Length > 0))
        {
            if (key.GetValue(name) is not string command) continue;
            var (display, publisher) = Describe(name, command);
            yield return new StartupEntry(source, name, command, display, IsEnabled(approved?.GetValue(name)), publisher);
        }
    }

    private static IEnumerable<StartupEntry> ScanFolder(StartupSource source)
    {
        var folder = Environment.GetFolderPath(source == StartupSource.UserFolder
            ? Environment.SpecialFolder.Startup
            : Environment.SpecialFolder.CommonStartup);
        if (!Directory.Exists(folder)) yield break;
        using var approved = Hive(source).OpenSubKey($@"{ApprovedKey}\StartupFolder");
        foreach (var file in Directory.GetFiles(folder))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            yield return new StartupEntry(source, name, file, Path.GetFileNameWithoutExtension(name), IsEnabled(approved?.GetValue(name)));
        }
    }

    /// <summary>
    /// Nom et éditeur, comme dans Paramètres → Applications → Démarrage : description de l'exécutable
    /// (à défaut, nom de la valeur) et société inscrite dans le fichier.
    /// </summary>
    private static (string Display, string? Publisher) Describe(string name, string command)
    {
        try
        {
            if (ExecutablePath(command) is { } exe && File.Exists(exe))
            {
                var info = FileVersionInfo.GetVersionInfo(exe);
                var description = (info.FileDescription ?? "").Trim();
                var publisher = (info.CompanyName ?? "").Trim();
                var display = description.Length >= 3 && !description.Equals(Path.GetFileName(exe), StringComparison.OrdinalIgnoreCase)
                    ? description
                    : name;
                return (display, publisher.Length > 0 ? publisher : null);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Nom de la valeur à défaut
        }
        return (name, null);
    }

    private static RegistryKey Hive(StartupSource source) =>
        source is StartupSource.UserRun or StartupSource.UserFolder ? Registry.CurrentUser : Registry.LocalMachine;

    private static string ApprovedSubKey(StartupSource source) => source switch
    {
        StartupSource.MachineRun32 => "Run32",
        StartupSource.UserFolder or StartupSource.CommonFolder => "StartupFolder",
        _ => "Run"
    };
}
