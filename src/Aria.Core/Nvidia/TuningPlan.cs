using Aria.Core.Performance;

namespace Aria.Core.Nvidia;

/// <summary>Objectif de l'assistant guidé. Équilibre passe en premier : son résultat sert de base aux deux autres.</summary>
public enum TuningGoal
{
    /// <summary>Undervolt : plafond à la fréquence haute habituelle en jeu + décalage GPU croissant. Mêmes FPS, moins de watts.</summary>
    Balanced,
    /// <summary>
    /// À partir d'Équilibre : plafond plus bas, palier par palier, jusqu'à la perte jugée acceptable. Moins de fréquence,
    /// c'est moins de tension : les watts et la chaleur baissent avec, et la perte affichée les compte. Sans plafond
    /// possible (Maxwell, Pascal), c'est la limite de puissance qui baisse. Le plafond garde une fréquence régulière ; une
    /// limite de watts garantirait une consommation maximale, mais ferait varier la fréquence dans les scènes lourdes.
    /// </summary>
    Economy,
    /// <summary>
    /// À partir d'Équilibre : limite de puissance au maximum dès le départ, sans plafond, décalage GPU croissant. La limite
    /// n'est qu'un plafond de consommation : la monter ne rend pas la carte instable, elle la laisse tenir sa fréquence
    /// (Équilibre reste souvent bridé par ses watts). C'est le décalage qui se cherche par paliers ; les watts en plus
    /// servent ensuite de juge, dans l'avis d'Aria (gain de FPS contre watts et degrés en plus).
    /// </summary>
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
    public GpuTuningState Safe => _backoff > 0 && (_fail != int.MaxValue || AlwaysBackOff) ? _at(Math.Max(0, _stable - _backoff)) : LastStable;

    /// <summary>
    /// Recul d'un cran même sans palier instable (marge pour l'été, la poussière, le vieillissement, et ce que les
    /// contrôles n'ont pas vu en quelques minutes) : 15 MHz pour le décalage GPU d'Équilibre et de Performance, 250 MHz pour la mémoire.
    /// </summary>
    public bool AlwaysBackOff { get; private init; }

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
        { Goal = TuningGoal.Balanced, AlwaysBackOff = true };

    /// <summary>
    /// Équilibre sans plafond (cartes Maxwell et Pascal) : limite de puissance calée sur la consommation mesurée au départ,
    /// puis décalage GPU croissant : plus de fréquence pour les mêmes watts, qu'Économie pourra ensuite échanger contre des watts.
    /// </summary>
    public static TuningSearch BalancedByPower(GpuTuningState start, GpuTuningLimits limits, uint powerMilliwatts)
    {
        var capped = start with { MaxClockMHz = 0, PowerLimitMilliwatts = powerMilliwatts };
        return new TuningSearch(d => capped with { CoreOffsetMHz = start.CoreOffsetMHz + d },
            [100, 30, 15], Math.Min(TuningPlan.CoreLimit, limits.MaxCoreOffsetMHz) - start.CoreOffsetMHz, TuningPlan.ClockBin)
        { Goal = TuningGoal.Balanced, AlwaysBackOff = true };
    }

    /// <summary>
    /// Économie : à partir du réglage d'Équilibre, le plafond baisse par pas d'environ 5 %, puis 2,5 %, puis 15 MHz,
    /// sans descendre sous 85 % du plafond d'Équilibre. « Stable » veut dire ici : perte jugée acceptable.
    /// </summary>
    public static TuningSearch Economy(GpuTuningState balanced, GpuTuningLimits limits)
    {
        // Sans plafond (Maxwell, Pascal) : c'est la limite de puissance qui baisse (≈ 10 %, 5 %, puis 5 W, jusqu'à 60 %)
        if (balanced.MaxClockMHz == 0 || !limits.SupportsClockLock)
        {
            var big = Round5W(limits.DefaultPowerMilliwatts * 0.10);
            var medium = Round5W(limits.DefaultPowerMilliwatts * 0.05);
            var powerFloor = Math.Max((int)limits.MinPowerMilliwatts, Round5W(limits.DefaultPowerMilliwatts * TuningPlan.PowerFloor));
            var max = limits.SupportsPowerLimit ? (int)balanced.PowerLimitMilliwatts - powerFloor : 0;
            return new TuningSearch(d => balanced with { PowerLimitMilliwatts = (uint)(balanced.PowerLimitMilliwatts - (uint)d) },
                [big, medium, 5000], max) { Goal = TuningGoal.Economy };
        }
        var cap = balanced.MaxClockMHz != 0 ? balanced.MaxClockMHz : limits.MaxClockMHz;
        var floor = Math.Max((int)limits.MinClockMHz + TuningPlan.ClockBin, Bin(cap * TuningPlan.EconomyFloor, up: true));
        return new TuningSearch(d => balanced with { MaxClockMHz = (uint)(cap - d) },
            [Math.Max(TuningPlan.ClockBin, Bin(cap * 0.05)), Math.Max(TuningPlan.ClockBin, Bin(cap * 0.025)), TuningPlan.ClockBin],
            (int)cap - floor) { Goal = TuningGoal.Economy };
    }

