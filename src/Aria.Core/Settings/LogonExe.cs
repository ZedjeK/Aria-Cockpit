using Aria.Core.Battery;
using Aria.Core.Nvidia;

namespace Aria.Core.Settings;

/// <summary>
/// Exe lancé par les tâches de démarrage (profil GPU, limite de charge). Ces tâches tournent avec les droits
/// administrateur, sans fenêtre UAC : si elles lançaient l'exe là où l'utilisateur l'a dézippé (Téléchargements,
/// Bureau…), n'importe quel programme lancé sans droits pourrait le remplacer et obtenir les droits administrateur à
/// l'ouverture de session suivante. Elles lancent donc une copie rangée dans Program Files, où seuls les
/// administrateurs écrivent (droits hérités de Program Files).
/// </summary>
public static class LogonExe
{
    /// <summary>Dossier de la copie : C:\Program Files\Aria Cockpit.</summary>
    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Aria Cockpit");

    /// <summary>Chemin de la copie d'un exe d'Aria (même nom de fichier, dans <see cref="Folder"/>).</summary>
    public static string PathFor(string sourceExe) => Path.Combine(Folder, Path.GetFileName(sourceExe));

    /// <summary>Tâches de démarrage d'Aria qui lancent la copie, avec leur argument.</summary>
    private static readonly (string Name, string Argument)[] Tasks =
    [
        (StartupTask.Name, StartupTask.Argument),
        (ChargeLimits.TaskName, ChargeLimits.Argument)
    ];

    /// <summary>
    /// Copie l'exe dans Program Files s'il n'y est pas déjà à jour, et renvoie le chemin à donner aux tâches.
    /// Lève <see cref="IOException"/> ou <see cref="UnauthorizedAccessException"/> si la copie échoue.
    /// </summary>
    public static string Install(string sourceExe) => Install(sourceExe, Folder);

    internal static string Install(string sourceExe, string folder)
    {
        var source = Path.GetFullPath(sourceExe);
        var target = Path.GetFullPath(Path.Combine(folder, Path.GetFileName(source)));
        // Aria lancé depuis la copie elle-même : rien à copier
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) return target;
        if (!File.Exists(source)) throw new FileNotFoundException("Exe d'Aria introuvable.", source);

        Directory.CreateDirectory(folder);
        if (!IsSameFile(source, target))
        {
            // Copie à côté puis remplacement : la tâche ne trouve jamais un exe à moitié écrit
            var temp = target + ".nouveau";
            File.Copy(source, temp, overwrite: true);
            File.Move(temp, target, overwrite: true);
        }
        return target;
    }

    /// <summary>Même taille et même date de modification (File.Copy garde la date) : la copie est à jour.</summary>
    internal static bool IsSameFile(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        return fa.Exists && fb.Exists && fa.Length == fb.Length && fa.LastWriteTimeUtc == fb.LastWriteTimeUtc;
    }

    /// <summary>Crée (ou remplace) une tâche d'ouverture de session qui lance la copie de l'exe avec les droits administrateur.</summary>
    internal static void CreateTask(string name, string argument, string sourceExe)
    {
        string exe;
        try
        {
            exe = Install(sourceExe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Copie d'Aria dans « {Folder} » impossible : {e.Message}", e);
        }
        // 20 s après l'ouverture de session : le pilote graphique est prêt
        var (code, output) = StartupTask.Run(
            $"/Create /TN \"{name}\" /TR \"\\\"{exe}\\\" {argument}\" /SC ONLOGON /RL HIGHEST /DELAY 0000:20 /F");
        if (code != 0) throw new InvalidOperationException($"Tâche de démarrage refusée par Windows : {output.Trim()}");
    }

    /// <summary>Supprime la copie quand plus aucune tâche d'Aria ne la lance.</summary>
    public static void RemoveIfUnused()
    {
        if (!OperatingSystem.IsWindows() || Tasks.Any(t => StartupTask.TaskExists(t.Name))) return;
        RemoveCopies(Folder);
    }

    internal static void RemoveCopies(string folder)
    {
        if (!Directory.Exists(folder)) return;
        try
        {
            // Uniquement ce qu'Aria y a mis : ses exe et une éventuelle copie interrompue
            foreach (var file in Directory.EnumerateFiles(folder, "*.exe").Concat(Directory.EnumerateFiles(folder, "*.nouveau")))
                File.Delete(file);
            if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Copie en cours d'exécution : elle sera supprimée au prochain lancement d'Aria
        }
    }

    /// <summary>
    /// Au lancement d'Aria : remet la copie à jour (nouvelle version d'Aria) et repointe vers elle les tâches qui
    /// lancent encore un autre exe (versions d'avant la 0.24, qui lançaient l'exe dézippé). Supprime la copie si plus
    /// aucune tâche ne l'utilise. Renvoie ce qui a été corrigé, pour le journal, ou null.
    /// </summary>
    public static string? Refresh(string currentExe)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var existing = Tasks.Where(t => StartupTask.TaskExists(t.Name)).ToList();
        if (existing.Count == 0)
        {
            RemoveCopies(Folder);
            return null;
        }

        var exe = Install(currentExe);
        var moved = new List<string>();
        foreach (var (name, argument) in existing)
        {
            if (string.Equals(StartupTask.CommandOf(name), exe, StringComparison.OrdinalIgnoreCase)) continue;
            CreateTask(name, argument, currentExe);
            moved.Add(name);
        }
        return moved.Count == 0 ? null
            : $"Tâches de démarrage repointées vers la copie protégée « {exe} » : {string.Join(", ", moved)}.";
    }
}
