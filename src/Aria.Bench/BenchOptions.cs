using System.Globalization;

namespace Aria.Bench;

/// <summary>
/// Options du banc d'essai, lues sur la ligne de commande.
/// Sans option : les trois définitions à la suite, 20 secondes chacune, en plein écran.
/// </summary>
public sealed record BenchOptions(
    IReadOnlyList<int> Definitions,
    TimeSpan Duration,
    TimeSpan Warmup,
    bool Windowed,
    bool Debug,
    string? OutputPath)
{
    public static readonly int[] AllDefinitions = [1080, 1440, 2160];
    public static readonly TimeSpan DefaultDuration = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan DefaultWarmup = TimeSpan.FromSeconds(3);

    public static BenchOptions Default { get; } =
        new(AllDefinitions, DefaultDuration, DefaultWarmup, Windowed: false, Debug: false, OutputPath: null);

    public const string Help =
        "Aria-Bench [--def 1080|1440|2160|tout] [--duree secondes] [--fenetre] [--debug] [--sortie fichier.json]";

    /// <summary>Taille du rendu interne (16:9) pour une définition.</summary>
    public static (int Width, int Height) SizeOf(int definition) => definition switch
    {
        1080 => (1920, 1080),
        1440 => (2560, 1440),
        2160 => (3840, 2160),
        _ => throw new ArgumentOutOfRangeException(nameof(definition), definition, "Définition inconnue")
    };

    public static string Label(int definition) => definition == 2160 ? "4K (2160p)" : $"{definition}p";

    /// <summary>Lit la ligne de commande. Lève <see cref="ArgumentException"/> avec un message en français.</summary>
    public static BenchOptions Parse(IReadOnlyList<string> args)
    {
        var options = Default;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i].ToLowerInvariant();
            switch (arg)
            {
                case "--def":
                    options = options with { Definitions = ParseDefinitions(Next(args, ref i, arg)) };
                    break;
                case "--duree":
                    var raw = Next(args, ref i, arg);
                    if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) || seconds < 5 || seconds > 3600)
                        throw new ArgumentException($"Durée invalide : « {raw} » (de 5 à 3600 secondes).");
                    options = options with { Duration = TimeSpan.FromSeconds(seconds) };
                    break;
                case "--fenetre":
                    options = options with { Windowed = true };
                    break;
                case "--debug":
                    options = options with { Debug = true };
                    break;
                case "--sortie":
                    options = options with { OutputPath = Next(args, ref i, arg) };
                    break;
                default:
                    throw new ArgumentException($"Option inconnue : « {args[i]} ». Utilisation : {Help}");
            }
        }
        return options;
    }

    private static string Next(IReadOnlyList<string> args, ref int i, string option)
    {
        if (i + 1 >= args.Count) throw new ArgumentException($"Valeur manquante après {option}.");
        return args[++i];
    }

    private static int[] ParseDefinitions(string raw)
    {
        if (raw.Equals("tout", StringComparison.OrdinalIgnoreCase)) return AllDefinitions;
        var list = new List<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = part.TrimEnd('p', 'P');
            if (text.Equals("4k", StringComparison.OrdinalIgnoreCase)) text = "2160";
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var def) || !AllDefinitions.Contains(def))
                throw new ArgumentException($"Définition invalide : « {part} » (1080, 1440, 2160 ou tout).");
            if (!list.Contains(def)) list.Add(def);
        }
        if (list.Count == 0) throw new ArgumentException("Aucune définition indiquée après --def.");
        return [.. list];
    }
}
