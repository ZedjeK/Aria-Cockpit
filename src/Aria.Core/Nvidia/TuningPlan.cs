using Aria.Core.Performance;

namespace Aria.Core.Nvidia;

/// <summary>Objectif de l'assistant guidé. Équilibre passe en premier : son résultat sert de base aux deux autres.</summary>
public enum TuningGoal
{
    /// <summary>Undervolt : plafond à la fréquence haute habituelle en jeu + décalage GPU croissant. Mêmes FPS, moins de watts.</summary>
    Balanced,
    /// <summary>À partir d'Équilibre : plafond plus bas, palier par palier, jusqu'à la perte jugée acceptable.</summary>
    Economy,
    /// <summary>À partir d'Équilibre : limite de puissance au maximum, sans plafond, décalage GPU croissant.</summary>
    Performance,
    /// <summary>Phase facultative : décalage mémoire croissant, à partir du réglage GPU retenu.</summary>
    Memory
}

/// <summary>
/// Recherche du meilleur palier, du grossier vers le fin : grands pas tant que ça tient, puis pas moyens à partir
/// du dernier palier stable, puis un seul palier d'affinage. Exemple (décalage GPU) :
/// +100 ✓, +200 ✓, +300 ✗ → +230 ✓, +260 ✗ → +245 → fin.
/// Aucun palier ne dépasse un palier déjà jugé instable, ni les bornes d'Aria et de la carte.
/// </summary>
public sealed class TuningSearch
{
    /// <summary>Nom affiché de chaque étape de la recherche.</summary>
    private static readonly string[] StageNames = ["GRANDS PAS", "PAS MOYENS", "AFFINAGE"];

    private readonly Func<int, GpuTuningState> _at;
    private readonly int[] _steps;
    private readonly int _max, _backoff;
    private int _stable, _fail = int.MaxValue, _candidate, _stage;

    /// <param name="at">Réglage correspondant à une distance depuis le départ (MHz de décalage, mW de puissance en moins).</param>
    /// <param name="steps">Pas de chaque étape (du plus grand au plus petit).</param>
    /// <param name="max">Distance maximale autorisée depuis le départ.</param>
    /// <param name="backoff">Recul de sécurité appliqué au résultat quand un palier a planté juste au-dessus.</param>
    private TuningSearch(Func<int, GpuTuningState> at, int[] steps, int max, int backoff = 0)
    {
        _at = at;
        _steps = steps;
        _max = Math.Max(0, max);
        _backoff = backoff;
        Advance();
    }

    public TuningGoal Goal { get; private init; }

    /// <summary>La recherche est terminée : <see cref="LastStable"/> est le résultat.</summary>
    public bool Finished { get; private set; }

    /// <summary>Palier à tester maintenant (sans objet si la recherche est terminée).</summary>
    public GpuTuningState Current => _at(_candidate);

    /// <summary>Dernier palier jugé stable (le départ tant qu'aucun palier n'a tenu).</summary>
    public GpuTuningState LastStable => _at(_stable);

    /// <summary>
    /// Résultat à garder : le dernier palier stable, reculé d'un cran s'il touchait un palier instable
    /// (un réglage qui a tenu 3 minutes au bord de la limite peut lâcher au bout d'une heure).
    /// </summary>
    public GpuTuningState Safe => _backoff > 0 && _fail != int.MaxValue ? _at(Math.Max(0, _stable - _backoff)) : LastStable;

    /// <summary>Un palier a été jugé instable pendant la recherche.</summary>
    public bool HitLimit => _fail != int.MaxValue;

    /// <summary>Nombre de paliers déjà jugés.</summary>
    public int Tested { get; private set; }

    public string StageName => StageNames[Math.Min(_stage, StageNames.Length - 1)];

    /// <summary>Verdict sur le palier en cours : stable (on continue) ou non (on affine à partir du dernier stable).</summary>
    public void Report(bool stable)
    {
        if (Finished) return;
        Tested++;
        // L'affinage ne compte qu'un palier : quel que soit son verdict, la recherche s'arrête après lui
        var wasFine = _steps.Length > 1 && _stage == _steps.Length - 1;
        if (stable) _stable = _candidate;
        else
        {
            _fail = _candidate;
            _stage++;
        }
        if (wasFine || _stage >= _steps.Length)
        {
            Finished = true;
            return;
        }
        Advance();
    }

    private void Advance()
    {
        while (_stage < _steps.Length)
        {
            var candidate = Math.Min(_stable + _steps[_stage], _max);
            if (candidate <= _stable)
            {
                // Borne atteinte : rien de plus à essayer
                Finished = true;
                return;
            }
            if (candidate < _fail)
            {
                _candidate = candidate;
                return;
            }
            // Le pas dépasserait un palier déjà instable : on passe au pas plus fin
            _stage++;
        }
        Finished = true;
    }

