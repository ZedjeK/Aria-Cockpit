namespace Aria.Bench.Stability;

/// <summary>
/// Ce que la carte a fait pendant le test des transitions, relevé toutes les 100 ms : états de performance traversés,
/// plages de fréquence du cœur et de la mémoire, nombre de changements d'état ou de fréquence mémoire. C'est la preuve
/// que le test a bien fait bouger la carte : sans elle, il ne dirait rien des transitions. Fonction pure (testable).
/// </summary>
internal sealed class TransitionTrace
{
    /// <summary>Plus haut numéro d'état de performance valide (P0 à P15) ; NVML en donne 32 quand il ne sait pas.</summary>
    public const uint LastState = 15;

    private uint? _lastState;
    private uint? _lastMemory;

    public SortedSet<uint> States { get; } = [];
    public int Samples { get; private set; }
    public int Changes { get; private set; }
    public uint CoreMin { get; private set; } = uint.MaxValue;
    public uint CoreMax { get; private set; }
    public uint MemoryMin { get; private set; } = uint.MaxValue;
    public uint MemoryMax { get; private set; }

    /// <summary>Un relevé : état de performance (0 à 15), fréquence du cœur et de la mémoire en MHz. Un état inconnu est ignoré.</summary>
    public void Add(uint state, uint coreMHz, uint memoryMHz)
    {
        if (state > LastState) return;
        Samples++;
        States.Add(state);
        CoreMin = Math.Min(CoreMin, coreMHz);
        CoreMax = Math.Max(CoreMax, coreMHz);
        MemoryMin = Math.Min(MemoryMin, memoryMHz);
        MemoryMax = Math.Max(MemoryMax, memoryMHz);
        if (_lastState is { } s && (s != state || _lastMemory != memoryMHz)) Changes++;
        _lastState = state;
        _lastMemory = memoryMHz;
    }

    /// <summary>« P2/P3/P5 » ; vide si rien n'a été relevé.</summary>
    public string StatesText => string.Join("/", States.Select(s => "P" + s));

    public string? CoreRange => Samples == 0 ? null : $"{CoreMin}-{CoreMax}";
    public string? MemoryRange => Samples == 0 ? null : $"{MemoryMin}-{MemoryMax}";
}