    /// <summary>
    /// Performance : réglage de départ = courbe d'usine (décalage GPU nul), sans plafond, limite de puissance au maximum de la carte
    /// (à mesurer d'abord) ; puis décalage croissant par pas de 60, 30 puis 15 MHz ; recul de 15 MHz au bord.
    /// Le décalage d'Équilibre ne sert pas de départ : le plafond ne l'a éprouvé qu'en bas de la courbe, pas aux fréquences hautes.
    /// </summary>
    public static TuningSearch Performance(GpuTuningState balanced, GpuTuningLimits limits)
    {
        var start = PerformanceStart(balanced, limits);
        return new TuningSearch(d => start with { CoreOffsetMHz = start.CoreOffsetMHz + d },
            [60, 30, 15], Math.Min(TuningPlan.CoreLimit, limits.MaxCoreOffsetMHz) - start.CoreOffsetMHz, TuningPlan.ClockBin)
        { Goal = TuningGoal.Performance, AlwaysBackOff = true };
    }

    /// <summary>Point de départ de Performance : courbe d'usine (décalage mémoire d'Équilibre gardé), sans plafond, puissance au maximum.</summary>
    public static GpuTuningState PerformanceStart(GpuTuningState balanced, GpuTuningLimits limits) =>
        balanced with { MaxClockMHz = 0, CoreOffsetMHz = 0, PowerLimitMilliwatts = limits.SupportsPowerLimit ? limits.MaxPowerMilliwatts : balanced.PowerLimitMilliwatts };

    /// <summary>
    /// Décalage mémoire par paliers fixes de 250 MHz jusqu'à +1000 : au premier problème, on s'arrête, et le résultat
    /// garde toujours un cran de marge (+1000 tenu → +750 retenu).
    /// </summary>
    public static TuningSearch Memory(GpuTuningState stable, GpuTuningLimits limits) =>
        new(d => stable with { MemoryOffsetMHz = stable.MemoryOffsetMHz + d },
            [TuningPlan.MemoryStep], Math.Min(TuningPlan.MemoryLimit, limits.MaxMemoryOffsetMHz) - stable.MemoryOffsetMHz, TuningPlan.MemoryStep)
        { Goal = TuningGoal.Memory, AlwaysBackOff = true };

    private static int Round5W(double milliwatts) => Math.Max(5000, (int)Math.Round(milliwatts / 5000.0) * 5000);

    /// <summary>Arrondi au cran de 15 MHz (inférieur, ou supérieur si demandé).</summary>
    private static int Bin(double mhz, bool up = false) =>
        (int)((up ? Math.Ceiling(mhz / TuningPlan.ClockBin) : Math.Floor(mhz / TuningPlan.ClockBin)) * TuningPlan.ClockBin);

}

/// <summary>Réglages communs de l'assistant.</summary>
public static class TuningPlan
{
    public const int CoreLimit = 300, MemoryStep = 250, MemoryLimit = 1000, ClockBin = 15;
    /// <summary>Limite de puissance la plus basse proposée par Économie sur les cartes sans plafond (part de la valeur d'usine).</summary>
    public const double PowerFloor = 0.60;

    /// <summary>Plafond le plus bas proposé par Économie (part du plafond d'Équilibre) : 15 % de fréquence en moins au plus.</summary>
    public const double EconomyFloor = 0.85;

    /// <summary>Le point de départ : le réglage d'origine (valeurs d'usine), sans « au démarrage ».</summary>
    public static GpuTuningState Start(GpuTuningState origin) => origin with { AtStartup = false };

    /// <summary>
    /// Plafond d'Équilibre : la fréquence haute que la carte atteint régulièrement en jeu au départ (90e centile),
    /// arrondie au cran de 15 MHz inférieur.
    /// null si la mesure de départ ne permet pas de la connaître.
    /// </summary>
    public static uint? BalancedCap(PerformanceStats? baseline, GpuTuningLimits limits)
    {
        if (!limits.SupportsClockLock) return null;
        if (baseline is null || baseline.ActiveSeconds < PerformanceAnalyzer.MinActiveSeconds || baseline.SustainedClock <= 0) return null;
        var cap = (uint)(Math.Floor(baseline.SustainedClock / ClockBin) * ClockBin);
        return cap <= limits.MinClockMHz ? null : Math.Min(cap, limits.MaxClockMHz);
    }

    /// <summary>
    /// Limite de puissance d'Équilibre sur les cartes sans plafond : la consommation moyenne mesurée au départ,
    /// arrondie à 5 W, dans la plage de la carte. null si la limite n'est pas réglable ou la mesure inutilisable.
    /// </summary>
    public static uint? BalancedPower(PerformanceStats? baseline, GpuTuningLimits limits)
    {
        if (!limits.SupportsPowerLimit || baseline?.AvgPower is not { } watts || baseline.ActiveSeconds < PerformanceAnalyzer.MinActiveSeconds) return null;
        var mw = (uint)(Math.Round(watts / 5.0) * 5000);
        return Math.Clamp(mw, limits.MinPowerMilliwatts, limits.MaxPowerMilliwatts);
    }

    /// <summary>Écart lisible entre deux mesures : « -38 W », « +2 °C », « +0 MHz ».</summary>
    public static string Delta(double after, double before, string unit) => $"{after - before:+0;-0;+0} {unit}";
}
