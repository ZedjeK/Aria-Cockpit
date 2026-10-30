namespace Aria.Bench;

/// <summary>
/// Préréglage graphique, comme dans les options d'un jeu. Le paysage est le même partout :
/// seuls la densité, les ombres, l'éclairage et les effets changent.
/// </summary>
/// <param name="TerrainResolution">Carrés par côté du terrain (densité du maillage).</param>
/// <param name="ObjectGrid">Objets par côté (tours en ville, rochers ailleurs) : ObjectGrid² instances.</param>
/// <param name="ShadowMapSize">Côté de la carte d'ombre du soleil, en pixels.</param>
/// <param name="PcfRadius">Adoucissement des ombres : (2r + 1)² échantillons filtrés par pixel.</param>
/// <param name="LightCount">Lumières néon ponctuelles évaluées par pixel.</param>
/// <param name="Octaves">Octaves de bruit du détail du sol (calcul par pixel).</param>
/// <param name="SsaoSamples">Échantillons d'occlusion ambiante (0 : désactivée).</param>
/// <param name="BloomLevels">Niveaux du halo lumineux (0 : désactivé).</param>
/// <param name="FogDensity">Brume : plus faible = on voit plus loin.</param>
/// <param name="RayTraced">Ray tracing (DXR 1.1) : ombre du soleil tracée, occlusion ambiante tracée et reflets. Il faut une
/// carte qui le prend en charge (GeForce RTX, Radeon RX 6000 ou plus récentes).</param>
/// <param name="AoRays">Rayons d'occlusion ambiante par pixel (ray tracing).</param>
public sealed record QualityPreset(string Id, string Label, int TerrainResolution, int ObjectGrid, int ShadowMapSize,
    int PcfRadius, int LightCount, int Octaves, int SsaoSamples, int BloomLevels, float FogDensity, bool RayTraced = false, int AoRays = 0)
{
    public const int MaxLights = 128;

    public static QualityPreset Low { get; } = new("faible", "Faible", 256, 64, 1024, 0, 8, 2, 0, 0, 0.0012f);
    public static QualityPreset Medium { get; } = new("moyen", "Moyen", 512, 110, 2048, 1, 32, 3, 8, 3, 0.0009f);
    public static QualityPreset High { get; } = new("eleve", "Élevé", 768, 170, 4096, 1, 64, 4, 12, 4, 0.0007f);
    public static QualityPreset Ultra { get; } = new("ultra", "Ultra", 1024, 240, 4096, 2, 96, 5, 16, 5, 0.0005f);

    /// <summary>
    /// Ultra, éclairé par ray tracing : ombre du soleil (1 rayon par pixel, à la place de la carte d'ombre), occlusion
    /// ambiante (4 rayons, à la place du SSAO) et reflets (1 rayon). Fait travailler les cœurs RT de la carte.
    /// </summary>
    public static QualityPreset UltraRt { get; } = new("ultra-rt", "Ultra RT", 1024, 240, 4096, 0, 96, 5, 0, 5, 0.0005f, RayTraced: true, AoRays: 4);

    /// <summary>Préréglages sans ray tracing (« --qualite tout ») : ils tournent sur toutes les cartes.</summary>
    public static IReadOnlyList<QualityPreset> All { get; } = [Low, Medium, High, Ultra];

    public int ObjectCount => ObjectGrid * ObjectGrid;

    /// <summary>« eleve », « élevé », « Ultra », « ultra-rt », « Ultra RT »… ; null si inconnu.</summary>
    public static QualityPreset? Find(string text)
    {
        var key = text.Trim().ToLowerInvariant().Replace('é', 'e').Replace('è', 'e').Replace(' ', '-').Replace('_', '-');
        if (key == "ultrart") key = UltraRt.Id;
        return All.Append(UltraRt).FirstOrDefault(p => p.Id == key);
    }
}
