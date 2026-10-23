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
/// Sauvegardes JSON des valeurs d'origine, dans C:\ProgramData\AriaCockpit\Sauvegardes.
/// Une sauvegarde est créée avant chaque série de modifications.
/// </summary>
public static class BackupStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Dossier des données (C:\ProgramData\AriaCockpit), préparé au premier accès.</summary>
    public static string DataRoot { get; } = PrepareDataRoot();

    /// <summary>
    /// Le logiciel s'est appelé PC-Tune (jusqu'à la 0.12, C:\ProgramData\PCTune), puis Aria (jusqu'à la 0.14,
    /// C:\ProgramData\Aria). L'ancien dossier est renommé une seule fois, pour garder sauvegardes, rapports et réglage GPU.
    /// S'il ne peut pas l'être (fichier ouvert ailleurs), on continue de l'utiliser : la page Restauration retrouve toujours les sauvegardes.
    /// </summary>
    private static string PrepareDataRoot()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return PrepareDataRoot(Path.Combine(common, "AriaCockpit"), Path.Combine(common, "Aria"), Path.Combine(common, "PCTune"));
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
