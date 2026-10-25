namespace Aria.Bench;

/// <summary>
/// Vérification des images, comme OCCT : une image de contrôle (toujours le même instant de la scène) est calculée
/// après la chauffe, puis recalculée régulièrement. Sur une carte stable, le résultat est identique au bit près.
/// En cas d'écart, l'image est recalculée aussitôt (contre-vérification) :
/// - même résultat que l'image fautive : calcul différent mais stable, le pilote a réoptimisé ses shaders
///   (écarts de quelques niveaux sur quelques pixels) → nouvelle référence, pas une erreur ;
/// - sinon (retour à la référence ou troisième résultat) : erreur de calcul, typique d'un réglage instable.
///   Une carte instable ne reproduit jamais deux fois la même erreur au pixel près.
/// </summary>
public sealed class ImageVerification
{
    private byte[]? _reference;

    /// <summary>Référence et première image fautive (pour les enregistrer avec --images).</summary>
    public byte[]? Reference => _reference;
    public byte[]? FirstFailure { get; private set; }
    /// <summary>Rectangle englobant les pixels différents de la première image fautive (x, y, largeur, hauteur).</summary>
    public (int X, int Y, int Width, int Height)? FailureBounds { get; private set; }
    public int ImageWidth { get; set; }

    public int Checks { get; private set; }
    /// <summary>Références remplacées après contre-vérification (calcul du pilote changé, pas une erreur).</summary>
    public int ReferenceUpdates { get; private set; }
    public int Failures { get; private set; }
    public long MaxDifferentPixels { get; private set; }
    public string? Fingerprint { get; private set; }

    /// <summary>Enregistre la référence (première image de contrôle).</summary>
    public void SetReference(byte[] pixels)
    {
        _reference = pixels;
        Fingerprint = Fnv1a64(pixels).ToString("x16");
    }

    /// <summary>
    /// Compare une nouvelle image de contrôle à la référence ; en cas d'écart, <paramref name="recheck"/> recalcule
    /// l'image pour la contre-vérification. Renvoie le nombre de pixels différents retenu comme erreur (0 si aucune).
    /// </summary>
    public long Check(byte[] pixels, Func<byte[]> recheck)
    {
        if (_reference is null) throw new InvalidOperationException("Référence absente.");
        Checks++;
        var different = CountDifferentPixels(_reference, pixels);
        if (different == 0) return 0;

        var again = recheck();
        if (CountDifferentPixels(pixels, again) == 0)
        {
            // Deux fois exactement le même résultat : calcul stable, la référence change
            ReferenceUpdates++;
            _reference = again;
            Fingerprint = Fnv1a64(again).ToString("x16");
            return 0;
        }
        different = Math.Max(different, CountDifferentPixels(_reference, again));
        {
            Failures++;
            MaxDifferentPixels = Math.Max(MaxDifferentPixels, different);
            if (FirstFailure is null)
            {
                FirstFailure = pixels;
                if (ImageWidth > 0) FailureBounds = Bounds(_reference, pixels, ImageWidth);
            }
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

    /// <summary>Rectangle englobant les pixels différents.</summary>
    public static (int X, int Y, int Width, int Height)? Bounds(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, int width)
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var i = 0; i + 3 < Math.Min(a.Length, b.Length); i += 4)
        {
            if (a[i] == b[i] && a[i + 1] == b[i + 1] && a[i + 2] == b[i + 2] && a[i + 3] == b[i + 3]) continue;
            var pixel = i / 4;
            int x = pixel % width, y = pixel / width;
            minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
        }
        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Carte des écarts : la référence assombrie, les pixels différents en magenta.</summary>
    public static byte[] DifferenceMap(ReadOnlySpan<byte> reference, ReadOnlySpan<byte> other)
    {
        var map = new byte[reference.Length];
        for (var i = 0; i + 3 < reference.Length; i += 4)
        {
            var same = reference[i] == other[i] && reference[i + 1] == other[i + 1] && reference[i + 2] == other[i + 2];
            map[i] = same ? (byte)(reference[i] / 4) : (byte)255;
            map[i + 1] = same ? (byte)(reference[i + 1] / 4) : (byte)0;
            map[i + 2] = same ? (byte)(reference[i + 2] / 4) : (byte)255;
            map[i + 3] = 255;
        }
        return map;
    }

    /// <summary>Écrit une image RGBA en BMP 32 bits (lisible partout, sans bibliothèque).</summary>
    public static void SaveBmp(string path, ReadOnlySpan<byte> rgba, int width, int height)
    {
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        var size = width * height * 4;
        w.Write((byte)'B'); w.Write((byte)'M'); w.Write(54 + size); w.Write(0); w.Write(54);
        w.Write(40); w.Write(width); w.Write(-height); w.Write((short)1); w.Write((short)32); w.Write(0); w.Write(size);
        w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        var row = new byte[width * 4];
        for (var y = 0; y < height; y++)
        {
            var line = rgba.Slice(y * width * 4, width * 4);
            for (var x = 0; x < width; x++)
            {
                row[x * 4] = line[x * 4 + 2];      // B
                row[x * 4 + 1] = line[x * 4 + 1];  // G
                row[x * 4 + 2] = line[x * 4];      // R
                row[x * 4 + 3] = 255;
            }
            w.Write(row);
        }
    }

    public static ulong Fnv1a64(ReadOnlySpan<byte> data)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in data) hash = (hash ^ b) * 1099511628211UL;
        return hash;
    }
}