    /// <summary>Équilibre : plafond fixe, décalage GPU croissant par pas de 100, 30 puis 15 MHz ; recul de 15 MHz au bord.</summary>
    public static TuningSearch Balanced(GpuTuningState start, GpuTuningLimits limits, uint cap) =>
        new(d => start with { MaxClockMHz = cap, CoreOffsetMHz = start.CoreOffsetMHz + d },
            [100, 30, 15], Math.Min(TuningPlan.CoreLimit, limits.MaxCoreOffsetMHz) - start.CoreOffsetMHz, TuningPlan.ClockBin)
        { Goal = TuningGoal.Balanced };

    /// <summary>
    /// Économie : à partir du réglage d'Équilibre, le plafond baisse par pas d'environ 5 %, puis 2,5 %, puis 15 MHz,
    /// sans descendre sous 85 % du plafond d'Équilibre. « Stable » veut dire ici : perte jugée acceptable.
    /// </summary>
    public static TuningSearch Economy(GpuTuningState balanced, GpuTuningLimits limits)
    {
        var cap = balanced.MaxClockMHz != 0 ? balanced.MaxClockMHz : limits.MaxClockMHz;
        var floor = Math.Max((int)limits.MinClockMHz + TuningPlan.ClockBin, Bin(cap * TuningPlan.EconomyFloor, up: true));
        return new TuningSearch(d => balanced with { MaxClockMHz = (uint)(cap - d) },
            [Math.Max(TuningPlan.ClockBin, Bin(cap * 0.05)), Math.Max(TuningPlan.ClockBin, Bin(cap * 0.025)), TuningPlan.ClockBin],
            (int)cap - floor) { Goal = TuningGoal.Economy };
    }

    /// <summary>
    /// Performance : réglage de départ = décalage d'Équilibre, sans plafond, limite de puissance au maximum de la carte
    /// (à mesurer d'abord) ; puis décalage croissant par pas de 60, 30 puis 15 MHz ; recul de 15 MHz au bord.
    /// </summary>
    public static TuningSearch Performance(GpuTuningState balanced, GpuTuningLimits limits)
    {
        var start = PerformanceStart(balanced, limits);
        return new TuningSearch(d => start with { CoreOffsetMHz = start.CoreOffsetMHz + d },
            [60, 30, 15], Math.Min(TuningPlan.CoreLimit, limits.MaxCoreOffsetMHz) - start.CoreOffsetMHz, TuningPlan.ClockBin)
        { Goal = TuningGoal.Performance };
    }

    /// <summary>Point de départ de Performance : le décalage d'Équilibre, sans plafond, puissance au maximum.</summary>
    public static GpuTuningState PerformanceStart(GpuTuningState balanced, GpuTuningLimits limits) =>
        balanced with { MaxClockMHz = 0, PowerLimitMilliwatts = limits.MaxPowerMilliwatts };

    /// <summary>Décalage mémoire par paliers fixes de 250 MHz jusqu'à +1000 : au premier problème, on s'arrête.</summary>
    public static TuningSearch Memory(GpuTuningState stable, GpuTuningLimits limits) =>
        new(d => stable with { MemoryOffsetMHz = stable.MemoryOffsetMHz + d },
            [TuningPlan.MemoryStep], Math.Min(TuningPlan.MemoryLimit, limits.MaxMemoryOffsetMHz) - stable.MemoryOffsetMHz) { Goal = TuningGoal.Memory };

    /// <summary>Arrondi au cran de 15 MHz (inférieur, ou supérieur si demandé).</summary>
    private static int Bin(double mhz, bool up = false) =>
        (int)((up ? Math.Ceiling(mhz / TuningPlan.ClockBin) : Math.Floor(mhz / TuningPlan.ClockBin)) * TuningPlan.ClockBin);

}

/// <summary>Réglages communs de l'assistant.</summary>
public static class TuningPlan
{
    public const int CoreLimit = 300, MemoryStep = 250, MemoryLimit = 1000, ClockBin = 15;
    /// <summary>Plafond le plus bas proposé par Économie (part du plafond d'Équilibre) : 15 % de fréquence en moins au plus.</summary>
    public const double EconomyFloor = 0.85;

    /// <summary>Le point de départ : le réglage d'origine (avant Aria), sans « au démarrage ».</summary>
    public static GpuTuningState Start(GpuTuningState origin) => origin with { AtStartup = false };

    /// <summary>
    /// Plafond d'Équilibre : la fréquence haute que la carte atteint régulièrement en jeu au départ (90e centile),
    /// arrondie au cran de 15 MHz inférieur.
    /// null si la mesure de départ ne permet pas de la connaître.
    /// </summary>
    public static uint? BalancedCap(PerformanceStats? baseline, GpuTuningLimits limits)
    {
        if (baseline is null || baseline.ActiveSeconds < PerformanceAnalyzer.MinActiveSeconds || baseline.SustainedClock <= 0) return null;
        var cap = (uint)(Math.Floor(baseline.SustainedClock / ClockBin) * ClockBin);
        return cap <= limits.MinClockMHz ? null : Math.Min(cap, limits.MaxClockMHz);
    }

    /// <summary>Écart lisible entre deux mesures : « -38 W », « +2 °C », « +0 MHz ».</summary>
    public static string Delta(double after, double before, string unit) => $"{after - before:+0;-0;+0} {unit}";
}
