using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Aria.Core.Updates;

/// <summary>Un programme ouvert, identifié par son chemin (gardé pour revérifier juste avant de le fermer).</summary>
public sealed record OpenProcess(int Id, string Name, string Path);

/// <summary>Une application à mettre à jour actuellement ouverte.</summary>
public sealed record RunningApp(UpdateItem Item, IReadOnlyList<OpenProcess> Processes)
{
    public IReadOnlyList<string> ProcessNames =>
        Processes.Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

/// <summary>Une entrée de la liste « Applications installées » de Windows.</summary>
public sealed record InstalledEntry(string DisplayName, string? InstallLocation, string? DisplayIcon, string? UninstallString);

/// <summary>
/// Repère les applications ouvertes parmi celles à mettre à jour, puis les ferme.
/// Prudence avant tout : un programme n'est retenu que s'il est lancé depuis le dossier d'installation
/// de l'application (trouvé dans le registre), et les dossiers trop larges sont refusés.
/// </summary>
public static class RunningApps
{
    public static IReadOnlyList<RunningApp> Find(IReadOnlyList<UpdateItem> items)
    {
        var apps = items.Where(i => i.Source == UpdateSource.Application).ToList();
        if (apps.Count == 0) return [];

        var entries = InstalledEntries.Read();
        var guard = FolderGuard.FromEnvironment();
        var processes = ProcessPaths.Snapshot();

        var result = new List<RunningApp>();
        foreach (var app in apps)
        {
            var folders = InstalledEntries.FoldersFor(entries, app.Title).Where(guard.IsSpecific).ToList();
            var matches = processes.Where(p =>
                    folders.Any(f => IsUnder(p.Path, f)) ||
                    (app.PackageId is { } id && ProcessPaths.IsInPackageFolder(p.Path, id)))
                .ToList();
            if (matches.Count > 0) result.Add(new RunningApp(app, matches));
        }
        return result;
    }

    /// <summary>
    /// Fermeture normale (comme un clic sur la croix), puis forcée après le délai de grâce.
    /// Seuls les programmes repérés sont fermés : jamais toute leur arborescence (un jeu lancé par Steam, par exemple).
    /// </summary>
    /// <returns>Une ligne par application, pour le résultat et le rapport.</returns>
    public static IReadOnlyList<string> Close(IReadOnlyList<RunningApp> apps, TimeSpan grace)
    {
        var targets = new List<(RunningApp App, Process Process)>();
        foreach (var app in apps)
        {
            foreach (var p in app.Processes)
            {
                // Le numéro d'un programme peut être réutilisé : on vérifie que c'est toujours le même exécutable
                if (ProcessPaths.PathOf(p.Id) is not { } path || !path.Equals(p.Path, StringComparison.OrdinalIgnoreCase)) continue;
                try { targets.Add((app, Process.GetProcessById(p.Id))); }
                catch (ArgumentException) { /* déjà fermé */ }
            }
        }

        foreach (var (_, process) in targets)
        {
            try { process.CloseMainWindow(); }
            catch (InvalidOperationException) { /* déjà fermé */ }
        }

        var deadline = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < deadline && targets.Any(t => !HasExited(t.Process))) Thread.Sleep(250);

        var messages = new List<string>();
        foreach (var app in apps)
        {
            var mine = targets.Where(t => t.App == app).Select(t => t.Process).ToList();
            var alive = mine.Where(p => !HasExited(p)).ToList();
            if (alive.Count == 0)
            {
                messages.Add($"{app.Item.Title} : fermé normalement.");
                continue;
            }

            var failed = false;
            foreach (var process in alive)
            {
                try
                {
                    process.Kill(entireProcessTree: false);
                    process.WaitForExit(3000);
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    failed |= !HasExited(process);
                }
            }
            messages.Add(failed
                ? $"{app.Item.Title} : impossible à fermer, sa mise à jour risque d'échouer."
                : $"{app.Item.Title} : fermeture forcée après {grace.TotalSeconds:0} s.");
        }

        foreach (var (_, process) in targets) process.Dispose();
        return messages;
    }

    internal static bool IsUnder(string path, string folder) =>
        path.StartsWith(folder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return true; }
    }
}

/// <summary>Lecture de la liste « Applications installées » (clés Uninstall du registre).</summary>
public static class InstalledEntries
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";

    public static IReadOnlyList<InstalledEntry> Read()
    {
        var entries = new List<InstalledEntry>();
        foreach (var (hive, path) in new[] { (Registry.LocalMachine, UninstallKey), (Registry.LocalMachine, UninstallKey32), (Registry.CurrentUser, UninstallKey) })
        {
            try
            {
                using var root = hive.OpenSubKey(path);
                if (root is null) continue;
                foreach (var name in root.GetSubKeyNames())
                {
                    using var key = root.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is not string displayName || displayName.Length == 0) continue;
                    entries.Add(new InstalledEntry(displayName,
                        key.GetValue("InstallLocation") as string,
                        key.GetValue("DisplayIcon") as string,
                        key.GetValue("UninstallString") as string));
                }
            }
            catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                // Clé illisible : on fait sans.
            }
        }
        return entries;
    }

    /// <summary>Dossiers d'installation probables d'une application, d'après son nom affiché par winget.</summary>
    public static IReadOnlyList<string> FoldersFor(IEnumerable<InstalledEntry> entries, string wingetName)
    {
        var folders = new List<string>();
        foreach (var entry in entries.Where(e => NameMatches(e.DisplayName, wingetName)))
        {
            AddFolder(folders, entry.InstallLocation);
            AddFolder(folders, ExePath(entry.DisplayIcon) is { } icon ? WinPath.DirectoryOf(icon) : null);
            AddFolder(folders, ExePath(entry.UninstallString) is { } uninstaller ? WinPath.DirectoryOf(uninstaller) : null);
        }
        return folders;
    }

    /// <summary>winget affiche le nom de la liste Windows, parfois tronqué avec « … ».</summary>
    internal static bool NameMatches(string displayName, string wingetName)
    {
        var name = wingetName.Trim();
        if (name.EndsWith('…'))
        {
            var prefix = name[..^1].TrimEnd();
            return prefix.Length >= 4 && displayName.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }
        return string.Equals(displayName.Trim(), name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Chemin d'un exécutable dans une valeur du registre :
    /// « "C:\X\app.exe",0 » · « C:\X\Uninstall.exe /S » · « MsiExec.exe /X{…} » (ignoré, ce n'est pas l'application).
    /// </summary>
    internal static string? ExePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();

        string path;
        if (v.StartsWith('"'))
        {
            var end = v.IndexOf('"', 1);
            if (end < 0) return null;
            path = v[1..end];
        }
        else
        {
            var exe = v.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe < 0) return null;
            path = v[..(exe + 4)];
        }

        path = path.Trim();
        if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !WinPath.IsFullyQualified(path)) return null;
        return WinPath.FileName(path).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase) ? null : path;
    }

    private static void AddFolder(List<string> folders, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        var f = folder.Trim().Trim('"').TrimEnd('\\');
        if (!WinPath.IsFullyQualified(f)) return;
        if (!folders.Contains(f, StringComparer.OrdinalIgnoreCase)) folders.Add(f);
    }
}

