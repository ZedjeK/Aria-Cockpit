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
/// Sauvegardes JSON des valeurs d'origine, dans C:\ProgramData\Aria\Sauvegardes.
/// Une sauvegarde est créée avant chaque série de modifications.
/// </summary>
public static class BackupStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Dossier des données (C:\ProgramData\Aria), préparé au premier accès.</summary>
    public static string DataRoot { get; } = PrepareDataRoot();

    /// <summary>
    /// Jusqu'à la version 0.12, le logiciel s'appelait PC-Tune et rangeait ses données dans C:\ProgramData\PCTune.
    /// Ce dossier est renommé une seule fois, pour garder les sauvegardes et les rapports. S'il ne peut pas l'être
    /// (fichier ouvert ailleurs), on continue de l'utiliser : la page Restauration retrouve toujours les sauvegardes.
    /// </summary>
    private static string PrepareDataRoot()
    {
        var common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var root = Path.Combine(common, "Aria");
        var legacy = Path.Combine(common, "PCTune");
        if (!Directory.Exists(legacy) || Directory.Exists(root)) return root;
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
