using Aria.Core.Performance;

namespace Aria.Core.Nvidia;

/// <summary>Objectif de l'assistant guidé.</summary>
public enum TuningGoal
{
    /// <summary>Moins de bruit et de chauffe : limite de puissance plus basse.</summary>
    Silence,
    /// <summary>Même fréquence avec moins de tension : plafond à la fréquence habituelle + décalage GPU croissant.</summary>
    Undervolt,
    /// <summary>Plus de fréquence : décalage GPU croissant, sans plafond.</summary>
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
    private readonly int _max;
    private int _stable, _fail = int.MaxValue, _candidate, _stage;

    /// <param name="at">Réglage correspondant à une distance depuis le départ (MHz de décalage, mW de puissance en moins).</param>
    /// <param name="steps">Pas de chaque étape (du plus grand au plus petit).</param>
    /// <param name="max">Distance maximale autorisée depuis le départ.</param>
    private TuningSearch(Func<int, GpuTuningState> at, int[] steps, int max)
    {
        _at = at;
        _steps = steps;
        _max = Math.Max(0, max);
        Advance();
    }

    public TuningGoal Goal { get; private init; }

    /// <summary>La recherche est terminée : <see cref="LastStable"/> est le résultat.</summary>
    public bool Finished { get; private set; }

    /// <summary>Palier à tester maintenant (sans objet si la recherche est terminée).</summary>
    public GpuTuningState Current => _at(_candidate);

    /// <summary>Dernier palier jugé stable (le départ tant qu'aucun palier n'a tenu).</summary>
    public GpuTuningState LastStable => _at(_stable);

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

    /// <summary>Décalage GPU croissant (undervolt avec plafond, ou performance sans plafond) : pas de 100, 30, puis 15 MHz.</summary>
    public static TuningSearch Core(TuningGoal goal, GpuTuningState start, GpuTuningLimits limits, uint maxClock) =>
        new(d => start with { MaxClockMHz = maxClock, CoreOffsetMHz = start.CoreOffsetMHz + d },
            [100, 30, 15], Math.Min(TuningPlan.CoreLimit, limits.MaxCoreOffsetMHz) - start.CoreOffsetMHz) { Goal = goal };

    /// <summary>Limite de puissance décroissante : pas d'environ 10 %, puis 5 % de la valeur d'usine, puis 5 W ; jusqu'à 60 %.</summary>
    public static TuningSearch Silence(GpuTuningState start, GpuTuningLimits limits)
    {
        var big = Round5W(limits.DefaultPowerMilliwatts * 0.10);
        var medium = Round5W(limits.DefaultPowerMilliwatts * 0.05);
        var floor = Math.Max((int)limits.MinPowerMilliwatts, Round5W(limits.DefaultPowerMilliwatts * TuningPlan.SilenceFloor));
        return new TuningSearch(d => start with { PowerLimitMilliwatts = (uint)(start.PowerLimitMilliwatts - (uint)d) },
            [big, medium, 5000], (int)start.PowerLimitMilliwatts - floor) { Goal = TuningGoal.Silence };
    }

    /// <summary>Décalage mémoire par paliers fixes de 250 MHz jusqu'à +1000 : au premier problème, on s'arrête.</summary>
    public static TuningSearch Memory(GpuTuningState stable, GpuTuningLimits limits) =>
        new(d => stable with { MemoryOffsetMHz = stable.MemoryOffsetMHz + d },
            [TuningPlan.MemoryStep], Math.Min(TuningPlan.MemoryLimit, limits.MaxMemoryOffsetMHz) - stable.MemoryOffsetMHz) { Goal = TuningGoal.Memory };

    private static int Round5W(double milliwatts) => Math.Max(5000, (int)Math.Round(milliwatts / 5000.0) * 5000);
}

/// <summary>Réglages communs de l'assistant.</summary>
public static class TuningPlan
{
    public const int CoreLimit = 300, MemoryStep = 250, MemoryLimit = 1000, ClockBin = 15;
    /// <summary>Limite de puissance la plus basse proposée par l'objectif Silence (part de la valeur d'usine).</summary>
    public const double SilenceFloor = 0.60;

    /// <summary>Le point de départ : le réglage d'origine (avant Aria), sans « au démarrage ».</summary>
    public static GpuTuningState Start(GpuTuningState origin) => origin with { AtStartup = false };

    /// <summary>
    /// Plafond de l'undervolt : la fréquence moyenne mesurée en jeu au départ, arrondie au cran de 15 MHz inférieur.
    /// null si la mesure de départ ne permet pas de la connaître.
    /// </summary>
    public static uint? UndervoltCap(PerformanceStats? baseline, GpuTuningLimits limits)
    {
        if (baseline is null || baseline.ActiveSeconds < PerformanceAnalyzer.MinActiveSeconds || baseline.AvgClock <= 0) return null;
        var cap = (uint)(Math.Floor(baseline.AvgClock / ClockBin) * ClockBin);
        return cap <= limits.MinClockMHz ? null : Math.Min(cap, limits.MaxClockMHz);
    }

    /// <summary>Écart lisible entre deux mesures : « -38 W », « +2 °C », « +0 MHz ».</summary>
    public static string Delta(double after, double before, string unit) => $"{after - before:+0;-0;+0} {unit}";
}
