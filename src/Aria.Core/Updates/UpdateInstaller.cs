using Aria.Core.Backup;

namespace Aria.Core.Updates;

/// <summary>
/// Installation des mises à jour cochées : point de restauration Windows, fermeture des applications ouvertes
/// (si l'utilisateur l'a accepté), puis applications (rapides) et enfin Windows Update (plus long).
/// L'arrêt demandé prend effet entre deux éléments.
/// </summary>
public static class UpdateInstaller
{
    public static InstallOutcome Install(
        IReadOnlyList<UpdateItem> items,
        WindowsUpdateService windowsUpdate,
        WingetService winget,
        IReadOnlyList<RunningApp>? appsToClose = null,
        IProgress<InstallStep>? progress = null,
        CancellationToken stop = default)
    {
        var ordered = items.OrderBy(i => i.Source == UpdateSource.Application ? 0 : 1).ToList();
        var total = ordered.Count + 1;

        // Une mise à jour ne se désinstalle pas depuis Aria : le point de restauration est le filet de sécurité
        progress?.Report(new InstallStep(0, total, "Création d'un point de restauration Windows…"));
        var (created, message) = RestorePoint.TryCreate("Aria - Avant mises à jour");

        // Fermeture juste avant les installations, pour que les applications restent ouvertes le moins longtemps possible
        IReadOnlyList<string> closed = [];
        if (appsToClose is { Count: > 0 })
        {
            progress?.Report(new InstallStep(0, total, "Fermeture des applications ouvertes…",
                string.Join(", ", appsToClose.Select(a => a.Item.Title))));
            closed = RunningApps.Close(appsToClose, TimeSpan.FromSeconds(10));
        }

        var results = new List<InstallResult>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var item = ordered[i];
            if (stop.IsCancellationRequested)
            {
                results.Add(new InstallResult(item, InstallStatus.Canceled, null, false));
                continue;
            }

            var step = 1 + i;
            if (item.Source == UpdateSource.WindowsUpdate)
            {
                progress?.Report(new InstallStep(step, total, item.Title, "Téléchargement puis installation par Windows Update…"));
                results.Add(windowsUpdate.Install(item));
            }
            else
            {
                progress?.Report(new InstallStep(step, total, item.Title, "Préparation…"));
                results.Add(winget.Install(item, p => progress?.Report(new InstallStep(step, total, item.Title, p.Text, p.Fraction))));
            }
        }

        progress?.Report(new InstallStep(total, total, "Terminé"));
        return new InstallOutcome(results, created, message, closed);
    }
}
