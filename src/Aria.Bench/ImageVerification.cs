namespace Aria.Bench;

/// <summary>
/// Vérification des images, comme OCCT : une image de contrôle (toujours le même instant de la scène) est calculée
/// au début de la passe, puis recalculée régulièrement. Sur une carte stable, le résultat est identique au bit près ;
/// la moindre différence est une erreur de calcul, typique d'un undervolt ou d'un overclocking trop poussé.
/// </summary>
public sealed class ImageVerification
{
    private byte[]? _reference;

    public int Checks { get; private set; }
    public int Failures { get; private set; }
    public long MaxDifferentPixels { get; private set; }
    public string? Fingerprint { get; private set; }

    /// <summary>Enregistre la référence (première image de contrôle).</summary>
    public void SetReference(byte[] pixels)
    {
        _reference = pixels;
        Fingerprint = Fnv1a64(pixels).ToString("x16");
    }

    /// <summary>Compare une nouvelle image de contrôle à la référence. Renvoie le nombre de pixels différents.</summary>
    public long Check(byte[] pixels)
    {
        if (_reference is null) throw new InvalidOperationException("Référence absente.");
        Checks++;
        var different = CountDifferentPixels(_reference, pixels);
        if (different > 0)
        {
            Failures++;
            MaxDifferentPixels = Math.Max(MaxDifferentPixels, different);
        }
        return different;
    }

    /// <summary>Pixels RGBA (4 octets) dont au moins une composante diffère.</summary>
    public static long CountDifferentPixels(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return Math.Max(a.Length, b.Length) / 4;
        if (a.SequenceEqual(b)) return 0;
        long count = 0;
        for (var i = 0; i + 3 < a.Length; i += 4)
            if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2] || a[i + 3] != b[i + 3]) count++;
        return count;
    }

    public static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in data) hash = (hash ^ b) * 1099511628211UL;
        return hash;
    }
}
