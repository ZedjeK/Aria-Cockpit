using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aria.Core.Nvidia;

/// <summary>Architecture d'une carte NVIDIA (valeurs de nvmlDeviceArchitecture_t).</summary>
public enum GpuArchitecture
{
    Unknown = 0,
    Kepler = 2,
    Maxwell = 3,
    Pascal = 4,
    Volta = 5,
    Turing = 6,
    Ampere = 7,
    Ada = 8,
    Hopper = 9,
    Blackwell = 10
}

/// <summary>Ce que la carte sait faire en DLSS (selon le modèle : une GTX 16 est Turing mais sans DLSS).</summary>
public enum DlssSupport { None, SuperResolution, FrameGeneration, MultiFrameGeneration }

/// <summary>Fiche d'un modèle GeForce : ce qui est fixe pour ce modèle, quel que soit le fabricant de la carte.</summary>
/// <param name="Memory">Type(s) de mémoire ; plusieurs quand un même nom existe en deux versions (ex. RTX 4070 GDDR6X puis GDDR6).</param>
public sealed record GpuModel(string Name, GpuArchitecture Architecture, bool IsLaptop, IReadOnlyList<string> Memory, IReadOnlyList<int> VramGb, bool IsRtx)
{
    public DlssSupport Dlss => !IsRtx ? DlssSupport.None : Architecture switch
    {
        GpuArchitecture.Blackwell => DlssSupport.MultiFrameGeneration,
        GpuArchitecture.Ada => DlssSupport.FrameGeneration,
        _ => DlssSupport.SuperResolution
    };

    /// <summary>« GDDR6X » ou « GDDR6X ou GDDR6 ».</summary>
    public string MemoryText => string.Join(" ou ", Memory);
}

/// <summary>Réglages que l'architecture permet, d'après la documentation NVML.</summary>
/// <param name="ClockLock">Plafond de fréquence (nvmlDeviceSetGpuLockedClocks) : Volta et plus récentes.</param>
/// <param name="ClockOffsets">Décalages de fréquence (nvmlDeviceSetClockOffsets) : Maxwell et plus récentes.</param>
public sealed record GpuCapabilities(GpuArchitecture Architecture, bool ClockLock, bool ClockOffsets)
{
    /// <summary>Architecture inconnue (carte plus récente que la table) : on suppose tout possible, le pilote refusera sinon.</summary>
    public static GpuCapabilities For(GpuArchitecture arch) => arch == GpuArchitecture.Unknown
        ? new(arch, true, true)
        : new(arch, arch >= GpuArchitecture.Volta, arch >= GpuArchitecture.Maxwell);
}

/// <summary>
/// Table des cartes GeForce, de la GTX 750 (Maxwell) aux RTX 50 (Blackwell), intégrée à l'exe (config/gpus/geforce.json).
/// Elle ne contient que ce qui est fixe pour un modèle ; fréquences et puissances sont lues en direct sur la carte,
/// car elles varient d'un fabricant à l'autre.
/// </summary>
public static class GpuCatalog
{
    private sealed record Entry(string Name, string Arch, string Segment, string[] Memory, int[] Vram, bool Rtx = false);
    private sealed record Document(Entry[] Models);

    private static readonly Lazy<IReadOnlyList<GpuModel>> All = new(Load);

    public static IReadOnlyList<GpuModel> Models => All.Value;

    /// <summary>Fiche du modèle d'après le nom donné par le pilote (« NVIDIA GeForce RTX 4070 Laptop GPU ») ; null si inconnu.</summary>
    public static GpuModel? Find(string gpuName)
    {
        var key = Normalize(gpuName);
        if (key.Length == 0) return null;
        return Models.FirstOrDefault(m => Normalize(m.Name) == key)
               // « TITAN X » de la GTX TITAN X (Maxwell) et variantes à suffixe : on retente sans le mot le plus court
               ?? Models.FirstOrDefault(m => Normalize(m.Name) == key.Replace("GTX ", "", StringComparison.Ordinal));
    }

    /// <summary>Architecture déduite du nom, pour une carte absente de la table (nouvelle référence, variante régionale…).</summary>
    public static GpuArchitecture ArchitectureFromName(string gpuName)
    {
        var n = Normalize(gpuName);
        var m = Regex.Match(n, @"\b(?:RTX|GTX|GT)\s*(\d{3,4})\b");
        if (!m.Success) return n.Contains("TITAN RTX") ? GpuArchitecture.Turing : GpuArchitecture.Unknown;
        var number = int.Parse(m.Groups[1].Value);
        return number switch
        {
            >= 5000 and < 6000 => GpuArchitecture.Blackwell,
            >= 4000 and < 5000 => GpuArchitecture.Ada,
            >= 3000 and < 4000 => GpuArchitecture.Ampere,
            2050 => GpuArchitecture.Ampere,
            >= 2000 and < 3000 => GpuArchitecture.Turing,
            >= 1600 and < 1700 => GpuArchitecture.Turing,
            >= 1000 and < 1100 => GpuArchitecture.Pascal,
            >= 900 and < 1000 => GpuArchitecture.Maxwell,
            750 => GpuArchitecture.Maxwell,
            _ => GpuArchitecture.Unknown
        };
    }

    /// <summary>« NVIDIA GeForce RTX 4070 Laptop GPU » → « RTX 4070 LAPTOP » ; « GeForce GTX 1060 6GB » → « GTX 1060 ».</summary>
    internal static string Normalize(string name)
    {
        var n = name.ToUpperInvariant();
        n = Regex.Replace(n, @"\b(NVIDIA|GEFORCE|GPU|WITH MAX-Q DESIGN|MAX-Q)\b", " ");
        n = Regex.Replace(n, @"\b\d+\s*GB\b", " ");
        n = Regex.Replace(n, @"\s+", " ").Trim();
        return n;
    }

    /// <summary>Nom affiché de l'architecture : « Ada Lovelace (RTX 40) ».</summary>
    public static string Describe(GpuArchitecture arch) => arch switch
    {
        GpuArchitecture.Kepler => "Kepler (GTX 600 et 700)",
        GpuArchitecture.Maxwell => "Maxwell (GTX 750 et 900)",
        GpuArchitecture.Pascal => "Pascal (GTX 10)",
        GpuArchitecture.Volta => "Volta",
        GpuArchitecture.Turing => "Turing (GTX 16 et RTX 20)",
        GpuArchitecture.Ampere => "Ampere (RTX 30)",
        GpuArchitecture.Ada => "Ada Lovelace (RTX 40)",
        GpuArchitecture.Hopper => "Hopper",
        GpuArchitecture.Blackwell => "Blackwell (RTX 50)",
        _ => "inconnue"
    };

    private static IReadOnlyList<GpuModel> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Aria.Gpus.geforce.json");
        if (stream is null) return [];
        var doc = JsonSerializer.Deserialize<Document>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return doc?.Models.Select(e => new GpuModel(e.Name,
                Enum.TryParse<GpuArchitecture>(e.Arch, out var a) ? a : GpuArchitecture.Unknown,
                e.Segment == "laptop", e.Memory, e.Vram, e.Rtx)).ToList()
            ?? [];
    }
}
