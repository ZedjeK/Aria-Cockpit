namespace Aria.Core.Backup;

/// <summary>
/// Dossier « cache » du dossier des données : les fichiers de travail qu'Aria écrivait dans le dossier temporaire de
/// Windows (sorties du banc d'essai, copie de la base de NVIDIA App, fichier de tâche). Ils restent ainsi dans le
/// dossier d'Aria, que seuls les administrateurs peuvent modifier. Le ménage quotidien en retire ce qui a plus d'une
/// semaine : Windows ne le fera pas à la place d'Aria.
/// </summary>
public static class DataCache
{
    public const string FolderName = "cache";

    /// <summary>Âge au-delà duquel un fichier du cache est retiré par le ménage.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public static string Folder => Path.Combine(BackupStore.DataRoot, FolderName);

    /// <summary>Chemin d'un fichier du cache ; le dossier est créé au besoin.</summary>
    public static string PathFor(string name) => PathFor(Folder, name);

    internal static string PathFor(string folder, string name)
    {
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Dossier impossible à créer : l'écriture du fichier échouera, et celui qui écrit sait déjà le dire
        }
        return Path.Combine(folder, name);
    }

    /// <summary>Retire du cache les fichiers plus vieux que <paramref name="maxAge"/>. Renvoie leur nombre ; ne lève jamais.</summary>
    internal static int Sweep(string folder, TimeSpan maxAge, DateTime now)
    {
        var removed = 0;
        try
        {
            if (!Directory.Exists(folder)) return 0;
            foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
            {
                if (now - file.LastWriteTime <= maxAge) continue;
                try
                {
                    file.Delete();
                    removed++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Fichier ouvert ailleurs : il partira au prochain ménage
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return removed;
    }
}
