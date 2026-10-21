using System.Globalization;

namespace Aria.Core.Nvidia;

/// <summary>
/// Réglage complet de la carte, appliqué d'un bloc (les 4 valeurs ne vont pas l'une sans l'autre pour un undervolt).
/// </summary>
/// <param name="PowerLimitMilliwatts">Limite de puissance (mW).</param>
/// <param name="MaxClockMHz">Plafond de fréquence graphique ; 0 = aucun plafond.</param>
/// <param name="CoreOffsetMHz">Décalage de la courbe fréquence/tension du GPU.</param>
/// <param name="MemoryOffsetMHz">Décalage de la fréquence mémoire.</param>
/// <param name="AtStartup">Réappliqué à chaque ouverture de session (sinon perdu au redémarrage).</param>
public sealed record GpuTuningState(uint PowerLimitMilliwatts, uint MaxClockMHz, int CoreOffsetMHz, int MemoryOffsetMHz, bool AtStartup)
{
    public double PowerLimitWatts => PowerLimitMilliwatts / 1000.0;

    /// <summary>Même effet sur la carte (sans tenir compte du démarrage).</summary>
    public bool SameTuning(GpuTuningState other) =>
        PowerLimitMilliwatts == other.PowerLimitMilliwatts && MaxClockMHz == other.MaxClockMHz
        && CoreOffsetMHz == other.CoreOffsetMHz && MemoryOffsetMHz == other.MemoryOffsetMHz;

    /// <summary>Valeur technique sauvegardée : « p=272000;c=0;o=0;m=0;b=0 ».</summary>
    public string ToRaw() => string.Create(CultureInfo.InvariantCulture,
        $"p={PowerLimitMilliwatts};c={MaxClockMHz};o={CoreOffsetMHz};m={MemoryOffsetMHz};b={(AtStartup ? 1 : 0)}");

    public static bool TryParse(string raw, out GpuTuningState state)
    {
        state = new GpuTuningState(0, 0, 0, 0, false);
        var parts = raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase);
        if (!parts.TryGetValue("p", out var p) || !uint.TryParse(p, CultureInfo.InvariantCulture, out var power) || power == 0) return false;
        uint clock = 0;
        int core = 0, memory = 0;
        if (parts.TryGetValue("c", out var c) && !uint.TryParse(c, CultureInfo.InvariantCulture, out clock)) return false;
        if (parts.TryGetValue("o", out var o) && !int.TryParse(o, CultureInfo.InvariantCulture, out core)) return false;
        if (parts.TryGetValue("m", out var m) && !int.TryParse(m, CultureInfo.InvariantCulture, out memory)) return false;
        state = new GpuTuningState(power, clock, core, memory, parts.TryGetValue("b", out var b) && b == "1");
        return true;
    }

    /// <summary>« 272 W · sans plafond · GPU +0 MHz · mémoire +0 MHz ».</summary>
    public string Describe() =>
        $"{PowerLimitWatts:0} W · {(MaxClockMHz == 0 ? "sans plafond" : $"plafond {MaxClockMHz} MHz")} · " +
        $"GPU {Signed(CoreOffsetMHz)} MHz · mémoire {Signed(MemoryOffsetMHz)} MHz" + (AtStartup ? " · au démarrage" : "");

    private static string Signed(int value) => value.ToString("+0;-0;+0", CultureInfo.InvariantCulture);
}

/// <summary>Plages acceptées par la carte (lues dans le pilote).</summary>
public sealed record GpuTuningLimits(
    uint MinPowerMilliwatts,
    uint MaxPowerMilliwatts,
    uint DefaultPowerMilliwatts,
    uint MinClockMHz,
    uint MaxClockMHz,
    int MinCoreOffsetMHz,
    int MaxCoreOffsetMHz,
    int MinMemoryOffsetMHz,
    int MaxMemoryOffsetMHz)
{
    /// <summary>null si le réglage tient dans les plages, sinon la raison du refus.</summary>
    public string? Check(GpuTuningState s)
    {
        if (s.PowerLimitMilliwatts < MinPowerMilliwatts || s.PowerLimitMilliwatts > MaxPowerMilliwatts)
            return $"Limite de puissance hors plage ({MinPowerMilliwatts / 1000} à {MaxPowerMilliwatts / 1000} W).";
        if (s.MaxClockMHz != 0 && (s.MaxClockMHz <= MinClockMHz || s.MaxClockMHz > MaxClockMHz))
            return $"Plafond de fréquence hors plage ({MinClockMHz} à {MaxClockMHz} MHz).";
        if (s.CoreOffsetMHz < MinCoreOffsetMHz || s.CoreOffsetMHz > MaxCoreOffsetMHz)
            return $"Décalage GPU hors plage ({MinCoreOffsetMHz} à {MaxCoreOffsetMHz} MHz).";
        if (s.MemoryOffsetMHz < MinMemoryOffsetMHz || s.MemoryOffsetMHz > MaxMemoryOffsetMHz)
            return $"Décalage mémoire hors plage ({MinMemoryOffsetMHz} à {MaxMemoryOffsetMHz} MHz).";
        return null;
    }
}
