using System.Diagnostics;
using System.Reflection;
using System.Text;
using Aria.Bench.Native;

namespace Aria.Bench.Stability;

/// <summary>
/// Ce que le noyau des cœurs Tensor doit trouver, calculé sur le processeur. Un réservoir de 64 paires de tuiles 16 × 16
/// (A en lignes, B en colonnes, valeurs INT8 pseudo-aléatoires) est lu en boucle par chaque warp : une exécution fait
/// <see cref="SegmentIterations"/> multiplications par segment, et chaque segment tombe sur chaque paire le même nombre
/// de fois. Le résultat est donc le même pour tous les warps et se calcule en une somme exacte (entiers, sans arrondi).
/// </summary>
internal static class TensorReference
{
    public const int Pool = 64, Tile = 16, TileBytes = 256, OutputElements = 256, SegmentIterations = 4096;
    private const uint Seed = 0x2545F491;

    /// <summary>Le réservoir : A (16 × 16 par ligne) puis B (16 × 16 par colonne), pour chacune des 64 paires.</summary>
    public static (sbyte[] A, sbyte[] B) BuildPool()
    {
        var a = new sbyte[Pool * TileBytes];
        var b = new sbyte[Pool * TileBytes];
        var state = Seed;
        for (var i = 0; i < a.Length; i++) { state = Next(state); a[i] = (sbyte)(state >> 24); }
        for (var i = 0; i < b.Length; i++) { state = Next(state); b[i] = (sbyte)(state >> 24); }
        return (a, b);
    }

    private static uint Next(uint x)
    {
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        return x;
    }

    /// <summary>Somme, sur les 64 paires, du produit A × B de chaque case (ligne, colonne) : exacte.</summary>
    public static long[] Totals(sbyte[] a, sbyte[] b)
    {
        var totals = new long[OutputElements];
        for (var t = 0; t < Pool; t++)
            for (var row = 0; row < Tile; row++)
                for (var column = 0; column < Tile; column++)
                {
                    long sum = 0;
                    for (var k = 0; k < Tile; k++) sum += a[t * TileBytes + row * Tile + k] * (long)b[t * TileBytes + column * Tile + k];
                    totals[row * Tile + column] += sum;
                }
        return totals;
    }

    /// <summary>Résultat attendu de chaque case après <paramref name="segments"/> segments (le total du noyau déborde et se replie sur 32 bits, comme sur la carte).</summary>
    public static int[] Expected(long[] totals, long segments)
    {
        var expected = new int[OutputElements];
        for (var i = 0; i < expected.Length; i++)
            expected[i] = unchecked((int)(segments * (SegmentIterations / Pool) * totals[i]));
        return expected;
    }
}

/// <summary>
/// Calcul des cœurs Tensor, vérifié bit à bit : multiplication-accumulation INT8 vers INT32 (instructions wmma du PTX), exacte
/// et sans arrondi, donc comparable au résultat calculé sur le processeur. C'est ce que fait une IA locale (inférence d'un
/// LLM, génération d'images) sur les cœurs Tensor, que ni le rendu 3D ni le calcul vérifié par shader ne sollicitent.
/// Quatre lancements d'environ 60 ms sont envoyés avant chaque relecture, pour garder la carte à sa limite de puissance.
/// </summary>
internal sealed class TensorTest
{
    private const uint ThreadsPerBlock = 128;
    private const uint BlocksPerMultiprocessor = 8;
    private const int Queued = 4;
    private const double MultiplyAddsPerTile = 4096;      // 16 × 16 × 16
    private static readonly TimeSpan TargetLaunch = TimeSpan.FromMilliseconds(60);

    /// <summary>Lancements dont les résultats ont été relus et comparés.</summary>
    public long Executions { get; private set; }
    public long WrongElements { get; private set; }
    public long ElementsChecked { get; private set; }
    /// <summary>Résultats comparés par lancement : 256 par warp.</summary>
    public long ElementsPerLaunch { get; private set; }
    public Dictionary<int, int> FaultyWarps { get; } = [];
    public List<WrongWord> FirstErrors { get; } = [];
    public uint Segments { get; private set; } = 1;
    /// <summary>Débit médian, en milliers de milliards d'opérations INT8 par seconde : une information, pas un verdict.</summary>
    public double MedianTops { get; private set; }

