using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Nvidia;
using Aria.Core.Performance;
using Aria.Gui.Services;

namespace Aria.Gui.ViewModels;

/// <summary>Un chiffre clé du rapport, avec la valeur de l'analyse précédente pour comparer.</summary>
public sealed record StatTile(string Label, string Value, string Unit, string Before)
{
    public bool HasBefore => Before.Length > 0;
}

/// <summary>
/// Analyse de performance : 5 minutes d'enregistrement pendant que l'utilisateur joue, puis un verdict.
/// Continue même si l'on quitte la page Mon PC. Ne modifie rien.
/// </summary>
public sealed partial class PerformanceViewModel : ObservableObject
{
    private const string NvidiaAppPage = "https://www.nvidia.com/fr-fr/software/nvidia-app/";

    private CancellationTokenSource? _stop;
    private PerformanceReport? _previous;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool isRunning;
    [ObservableProperty] private double progress;
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private bool hasReport;
    [ObservableProperty] private string reportTitle = "";
    [ObservableProperty] private string headline = "";
    [ObservableProperty] private bool isConclusive;
    [ObservableProperty] private IReadOnlyList<StatTile> tiles = [];
    [ObservableProperty] private IReadOnlyList<FindingItem> findings = [];

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task Start()
    {
        if (await GpuTelemetry.OpenSharedAsync() is not { } gpu) return;
        _stop = new CancellationTokenSource();
        IsRunning = true;
        Progress = 0;
        UpdateProgress(TimeSpan.Zero);
        try
        {
            var report = await PerformanceSession.RunAsync(gpu, PerformanceSession.DefaultDuration,
                new Progress<TimeSpan>(UpdateProgress), _stop.Token);
            Show(report);
        }
        finally
        {
            IsRunning = false;
            _stop.Dispose();
            _stop = null;
        }
    }

    private bool CanStart() => !IsRunning;

    /// <summary>Arrêt anticipé : le verdict porte sur ce qui a déjà été enregistré.</summary>
    [RelayCommand]
    private void StopNow() => _stop?.Cancel();

    /// <summary>NVIDIA App (son Réglage automatique), ou sa page de téléchargement si elle n'est pas installée.</summary>
    [RelayCommand]
    private static void OpenNvidiaApp() => Shell.OpenUnelevated(FindNvidiaApp() ?? NvidiaAppPage);

    private void UpdateProgress(TimeSpan elapsed)
    {
        var total = PerformanceSession.DefaultDuration;
        Progress = Math.Clamp(elapsed / total, 0, 1);
        var left = total - elapsed;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        ProgressText = $"ANALYSE EN COURS · {left:m\\:ss} RESTANTES";
    }

    private void Show(PerformanceReport report)
    {
        var before = _previous is { Conclusive: true } ? _previous.Stats : null;
        var s = report.Stats;
        ReportTitle = $"ANALYSE DE PERFORMANCE · {report.At:HH:mm} · {s.ActiveSeconds} S DE JEU SUR {s.Seconds} S";
        Headline = report.Headline;
        IsConclusive = report.Conclusive;
        Tiles = report.Conclusive
            ?
            [
                new("CHARGE GPU MOY.", $"{s.AvgLoad:0}", "%", Before(before, b => $"{b.AvgLoad:0} %")),
                new("FRÉQUENCE MOY.", $"{s.AvgClock:0}", "MHz", Before(before, b => $"{b.AvgClock:0} MHz")),
                new("TEMPÉRATURE MAX", $"{s.MaxTemp}", "°C", Before(before, b => $"{b.MaxTemp} °C")),
                new("CONSO. MOY.", s.AvgPower is { } p ? $"{p:0}" : "–", "W", Before(before, b => b.AvgPower is { } bp ? $"{bp:0} W" : "")),
                new("MÉMOIRE VIDÉO MAX", s.MaxVramBytes is { } v ? Gb(v) : "–", "Go", Before(before, b => b.MaxVramBytes is { } bv ? $"{Gb(bv)} Go" : "")),
                new("CŒUR CPU LE + CHARGÉ", s.AvgCpuBusiestCore is { } c ? $"{c:0}" : "–", "%", Before(before, b => b.AvgCpuBusiestCore is { } bc ? $"{bc:0} %" : ""))
            ]
            : [];
        Findings = report.Findings.Select(f => new FindingItem(f.Title, f.Detail, f.Advice, [], f.Severity)).ToList();
        HasReport = true;
        if (report.Conclusive) _previous = report;
    }

    private static string Before(PerformanceStats? before, Func<PerformanceStats, string> text) =>
        before is null ? "" : text(before) is { Length: > 0 } t ? $"avant : {t}" : "";

    private static string Gb(ulong bytes) => (bytes / 1073741824.0).ToString("0.0", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"));

    /// <summary>Exécutable de NVIDIA App, sinon son raccourci du menu Démarrer ; null si elle n'est pas installée.</summary>
    private static string? FindNvidiaApp()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "NVIDIA Corporation", "NVIDIA App", "CEF", "NVIDIA App.exe");
        if (File.Exists(exe)) return exe;
        try
        {
            var programs = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            return Directory.Exists(programs)
                ? Directory.EnumerateFiles(programs, "NVIDIA App*.lnk", SearchOption.AllDirectories).FirstOrDefault()
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
