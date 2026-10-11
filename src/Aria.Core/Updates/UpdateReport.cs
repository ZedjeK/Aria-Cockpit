using Aria.Core.Reporting;

namespace Aria.Core.Updates;

/// <summary>Rapport texte d'une installation de mises à jour, dans C:\ProgramData\Aria\Rapports.</summary>
public static class UpdateReport
{
    public static string Save(InstallOutcome outcome, int notSelected, NvidiaDriverCheck? nvidia)
    {
        Directory.CreateDirectory(ReportFile.Folder);
        var now = DateTime.Now;
        var path = Path.Combine(ReportFile.Folder, $"mises-a-jour_{now:yyyy-MM-dd_HHmmss}.txt");
        using var writer = new StreamWriter(path);
        Write(writer, outcome, notSelected, nvidia, now);
        return path;
    }

    public static void Write(TextWriter w, InstallOutcome outcome, int notSelected, NvidiaDriverCheck? nvidia, DateTime at)
    {
        w.WriteLine($"Aria — mises à jour du {at:dd/MM/yyyy à HH:mm:ss} — {Environment.MachineName}");
        w.WriteLine();
        w.WriteLine($"Point de restauration : {outcome.RestorePointMessage}");
        if (outcome.ClosedApps is { Count: > 0 } closed)
        {
            w.WriteLine("Applications fermées avant leur mise à jour :");
            foreach (var line in closed) w.WriteLine($"  {line}");
        }

        foreach (var group in outcome.Results.GroupBy(r => r.Item.Source))
        {
            w.WriteLine();
            w.WriteLine(group.Key == UpdateSource.WindowsUpdate ? "== WINDOWS UPDATE ==" : "== APPLICATIONS ==");
            foreach (var r in group)
            {
                var status = r.Status switch
                {
                    InstallStatus.Installed => r.RebootRequired ? "[✓] installé (au redémarrage)" : "[✓] installé",
                    InstallStatus.UpToDate => "[=] déjà à jour",
                    InstallStatus.Canceled => "[-] annulé",
                    _ => "[X] échec"
                };
                var versions = r.Item.CurrentVersion is { } from ? $" : {from} → {r.Item.NewVersion}"
                    : r.Item.NewVersion is { } kb && !r.Item.Title.Contains(kb, StringComparison.OrdinalIgnoreCase) ? $" ({kb})"
                    : "";
                w.WriteLine($"  {status,-30} {r.Item.Title}{versions}");
                if (r.Error is not null) w.WriteLine($"  {"",-30} ↳ {r.Error}");
            }
        }

        if (nvidia is not null)
        {
            w.WriteLine();
            w.WriteLine("== PILOTE NVIDIA ==");
            w.WriteLine(nvidia.Error is not null
                ? $"  {nvidia.GpuName} : {nvidia.Error}"
                : $"  {nvidia.GpuName} : installé {nvidia.InstalledVersion}, dernier Game Ready {nvidia.LatestVersion}" +
                  (nvidia.UpdateAvailable ? " (mise à jour disponible)" : " (à jour)"));
        }

        int Count(InstallStatus s) => outcome.Results.Count(r => r.Status == s);
        w.WriteLine();
        w.WriteLine($"Bilan : {Count(InstallStatus.Installed)} installé(s) · {Count(InstallStatus.UpToDate)} déjà à jour · " +
                    $"{Count(InstallStatus.Failed)} échec(s) · {Count(InstallStatus.Canceled)} annulé(s) · {notSelected} non retenu(s)" +
                    (outcome.Results.Any(r => r.RebootRequired) ? " · redémarrage requis" : ""));
    }
}
