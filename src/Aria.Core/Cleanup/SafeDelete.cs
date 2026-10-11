namespace Aria.Core.Cleanup;

/// <summary>Bilan d'un parcours de dossier : taille et nombre de fichiers (supprimés ou supprimables).</summary>
public sealed record FolderTally(long Bytes, int Files, int Skipped)
{
    public static readonly FolderTally Empty = new(0, 0, 0);

    public FolderTally Add(FolderTally other) => new(Bytes + other.Bytes, Files + other.Files, Skipped + other.Skipped);
}

/// <summary>
/// Parcours et suppression de fichiers DANS un dossier autorisé, avec garde-fous :
/// jamais en dehors de la racine, jamais à travers un lien (jonction, lien symbolique), jamais la racine elle-même.
/// Un fichier en cours d'utilisation est simplement ignoré.
/// </summary>
public static class SafeDelete
{
    /// <param name="olderThan">Seuls les fichiers non modifiés depuis cette date sont pris en compte (null = tous).</param>
    public static FolderTally Measure(string root, DateTime? olderThan = null) => Walk(root, olderThan, delete: false);

    public static FolderTally Delete(string root, DateTime? olderThan = null) => Walk(root, olderThan, delete: true);

    /// <summary>Le chemin est-il strictement à l'intérieur de la racine ? (la racine elle-même : non)</summary>
    public static bool IsInside(string root, string path)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        return fullPath.Length > fullRoot.Length && fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Racine acceptable pour un nettoyage : au moins deux niveaux sous le disque, et jamais un dossier vital
    /// (Windows, profil utilisateur, Program Files, ProgramData…) lui-même.
    /// </summary>
    public static bool IsSafeRoot(string root)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }

        var drive = Path.GetPathRoot(full) ?? "";
        var depth = full[drive.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;
        if (depth < 2) return false;

        string[] vital =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.System)
        ];
        return !vital.Where(v => v.Length > 0)
            .Any(v => string.Equals(Path.TrimEndingDirectorySeparator(v), full, StringComparison.OrdinalIgnoreCase));
    }

    private static FolderTally Walk(string root, DateTime? olderThan, bool delete)
    {
        // Mesurer est sans risque (lecture seule, utile pour Windows.old) ; supprimer exige une racine sûre
        if ((delete && !IsSafeRoot(root)) || !Directory.Exists(root) || IsLink(root)) return FolderTally.Empty;

        long bytes = 0;
        int files = 0, skipped = 0;
        var pending = new Stack<string>();
        var visited = new List<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            visited.Add(dir);

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(dir).ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                skipped++;
                continue;
            }

            foreach (var entry in entries)
            {
                if (!IsInside(root, entry)) { skipped++; continue; } // garde-fou absolu

                FileSystemInfo info;
                try
                {
                    info = Directory.Exists(entry) ? new DirectoryInfo(entry) : new FileInfo(entry);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // lien : on ne le suit pas, on n'y touche pas
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    skipped++;
                    continue;
                }

                if (info is DirectoryInfo) { pending.Push(entry); continue; }

                var file = (FileInfo)info;
                if (olderThan is { } limit && file.LastWriteTime > limit) continue;

                long size;
                try { size = file.Length; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { skipped++; continue; }

                if (delete)
                {
                    try
                    {
                        if (file.IsReadOnly) file.IsReadOnly = false;
                        file.Delete();
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        skipped++; // en cours d'utilisation ou protégé
                        continue;
                    }
                }
                bytes += size;
                files++;
            }
        }

        // Dossiers vidés : supprimés du plus profond au moins profond, jamais la racine
        if (delete)
        {
            foreach (var dir in visited.Skip(1).OrderByDescending(d => d.Length))
            {
                try
                {
                    if (IsInside(root, dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Dossier encore utilisé : sans importance
                }
            }
        }

        return new FolderTally(bytes, files, skipped);
    }

    private static bool IsLink(string path)
    {
        try { return new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
    }
}
