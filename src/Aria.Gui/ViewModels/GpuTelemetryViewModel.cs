using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Aria.Core.Nvidia;

namespace Aria.Gui.ViewModels;

/// <summary>Une mesure du bandeau, mise à jour sur place pour éviter tout clignotement.</summary>
public sealed partial class LiveReadout(string key) : ObservableObject
{
    public string Key { get; } = key;
    [ObservableProperty] private string label = "";
    [ObservableProperty] private string value = "";
    [ObservableProperty] private string unit = "";
    [ObservableProperty] private string detail = "";
    [ObservableProperty] private double ratio;
    [ObservableProperty] private bool hot;

    public void Update(GpuReadout r)
    {
        Label = r.Label;
        Value = r.Value;
        Unit = r.Unit;
        Detail = r.Detail;
        Ratio = r.Ratio;
        Hot = r.Hot;
    }
}

/// <summary>
/// Bandeau « Télémétrie GPU » de Mon PC : une lecture par seconde, seulement quand la page est affichée.
/// Masqué sans carte NVIDIA (ou si le pilote ne fournit pas NVML).
/// </summary>
public sealed partial class GpuTelemetryViewModel : ObservableObject
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private GpuTelemetry? _telemetry;
    private bool _reading, _active;

    [ObservableProperty] private bool isAvailable;

    public ObservableCollection<LiveReadout> Readouts { get; } = [];

    public GpuTelemetryViewModel() => _timer.Tick += async (_, _) => await TickAsync();

    /// <summary>La page devient visible : première lecture tout de suite, puis chaque seconde.</summary>
    public async void Start()
    {
        _active = true;
        _telemetry = await GpuTelemetry.OpenSharedAsync();
        if (_telemetry is null || !_active) return;
        await TickAsync();
        if (_active) _timer.Start();
    }

    /// <summary>La page est quittée : plus aucune lecture.</summary>
    public void Stop()
    {
        _active = false;
        _timer.Stop();
    }

    private async Task TickAsync()
    {
        if (_telemetry is not { } telemetry || _reading) return;
        _reading = true;
        try
        {
            var sample = await Task.Run(telemetry.Read);
            Apply(GpuTelemetry.Describe(sample));
        }
        finally
        {
            _reading = false;
        }
    }

    private void Apply(IReadOnlyList<GpuReadout> readouts)
    {
        // Même liste de mesures que la seconde précédente : mise à jour sur place
        if (!Readouts.Select(r => r.Key).SequenceEqual(readouts.Select(r => r.Key)))
        {
            Readouts.Clear();
            foreach (var r in readouts) Readouts.Add(new LiveReadout(r.Key));
        }
        for (var i = 0; i < readouts.Count; i++) Readouts[i].Update(readouts[i]);
        IsAvailable = readouts.Count > 0;
    }
}
