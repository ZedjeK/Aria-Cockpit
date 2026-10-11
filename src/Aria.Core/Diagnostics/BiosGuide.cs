using System.Reflection;
using System.Text.Json;
using Aria.Core.Models;

namespace Aria.Core.Diagnostics;

public sealed class BiosOption
{
    public string Path { get; set; } = "";
    public string Value { get; set; } = "";
    public List<string> Alternates { get; set; } = [];
}

public sealed class BiosBrandGuide
{
    public string Brand { get; set; } = "";
    public List<string> Match { get; set; } = [];
    public string SetupKey { get; set; } = "";
    public string? AdvancedModeKey { get; set; }
    public string UpdateTool { get; set; } = "";
    public Dictionary<string, BiosOption> Options { get; set; } = [];
}

/// <summary>
/// Indique où trouver chaque option dans le BIOS selon la marque de la carte mère.
/// Les données viennent de config/bios/*.json (intégrés à l'exe). Un dossier "bios" placé
/// à côté d'Aria.exe remplace les fichiers du même nom, sans recompiler.
/// </summary>
public sealed class BiosGuide
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public BiosBrandGuide Brand { get; }
    public bool IsGeneric => Brand.Match.Count == 0;

    private readonly BiosBrandGuide _generic;

    private BiosGuide(BiosBrandGuide brand, BiosBrandGuide generic)
    {
        Brand = brand;
        _generic = generic;
    }

    public static BiosGuide Load(BoardInfo board, List<string> warnings)
    {
        var guides = LoadAll(warnings);
        var generic = guides.FirstOrDefault(g => g.Match.Count == 0) ?? new BiosBrandGuide { Brand = "Générique" };

        var brand = guides.FirstOrDefault(g => g.Match.Any(m =>
            board.Manufacturer.Contains(m, StringComparison.OrdinalIgnoreCase))) ?? generic;

        return new BiosGuide(brand, generic);
    }

    /// <summary>Lignes à afficher pour une option : chemin, valeur, autres noms possibles.</summary>
    public IReadOnlyList<string> Steps(string optionId)
    {
        if (!Brand.Options.TryGetValue(optionId, out var option) && !_generic.Options.TryGetValue(optionId, out option))
            return [];

        var steps = new List<string> { $"{option.Path} → {option.Value}" };
        if (option.Alternates.Count > 0)
            steps.Add($"Autres noms possibles : {string.Join(", ", option.Alternates)}");
        return steps;
    }

    private static List<BiosBrandGuide> LoadAll(List<string> warnings)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1. Fichiers intégrés à l'exécutable
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("Aria.Bios.")))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            files[name["Aria.Bios.".Length..]] = reader.ReadToEnd();
        }

        // 2. Remplacements éventuels à côté de l'exe
        var overrideDir = Path.Combine(AppContext.BaseDirectory, "bios");
        if (Directory.Exists(overrideDir))
            foreach (var file in Directory.GetFiles(overrideDir, "*.json"))
                files[Path.GetFileName(file)] = File.ReadAllText(file);

        var guides = new List<BiosBrandGuide>();
        foreach (var (name, json) in files)
        {
            try
            {
                if (JsonSerializer.Deserialize<BiosBrandGuide>(json, JsonOptions) is { } guide) guides.Add(guide);
            }
            catch (JsonException e)
            {
                warnings.Add($"Fichier BIOS {name} illisible : {e.Message}");
            }
        }
        return guides;
    }
}