    /// <summary>Auto-contrôle : un lancement est fait exprès avec un segment de plus, tous ses résultats doivent différer.</summary>
    public bool InjectFault { get; init; }

    public void Run(CudaContext cuda, Func<bool> keepGoing)
    {
        var (a, b) = TensorReference.BuildPool();
        var totals = TensorReference.Totals(a, b);
        var function = cuda.LoadKernel(ReadPtx(), "tensor_int8");

        var poolA = cuda.Alloc(a.Length);
        var poolB = cuda.Alloc(b.Length);
        cuda.Upload(poolA, a);
        cuda.Upload(poolB, b);

        var blocks = (uint)cuda.Multiprocessors * BlocksPerMultiprocessor;
        var warps = (int)(blocks * ThreadsPerBlock / 32);
        ElementsPerLaunch = (long)warps * TensorReference.OutputElements;
        var outputs = new IntPtr[Queued];
        for (var i = 0; i < Queued; i++) outputs[i] = cuda.Alloc(ElementsPerLaunch * 4);
        var got = new int[ElementsPerLaunch];

        void Launch(IntPtr output, uint segments) =>
            cuda.Launch(function, blocks, ThreadsPerBlock, poolA, poolB, output, (uint)TensorReference.SegmentIterations, segments);

        // Étalonnage : un lancement pour réveiller le pilote, puis quatre segments mesurés ; on vise environ 60 ms par lancement
        Launch(outputs[0], 1);
        cuda.Synchronize();
        var watch = Stopwatch.StartNew();
        Launch(outputs[0], 4);
        cuda.Synchronize();
        var perSegment = Math.Max(watch.Elapsed.TotalSeconds / 4, 1e-5);
        Segments = (uint)Math.Clamp(TargetLaunch.TotalSeconds / perSegment, 1, 4096);
        var expected = TensorReference.Expected(totals, Segments);

        if (InjectFault)
        {
            Launch(outputs[0], Segments + 1);
            cuda.Synchronize();
            cuda.Download(outputs[0], got);
            Compare(got, expected, record: false);
            Executions++;
            ElementsChecked += ElementsPerLaunch;
        }

        var rates = new List<double>();
        while (keepGoing())
        {
            var round = Stopwatch.StartNew();
            for (var i = 0; i < Queued; i++) Launch(outputs[i], Segments);
            cuda.Synchronize();
            var seconds = round.Elapsed.TotalSeconds;
            rates.Add(2 * MultiplyAddsPerTile * warps * Segments * TensorReference.SegmentIterations * Queued / seconds / 1e12);
            for (var i = 0; i < Queued; i++)
            {
                cuda.Download(outputs[i], got);
                Compare(got, expected, record: true);
            }
            Executions += Queued;
            ElementsChecked += Queued * ElementsPerLaunch;
        }
        if (rates.Count > 0) MedianTops = Math.Round(rates.Order().ElementAt(rates.Count / 2), 1);
    }

    private void Compare(int[] got, int[] expected, bool record)
    {
        for (var warp = 0; warp < got.Length / TensorReference.OutputElements; warp++)
        {
            var first = warp * TensorReference.OutputElements;
            var wrong = 0;
            for (var i = 0; i < TensorReference.OutputElements; i++)
            {
                if (got[first + i] == expected[i]) continue;
                wrong++;
                WrongElements++;
                if (record && FirstErrors.Count < 16)
                    FirstErrors.Add(new WrongWord((ulong)(first + i), $"0x{expected[i]:X8}", $"0x{got[first + i]:X8}"));
            }
            if (wrong > 0 && record) FaultyWarps[warp] = FaultyWarps.GetValueOrDefault(warp) + 1;
        }
    }

    private static string ReadPtx()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Aria.Bench.Shaders.Tensor.ptx")
            ?? throw new BenchException("Noyau des cœurs Tensor introuvable dans l'exe : Tensor.ptx");
        using var reader = new StreamReader(stream, Encoding.ASCII);
        // Fins de ligne de Windows ou d'Unix selon l'extraction du dépôt : le noyau est toujours donné au pilote avec des \n
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }
}
