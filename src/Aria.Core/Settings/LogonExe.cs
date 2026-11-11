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
    /// <remarks>LongRunning : la tâche lance un exemplaire qui reste ouvert (zone de notification), et non un passage silencieux.</remarks>
    private static readonly (string Name, string Argument, bool LongRunning)[] Tasks =
    [
        (StartupTask.Name, StartupTask.Argument, false),
        (ChargeLimits.TaskName, ChargeLimits.Argument, false),
        (Tray.TrayStartup.TaskName, Tray.TrayStartup.Argument, true)
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
    /// <param name="longRunning">
    /// L'exemplaire lancé reste ouvert (zone de notification). Une tâche créée en ligne de commande est arrêtée au bout
    /// de 3 jours et ne démarre pas sur batterie : celle-ci est décrite en XML, sans ces deux limites.
    /// </param>
    internal static void CreateTask(string name, string argument, string sourceExe, bool longRunning = false)
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

        int code;
        string output;
        if (longRunning)
        {
            using var user = System.Security.Principal.WindowsIdentity.GetCurrent();
            var file = Path.Combine(Path.GetTempPath(), $"aria-tache-{Guid.NewGuid():N}.xml");
            try
            {
                // UTF-16 : l'encodage que schtasks attend pour un fichier de tâche
                File.WriteAllText(file, LongRunningTaskXml(exe, argument, user.Name, user.User?.Value ?? user.Name), System.Text.Encoding.Unicode);
                (code, output) = StartupTask.Run($"/Create /TN \"{name}\" /XML \"{file}\" /F");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"Tâche de démarrage non créée : {e.Message}", e);
            }
            finally
            {
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        else
        {
            // 20 s après l'ouverture de session : le pilote graphique est prêt
            (code, output) = StartupTask.Run(
                $"/Create /TN \"{name}\" /TR \"\\\"{exe}\\\" {argument}\" /SC ONLOGON /RL HIGHEST /DELAY 0000:20 /F");
        }
        if (code != 0) throw new InvalidOperationException($"Tâche de démarrage refusée par Windows : {output.Trim()}");
    }

    /// <summary>
    /// Tâche d'ouverture de session pour un exemplaire qui reste ouvert : droits administrateur, 20 s après
    /// l'ouverture de session (pilote graphique prêt), sans durée maximale, lancée et gardée aussi sur batterie.
    /// </summary>
    /// <param name="account">Compte dont l'ouverture de session lance la tâche (« PC\nom »).</param>
    /// <param name="sid">Identifiant du même compte, sous lequel la tâche tourne.</param>
    internal static string LongRunningTaskXml(string exe, string argument, string account, string sid)
    {
        System.Xml.Linq.XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        System.Xml.Linq.XElement E(string name, params object[] content) => new(ns + name, content);
        var task = new System.Xml.Linq.XElement(ns + "Task", new System.Xml.Linq.XAttribute("version", "1.2"),
            E("RegistrationInfo",
                E("Description", "Lance Aria Cockpit dans la zone de notification à l'ouverture de session. Créée par Aria Cockpit (« Démarrer avec Windows »).")),
            E("Triggers",
                E("LogonTrigger", E("Enabled", "true"), E("UserId", account), E("Delay", "PT20S"))),
            E("Principals",
                E("Principal", new System.Xml.Linq.XAttribute("id", "Author"),
                    E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings",
                E("MultipleInstancesPolicy", "IgnoreNew"),
                E("DisallowStartIfOnBatteries", "false"),
                E("StopIfGoingOnBatteries", "false"),
                E("AllowHardTerminate", "true"),
                E("StartWhenAvailable", "false"),
                E("RunOnlyIfNetworkAvailable", "false"),
                E("AllowStartOnDemand", "true"),
                E("Enabled", "true"),
                E("Hidden", "false"),
                E("RunOnlyIfIdle", "false"),
                E("WakeToRun", "false"),
                E("ExecutionTimeLimit", "PT0S"),
                E("Priority", "7")),
            E("Actions", new System.Xml.Linq.XAttribute("Context", "Author"),
                E("Exec", E("Command", exe), E("Arguments", argument))));
        return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" + Environment.NewLine + task;
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
        foreach (var (name, argument, longRunning) in existing)
        {
            if (string.Equals(StartupTask.CommandOf(name), exe, StringComparison.OrdinalIgnoreCase)) continue;
            CreateTask(name, argument, currentExe, longRunning);
            moved.Add(name);
        }
        return moved.Count == 0 ? null
            : $"Tâches de démarrage repointées vers la copie protégée « {exe} » : {string.Join(", ", moved)}.";
    }
}
