using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Text.Json;
using Aria.Core.Backup;
using Aria.Core.Models;

namespace Aria.Core.Nvidia;

/// <summary>Réglage GPU posé par Aria : la valeur d'origine (avant Aria) et celle en place.</summary>
public sealed record GpuProfileFile(string Origin, string Current, DateTime AppliedAt);

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

    public static GpuProfileFile? Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<GpuProfileFile>(File.ReadAllText(FilePath), Json) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Valeur d'origine, avant le premier réglage d'Aria ; null si Aria n'a rien réglé.</summary>
    public static GpuTuningState? Origin => Load() is { } f && GpuTuningState.TryParse(f.Origin, out var o) ? o : null;

    /// <summary>État actuel de la carte (le plafond de fréquence, illisible par NVML, vient du profil d'Aria).</summary>
    public static GpuTuningState ReadCurrent(GpuTuner tuner)
    {
        var known = Load() is { } f && GpuTuningState.TryParse(f.Current, out var current) ? current.MaxClockMHz : 0u;
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
            var before = ReadCurrent(tuner);
            var origin = saved is not null && GpuTuningState.TryParse(saved.Origin, out var o) ? o : before with { AtStartup = false };

            if (saved is null && saveBackup)
                BackupStore.Save("Avant réglage GPU",
                    [new BackupEntry(SettingId, SettingName, SettingCategory.Nvidia, before.ToRaw(), before.Describe())]);

            try
            {
                tuner.Apply(target);
            }
            catch (GpuTuningException)
            {
                try { tuner.Apply(before); } catch (GpuTuningException) { /* le redémarrage remettra l'origine */ }
                throw;
            }

            if (target.AtStartup) StartupTask.Create(exePath);
            else StartupTask.Delete();

            if (target.SameTuning(origin) && !target.AtStartup)
                File.Delete(FilePath);
            else
            {
                Directory.CreateDirectory(BackupStore.DataRoot);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(new GpuProfileFile(origin.ToRaw(), target.ToRaw(), DateTime.Now), Json));
            }
        }
    }

    /// <summary>Remet la carte comme avant Aria et supprime la tâche de démarrage.</summary>
    public static void RevertToOrigin(GpuTuner tuner, string exePath)
    {
        if (Origin is { } origin) Apply(tuner, origin with { AtStartup = false }, exePath, saveBackup: false);
        else StartupTask.Delete();
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
    /// Plantages du pilote graphique notés par Windows depuis une date (journal Système) :
    /// « Le pilote d'affichage nvlddmkm a cessé de répondre » (Display, 4101) et les erreurs de nvlddmkm.
    /// </summary>
    public static IReadOnlyList<DateTime> DriverCrashesSince(DateTime since)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var ms = (long)Math.Max(0, (DateTime.Now - since).TotalMilliseconds);
        var query = new EventLogQuery("System", PathType.LogName,
            "*[System[((Provider[@Name='Display'] and EventID=4101) or (Provider[@Name='nvlddmkm'] and (Level=1 or Level=2)))" +
            $" and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
        var found = new List<DateTime>();
        try
        {
            using var reader = new EventLogReader(query);
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
                using (e)
                    if (e.TimeCreated is { } at) found.Add(at);
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException)
        {
            // Journal illisible : pas de garde-fou, mais pas d'alerte inventée
        }
        return found.Order().ToList();
    }

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
/// Tâche planifiée « Aria\Profil GPU » : lance Aria en mode silencieux à l'ouverture de session,
/// avec les droits administrateur (sans fenêtre UAC), pour réappliquer le réglage GPU.
/// </summary>
public static class StartupTask
{
    public const string Name = @"Aria\Profil GPU";
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
    }

    public static void Delete()
    {
        if (Exists()) Run($"/Delete /TN \"{Name}\" /F");
    }

    /// <summary>Exe déplacé depuis la création de la tâche : la tâche est recréée vers le nouvel emplacement.</summary>
    public static void Repair(string exePath)
    {
        if (Command() is { } command && !string.Equals(command, exePath, StringComparison.OrdinalIgnoreCase)) Create(exePath);
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
