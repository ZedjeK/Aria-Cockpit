using Aria.Core.Reporting;

namespace Aria.Core.Cleanup;

/// <summary>Rapport texte d'un nettoyage, dans %USERPROFILE%\.Aria-Cockpit\Rapports.</summary>
public static class CleanupReport
{
    public static string Save(IReadOnlyList<CleanupResult> results, DateTime at)
    {
        Directory.CreateDirectory(ReportFile.Folder);
        var path = Path.Combine(ReportFile.Folder, $"{ReportIndex.CleanupPrefix}{at:yyyy-MM-dd_HHmmss}.txt");
        using var writer = new StreamWriter(path);
        Write(writer, results, at);
        return path;
    }

    public static void Write(TextWriter w, IReadOnlyList<CleanupResult> results, DateTime at)
    {
        w.WriteLine($"Aria Cockpit — nettoyage du {at:dd/MM/yyyy à HH:mm:ss} — {Environment.MachineName}");
        w.WriteLine();
        w.WriteLine("== NETTOYAGE ==");
        foreach (var r in results)
        {
            var status = r.Status switch
            {
                CleanupStatus.Done => "[✓] nettoyé",
                CleanupStatus.Partial => "[~] en partie",
                CleanupStatus.Declined => "[ ] non retenu",
                _ => "[X] échec"
            };
            var detail = r.Status == CleanupStatus.Declined ? "" : $" : {Size(r.Freed)} libérés" +
                         (r.Files > 0 ? $", {r.Files} fichier(s)" : "") + (r.Skipped > 0 ? $", {r.Skipped} ignoré(s) (en cours d'utilisation)" : "");
            w.WriteLine($"  {status,-18} {r.Item.Title}{detail}");
            if (r.Error is not null) w.WriteLine($"  {"",-18} ↳ {r.Error}");
        }

        var done = results.Where(r => r.Status != CleanupStatus.Declined).ToList();
        w.WriteLine();
        w.WriteLine($"Bilan : {Size(done.Sum(r => r.Freed))} libérés · {done.Count(r => r.Status is CleanupStatus.Done or CleanupStatus.Partial)} élément(s) nettoyé(s) · " +
                    $"{done.Count(r => r.Status == CleanupStatus.Failed)} échec(s)");
    }

    /// <summary>Taille lisible : « 1,2 Go », « 350 Mo », « 12 Ko ».</summary>
    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} Go",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} Mo",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} Ko",
        _ => $"{bytes} o"
    };
}
