using System.Diagnostics;

namespace Aria.Core.Performance;

/// <summary>Charge du processeur à un instant : moyenne de tous les cœurs et cœur le plus chargé (en %).</summary>
public sealed record CpuLoad(double Total, double BusiestCore);

/// <summary>
/// Charge du processeur via les compteurs de performance de Windows (« Processor Information »,
/// « % Processor Utility », la même mesure que le Gestionnaire des tâches).
/// Si les compteurs sont indisponibles (désactivés ou corrompus), les lectures renvoient null.
/// </summary>
public sealed class CpuLoadMonitor : IDisposable
{
    private const string Category = "Processor Information";
    private const string Counter = "% Processor Utility";

    private readonly PerformanceCounter? _total;
    private readonly List<PerformanceCounter> _cores = [];

    public CpuLoadMonitor()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var instances = new PerformanceCounterCategory(Category).GetInstanceNames();
            _total = new PerformanceCounter(Category, Counter, "_Total", readOnly: true);
            // Instances « 0,3 » : groupe, cœur logique ; « 0,_Total » regroupe un groupe entier
            foreach (var name in instances.Where(n => !n.Contains("_Total", StringComparison.OrdinalIgnoreCase)))
                _cores.Add(new PerformanceCounter(Category, Counter, name, readOnly: true));
            // La 1re lecture d'un compteur vaut toujours 0 : on l'amorce ici
            _total.NextValue();
            foreach (var core in _cores) core.NextValue();
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or FormatException)
        {
            Dispose();
            _total = null;
            _cores.Clear();
        }
    }

    /// <summary>Charge depuis la lecture précédente ; null si les compteurs ne sont pas lisibles.</summary>
    public CpuLoad? Read()
    {
        if (_total is null || _cores.Count == 0) return null;
        try
        {
            var total = Math.Clamp(_total.NextValue(), 0, 100);
            var busiest = _cores.Max(c => Math.Clamp(c.NextValue(), 0, 100));
            return new CpuLoad(total, busiest);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _total?.Dispose();
        foreach (var core in _cores) core.Dispose();
    }
}
