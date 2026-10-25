using System.Reflection;

namespace Aria.Bench.Rendering;

/// <summary>
/// Shaders précompilés en DXIL signé (modèle 6.0), intégrés à l'exe : Aria-Bench n'embarque aucun compilateur.
/// Sources dans Shaders/*.hlsl ; après une modification, relancer outils/compiler-shaders.py (les tests le vérifient).
/// </summary>
public static class ShaderLibrary
{
    public static IReadOnlyList<string> Files { get; } = ["Scene", "Post"];

    /// <summary>Tous les points d'entrée utilisés par le moteur (fichier source, profil, nom).</summary>
    public static IReadOnlyList<(string File, string Profile, string Entry)> Entries { get; } =
    [
        ("Scene", "vs_6_0", "VSSky"), ("Scene", "ps_6_0", "PSSky"),
        ("Scene", "vs_6_0", "VSTerrain"), ("Scene", "ps_6_0", "PSTerrain"),
        ("Scene", "vs_6_0", "VSObjects"), ("Scene", "ps_6_0", "PSObjects"),
        ("Post", "vs_6_0", "VSFull"), ("Post", "ps_6_0", "PSSsao"),
        ("Post", "ps_6_0", "PSBloomDown"), ("Post", "ps_6_0", "PSBloomUp"),
        ("Post", "ps_6_0", "PSComposite")
    ];

    /// <summary>Code compilé d'un point d'entrée.</summary>
    public static byte[] Load(string entry)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"Aria.Bench.Shaders.{entry}.cso")
            ?? throw new BenchException($"Shader compilé introuvable dans l'exe : {entry}.cso");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        if (bytes.Length < 4 || bytes[0] != 'D' || bytes[1] != 'X' || bytes[2] != 'B' || bytes[3] != 'C')
            throw new BenchException($"Shader compilé invalide : {entry}.cso");
        return bytes;
    }

    /// <summary>Tous les points d'entrée ; clé : nom du point d'entrée.</summary>
    public static Dictionary<string, byte[]> LoadAll() => Entries.ToDictionary(e => e.Entry, e => Load(e.Entry));
}
