using System.Text.Json;
using System.Text.Json.Serialization;
using Aria.Core.Models;

namespace Aria.Core.Backup;

public sealed record BackupEntry(string Id, string DisplayName, SettingCategory Category, string Raw, string Display)
{
    public SettingValue Value => new(Raw, Display);
}

public sealed record BackupFile(DateTime CreatedAt, string Computer, string Reason, List<BackupEntry> Entries);

/// <summary>
/// Sauvegardes JSON des valeurs d'origine, dans %USERPROFILE%\.Aria-Cockpit\Sauvegardes.
/// Une sauvegarde est créée avant chaque série de modifications.
/// </summary>
public static class BackupStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Nom du dossier des données, dans le profil de l'utilisateur.</summary>
    public const string FolderName = ".Aria-Cockpit";

    /// <summary>Dossier des données (%USERPROFILE%\.Aria-Cockpit), préparé au premier accès.</summary>
    public static string DataRoot { get; } = PrepareDataRoot();

    /// <summary>
    /// Depuis la 0.22, les données sont dans le profil de l'utilisateur (C:\Users\nom\.Aria-Cockpit), plus faciles à trouver.
    /// Avant, elles étaient dans C:\ProgramData : AriaCockpit (0.15 à 0.21), Aria (0.13 et 0.14), PCTune (0.12 et avant).
    /// L'ancien dossier est déplacé une seule fois, pour garder sauvegardes, rapports et réglage GPU. S'il ne peut pas l'être
    /// (fichier ouvert ailleurs, profil sur un autre disque), on continue de l'utiliser : la page Restauration retrouve
    /// toujours les sauvegardes. Le dossier neuf (ou déplacé) n'est modifiable que par les administrateurs.
    /// </summary>
    private static string PrepareDataRoot()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FolderName);
        var existed = Directory.Exists(home);
        var root = PrepareDataRoot(home, Path.Combine(common, "AriaCockpit"), Path.Combine(common, "Aria"), Path.Combine(common, "PCTune"));
        if (!existed && root == home) DataFolderSecurity.Protect(home);
        return root;
    }

    /// <summary>Dossier à utiliser : le nouveau, éventuellement obtenu en renommant le plus récent des anciens.</summary>
    internal static string PrepareDataRoot(string root, params string[] legacies)
    {
        if (Directory.Exists(root)) return root;
        foreach (var legacy in legacies.Where(Directory.Exists))
        {
            try
            {
                Directory.Move(legacy, root);
                return root;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return legacy;
            }
        }
        return root;
    }

    public static string Folder => Path.Combine(DataRoot, "Sauvegardes");

    public static string Save(string reason, IEnumerable<BackupEntry> entries)
    {
        Directory.CreateDirectory(Folder);
        var now = DateTime.Now;
        var file = new BackupFile(now, Environment.MachineName, reason, entries.ToList());
        var path = Path.Combine(Folder, $"sauvegarde_{now:yyyy-MM-dd_HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(file, Json));
        return path;
    }

    public static BackupFile Load(string path) =>
        JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(path), Json)
        ?? throw new InvalidDataException($"Sauvegarde illisible : {path}");

    /// <summary>Sauvegardes disponibles, la plus récente en premier.</summary>
    public static IReadOnlyList<string> List() =>
        Directory.Exists(Folder)
            ? Directory.GetFiles(Folder, "sauvegarde_*.json").OrderDescending().ToList()
            : [];
}
