using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using Aria.Core.Backup;
using Aria.Core.Models;

namespace Aria.Core.Nvidia;

/// <summary>Réglage GPU posé par Aria : la valeur d'origine (valeurs d'usine de la carte) et celle en place.</summary>
/// <param name="GpuUuid">Carte pour laquelle le réglage a été posé (identifiant NVML) ; vide pour les profils d'avant la 0.17.</param>
public sealed record GpuProfileFile(string Origin, string Current, DateTime AppliedAt, string GpuUuid = "", string GpuName = "")
{
    /// <summary>Le réglage a été posé sur une autre carte que celle-ci (carte changée depuis).</summary>
    public bool IsForOtherGpu(GpuIdentity gpu) =>
        GpuUuid.Length > 0 && gpu.Uuid.Length > 0 && !string.Equals(GpuUuid, gpu.Uuid, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Réglage GPU d'Aria, de bout en bout : sauvegarde de l'origine, application, tâche de démarrage, garde-fou.
/// Le fichier gpu-profile.json n'existe que tant qu'un réglage d'Aria est en place.
/// </summary>
public static class GpuProfile
{
    public const string SettingId = "nvidia.gputuning";
    public const string SettingName = "Réglage de la carte graphique";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Lazy<GpuTuner?> SharedTuner = new(GpuTuner.TryOpen);
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(BackupStore.DataRoot, "gpu-profile.json");
    private static string LogPath => Path.Combine(BackupStore.DataRoot, "gpu-profile.log");

    /// <summary>Carte réglable (NVIDIA, NVML disponible) ; null sinon.</summary>
    public static GpuTuner? Tuner => SharedTuner.Value;

    /// <summary>Profil en place ; null s'il n'y en a pas, ou si un redémarrage a effacé le réglage (le fichier est alors supprimé).</summary>
    public static GpuProfileFile? Load()
    {
        GpuProfileFile? file;
        try
        {
            file = File.Exists(FilePath) ? JsonSerializer.Deserialize<GpuProfileFile>(File.ReadAllText(FilePath), Json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
        if (file is null || !WipedByRestart(file, BootTime)) return file;
        // Réglage « Essayer » effacé par le redémarrage : le garder ferait afficher un plafond qui n'existe plus
        try { File.Delete(FilePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        Log($"Redémarrage depuis le réglage du {file.AppliedAt:yyyy-MM-dd HH:mm:ss} (non gardé au démarrage) : profil oublié.");
        return null;
    }

    /// <summary>Démarrage de Windows (le temps écoulé depuis inclut les mises en veille).</summary>
    private static DateTime BootTime => DateTime.Now - TimeSpan.FromMilliseconds(Environment.TickCount64);

    /// <summary>Un réglage non gardé au démarrage, posé avant le dernier démarrage de Windows, n'est plus en place.</summary>
    internal static bool WipedByRestart(GpuProfileFile file, DateTime bootTime) =>
        GpuTuningState.TryParse(file.Current, out var current) && !current.AtStartup && file.AppliedAt < bootTime;

    /// <summary>
    /// Valeurs d'usine de la carte : limite de puissance par défaut du pilote, sans plafond ni décalage.
    /// L'état trouvé au premier réglage peut venir d'un autre outil (NVIDIA App, Afterburner) : il ne sert pas d'origine.
    /// </summary>
    /// <param name="fallbackPowerMilliwatts">Limite gardée si le pilote ne donne pas sa valeur par défaut.</param>
    public static GpuTuningState Factory(GpuTuningLimits limits, uint fallbackPowerMilliwatts) =>
        new(limits.DefaultPowerMilliwatts > 0 ? limits.DefaultPowerMilliwatts : fallbackPowerMilliwatts, 0, 0, 0, false);

    /// <summary>Valeur d'origine de cette carte (valeurs d'usine) ; null si Aria n'a rien réglé, ou si le réglage enregistré concerne une autre carte.</summary>
    public static GpuTuningState? OriginFor(GpuTuner tuner) =>
        Load() is { } f && !f.IsForOtherGpu(tuner.Identity) && GpuTuningState.TryParse(f.Origin, out var o)
            ? Factory(tuner.Limits, o.PowerLimitMilliwatts) : null;

    /// <summary>État actuel de la carte (le plafond de fréquence, illisible par NVML, vient du profil d'Aria).</summary>
    public static GpuTuningState ReadCurrent(GpuTuner tuner)
    {
        var known = Load() is { } f && !f.IsForOtherGpu(tuner.Identity) && GpuTuningState.TryParse(f.Current, out var current)
            ? current.MaxClockMHz : 0u;
        return tuner.Read(known, StartupTask.Exists());
    }

    /// <summary>
    /// Applique un réglage complet. Au premier réglage, la valeur d'origine est sauvegardée (page Restauration)
    /// et notée dans le profil. En cas de refus du pilote, l'état précédent est remis avant de signaler l'erreur.
    /// </summary>
    /// <param name="saveBackup">false quand l'appel vient d'une restauration (qui a déjà sa propre sauvegarde).</param>
    public static void Apply(GpuTuner tuner, GpuTuningState target, string exePath, bool saveBackup = true)
    {
        lock (Gate)
        {
            var saved = Load();
            if (saved?.IsForOtherGpu(tuner.Identity) == true)
            {
                // Réglage d'une ancienne carte : son origine ne vaut rien pour celle-ci, on repart de zéro
                Forget();
                saved = null;
            }
            var before = ReadCurrent(tuner);
            var origin = Factory(tuner.Limits, before.PowerLimitMilliwatts);

            if (saved is null && saveBackup)
                BackupStore.Save("Avant réglage GPU",
                    [new BackupEntry(SettingId, SettingName, SettingCategory.Nvidia, before.ToRaw(), before.Describe())]);

            try
            {
                tuner.Apply(target);
            }
            catch (GpuTuningException e)
            {
                Log($"Refusé par le pilote : {target.Describe()} ({e.Message}) ; retour à {before.Describe()}");
                try { tuner.Apply(before); } catch (GpuTuningException) { /* le redémarrage remettra l'origine */ }
                throw;
            }
            // Trace de chaque réglage : un retour d'origine ou un abandon qui n'a pas eu lieu se voit dans le journal
            Log($"Appliqué : {target.Describe()} (avant : {before.Describe()})");

            if (target.AtStartup) StartupTask.Create(exePath);
            else StartupTask.Delete();

            if (target.SameTuning(origin) && !target.AtStartup)
                File.Delete(FilePath);
            else
            {
                Directory.CreateDirectory(BackupStore.DataRoot);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(
                    new GpuProfileFile(origin.ToRaw(), target.ToRaw(), DateTime.Now, tuner.Identity.Uuid, tuner.Identity.Name), Json));
            }
        }
    }

    /// <summary>Remet la carte à ses valeurs d'usine et supprime la tâche de démarrage.</summary>
    public static void RevertToOrigin(GpuTuner tuner, string exePath)
    {
        if (Load()?.IsForOtherGpu(tuner.Identity) == true) Forget();
        else if (OriginFor(tuner) is { } origin) Apply(tuner, origin, exePath, saveBackup: false);
        else StartupTask.Delete();
    }

    /// <summary>
    /// Oublie le réglage enregistré (carte changée) : supprime le profil et la tâche de démarrage,
    /// sans rien appliquer à la carte actuelle.
    /// </summary>
    public static void Forget()
    {
        lock (Gate)
        {
            StartupTask.Delete();
            try { File.Delete(FilePath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Log("Réglage enregistré oublié (carte changée) : rien n'a été appliqué.");
        }
    }

    /// <summary>
    /// Mode silencieux lancé par la tâche planifiée à l'ouverture de session : réapplique le réglage gardé.
    /// Le pilote peut être encore en cours de chargement : on réessaie pendant une minute.
    /// </summary>
    public static void ApplyAtLogon()
    {
        try
        {
            if (Load() is not { } saved || !GpuTuningState.TryParse(saved.Current, out var current) || !current.AtStartup)
            {
                Log("Aucun réglage à réappliquer.");
                return;
            }
            for (var attempt = 0; attempt < 12; attempt++)
            {
                if (GpuTuner.TryOpen() is { } tuner)
                {
                    if (saved.IsForOtherGpu(tuner.Identity))
                    {
                        Log($"Carte changée ({tuner.Identity.Name}) : réglage de « {saved.GpuName} » non réappliqué. Ouvrez l'onglet Réglage GPU.");
                        return;
                    }
                    tuner.Apply(current);
                    Log($"Réappliqué : {current.Describe()}");
                    return;
                }
                Thread.Sleep(5000);
            }
            Log("Pilote NVIDIA inaccessible : réglage non réappliqué.");
        }
        catch (Exception e)
        {
            Log($"Échec : {e.Message}");
        }
    }

    /// <summary>
    /// Plantages du pilote graphique depuis une date, un par incident (les lignes écrites ensemble par le pilote
    /// ne comptent qu'une fois) : voir <see cref="DriverIncidents"/>.
    /// </summary>
    public static IReadOnlyList<DateTime> DriverCrashesSince(DateTime since) =>
        DriverIncidents.Since(since).Select(i => i.At).ToList();

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(BackupStore.DataRoot);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>
/// Tâche planifiée « Aria Cockpit\Profil GPU » : lance Aria Cockpit en mode silencieux à l'ouverture de session,
/// avec les droits administrateur (sans fenêtre UAC), pour réappliquer le réglage GPU.
/// </summary>
public static class StartupTask
{
    public const string Name = @"Aria Cockpit\Profil GPU";
    /// <summary>Nom utilisé par les versions de test d'avant le renommage en Aria Cockpit : supprimé dès qu'on touche à la tâche.</summary>
    public const string LegacyName = @"Aria\Profil GPU";
    public const string Argument = "--gpu-profile";

    public static bool Exists() => OperatingSystem.IsWindows() && Run($"/Query /TN \"{Name}\"").Code == 0;

    /// <summary>Commande lancée par la tâche (pour vérifier qu'elle pointe toujours vers cet exe).</summary>
    public static string? Command()
    {
        var (code, output) = Run($"/Query /TN \"{Name}\" /XML");
        if (code != 0) return null;
        var start = output.IndexOf("<Command>", StringComparison.Ordinal);
        var end = output.IndexOf("</Command>", StringComparison.Ordinal);
        return start >= 0 && end > start ? output[(start + 9)..end].Trim().Trim('"') : null;
    }

    public static void Create(string exePath)
    {
        // 20 s après l'ouverture de session : le pilote graphique est prêt
        var (code, output) = Run($"/Create /TN \"{Name}\" /TR \"\\\"{exePath}\\\" {Argument}\" /SC ONLOGON /RL HIGHEST /DELAY 0000:20 /F");
        if (code != 0) throw new GpuTuningException($"Tâche de démarrage refusée par Windows : {output.Trim()}");
        DeleteLegacy();
    }

    public static void Delete()
    {
        if (Exists()) Run($"/Delete /TN \"{Name}\" /F");
        DeleteLegacy();
    }

    private static void DeleteLegacy()
    {
        if (OperatingSystem.IsWindows() && Run($"/Query /TN \"{LegacyName}\"").Code == 0) Run($"/Delete /TN \"{LegacyName}\" /F");
    }

    private static (int Code, string Output) Run(string arguments)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (p is null) return (-1, "");
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15_000))
            {
                p.Kill();
                return (-1, "délai dépassé");
            }
            return (p.ExitCode, output.Result + error.Result);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (-1, e.Message);
        }
    }
}