/// <summary>Refuse les dossiers trop larges, pour ne jamais fermer un programme qui n'a rien à voir.</summary>
public sealed class FolderGuard(IEnumerable<string> broadFolders, IEnumerable<string> forbiddenTrees)
{
    private readonly List<string> _broad = broadFolders.Where(f => !string.IsNullOrEmpty(f)).Select(Normalize).ToList();
    private readonly List<string> _forbidden = forbiddenTrees.Where(f => !string.IsNullOrEmpty(f)).Select(Normalize).ToList();

    public bool IsSpecific(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !WinPath.IsFullyQualified(folder.Trim())) return false;
        var f = Normalize(folder);

        if (WinPath.IsDriveRoot(f)) return false; // C:\
        if (_forbidden.Any(t => f.Equals(t, StringComparison.OrdinalIgnoreCase) || RunningApps.IsUnder(f, t))) return false;
        return !_broad.Any(b => f.Equals(b, StringComparison.OrdinalIgnoreCase));
    }

    public static FolderGuard FromEnvironment()
    {
        string Special(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder);
        var localAppData = Special(Environment.SpecialFolder.LocalApplicationData);
        return new FolderGuard(
            broadFolders:
            [
                Special(Environment.SpecialFolder.ProgramFiles),
                Special(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
                Special(Environment.SpecialFolder.CommonProgramFiles),
                Special(Environment.SpecialFolder.CommonProgramFilesX86),
                Special(Environment.SpecialFolder.CommonApplicationData),
                Special(Environment.SpecialFolder.UserProfile),
                Special(Environment.SpecialFolder.ApplicationData),
                localAppData,
                Path.Combine(localAppData, "Programs"),
                Special(Environment.SpecialFolder.Desktop),
                Special(Environment.SpecialFolder.MyDocuments),
                Path.GetTempPath()
            ],
            forbiddenTrees: [Special(Environment.SpecialFolder.Windows)]);
    }

    private static string Normalize(string path) => path.Trim().TrimEnd('\\');
}

/// <summary>Chemins des programmes ouverts de la session de l'utilisateur.</summary>
internal static class ProcessPaths
{
    private const uint QueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public static IReadOnlyList<OpenProcess> Snapshot()
    {
        var self = Environment.ProcessId;
        var session = Process.GetCurrentProcess().SessionId;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var list = new List<OpenProcess>();

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == self || process.SessionId != session) continue;
                    if (PathOf(process.Id) is not { } path) continue;
                    if (RunningApps.IsUnder(path, windows)) continue; // jamais un programme de Windows (Explorateur…)
                    list.Add(new OpenProcess(process.Id, WinPath.FileName(path), path));
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                {
                    // Programme protégé ou déjà fermé.
                }
            }
        }
        return list;
    }

    public static string? PathOf(int processId)
    {
        var handle = OpenProcess(QueryLimitedInformation, false, processId);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var size = 1024;
            var buffer = new StringBuilder(size);
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Applications du Microsoft Store / MSIX (ex. Terminal Windows) : dossier
    /// « WindowsApps\Microsoft.WindowsTerminal_1.24…__8wekyb3d8bbwe » dont le nom commence par l'identifiant winget exact.
    /// </summary>
    internal static bool IsInPackageFolder(string path, string packageId)
    {
        const string marker = @"\WindowsApps\";
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return false;
        var folder = path[(index + marker.Length)..];
        return folder.StartsWith(packageId + "_", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Chemins Windows (« C:\dossier\fichier.exe »), traités de la même façon quel que soit le système :
/// les fonctions de System.IO suivent les règles du système courant.
/// </summary>
internal static class WinPath
{
    public static bool IsFullyQualified(string path) =>
        (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\')
        || path.StartsWith(@"\\", StringComparison.Ordinal);

    public static bool IsDriveRoot(string path) =>
        path.TrimEnd('\\').Length == 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';

    public static string? DirectoryOf(string path)
    {
        var index = path.TrimEnd('\\').LastIndexOf('\\');
        return index > 2 ? path[..index] : null;
    }

    public static string FileName(string path)
    {
        var index = path.LastIndexOf('\\');
        return index >= 0 ? path[(index + 1)..] : path;
    }
}
