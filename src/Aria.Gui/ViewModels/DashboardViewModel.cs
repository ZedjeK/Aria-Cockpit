using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Aria.Core.Diagnostics.Checks;
using Aria.Core.Models;
using Aria.Core.Reporting;
using Aria.Gui.Services;
using Wpf.Ui.Controls;

namespace Aria.Gui.ViewModels;

/// <summary>Niveau du score de conformité, qui fixe la couleur de l'anneau.</summary>
public enum ScoreLevel { Low, Medium, High }

/// <summary>Une carte de la section Matériel. <see cref="Label"/> : son repère dans le cockpit (« 01 · PROCESSEUR »).</summary>
public sealed record HardwareCard(string Title, SymbolRegular Icon, string Main, IReadOnlyList<string> Details, string Label = "");

/// <summary>Un point du diagnostic, avec de quoi le déplier.</summary>
public sealed record FindingItem(string Title, string Detail, string? Advice, IReadOnlyList<string> Steps, Severity Severity)
{
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasAdvice => !string.IsNullOrWhiteSpace(Advice);
    public bool HasSteps => Steps.Count > 0;

    /// <summary>Les points à corriger pulsent pour attirer l'œil.</summary>
    public bool IsUrgent => Severity >= Severity.Warning;

    public SymbolRegular Icon => Severity switch
    {
        Severity.Critical => SymbolRegular.ErrorCircle24,
        Severity.Warning => SymbolRegular.Warning24,
        Severity.Info => SymbolRegular.Info24,
        _ => SymbolRegular.CheckmarkCircle24
    };
}

public sealed partial class DashboardViewModel : ObservableObject
{
    private readonly AppState _state;

    [ObservableProperty] private string summary = "";
    [ObservableProperty] private string pcName = Environment.MachineName;
    [ObservableProperty] private string systemLine = "";
    [ObservableProperty] private string statusLine = "";
    [ObservableProperty] private int toFixCount;
    [ObservableProperty] private int infoCount;
    [ObservableProperty] private int conformCount;
    [ObservableProperty] private double conformRatio;
    [ObservableProperty] private string conformTotalText = "/–";
    [ObservableProperty] private ScoreLevel scoreLevel = ScoreLevel.Medium;
    [ObservableProperty] private IReadOnlyList<HardwareCard> hardware = [];
    [ObservableProperty] private IReadOnlyList<HardwareCard> leftHardware = [];
    [ObservableProperty] private IReadOnlyList<HardwareCard> rightHardware = [];
    [ObservableProperty] private string toFixPreview = "";
    [ObservableProperty] private string infoPreview = "";
    [ObservableProperty] private string conformPreview = "";
    [ObservableProperty] private IReadOnlyList<FindingItem> attention = [];
    [ObservableProperty] private IReadOnlyList<FindingItem> conform = [];
    [ObservableProperty] private bool hasAttention;
    [ObservableProperty] private bool showBiosAccess;
    [ObservableProperty] private string biosAccessText = "";
    [ObservableProperty] private bool canRestartToBios;
    [ObservableProperty] private string detectionWarnings = "";
    [ObservableProperty] private bool hasDetectionWarnings;
    [ObservableProperty] private string error = "";
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private string telemetryTitle = "TÉLÉMÉTRIE GPU · EN DIRECT";

    /// <summary>Bandeau de télémétrie en direct, démarré et arrêté par la page.</summary>
    public GpuTelemetryViewModel Telemetry { get; } = new();

    public DashboardViewModel(AppState state)
    {
        _state = state;
        _state.PropertyChanged += OnStateChanged;
        Refresh();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppState.Analysis) or nameof(AppState.Error)) Refresh();
        if (e.PropertyName == nameof(AppState.HidePersonalInfo)) OnPropertyChanged(nameof(HidePersonalInfo));
    }

    [RelayCommand]
    private Task Reanalyze() => _state.AnalyzeAsync();

    /// <summary>Choix partagé avec la page Rapports.</summary>
    public bool HidePersonalInfo
    {
        get => _state.HidePersonalInfo;
        set => _state.HidePersonalInfo = value;
    }

    [RelayCommand]
    private Task ExportHtml() => SheetExporter.ExportAsync(_state);

    [RelayCommand]
    private async Task RestartToBios()
    {
        if (await DialogService.Current.ConfirmAsync(new DialogRequest(
                Title: "Redémarrer dans le BIOS",
                ConfirmText: "Redémarrer",
                Message: "Le PC va redémarrer dans 10 secondes et s'ouvrir directement sur le BIOS.",
                Warnings: ["Enregistrez votre travail avant de continuer."],
                Note: "Aria ne modifie jamais le BIOS : suivez les étapes indiquées dans le diagnostic.",
                IsRisky: true)))
            RestartService.Restart(intoBios: true);
    }

    private void Refresh()
    {
        Error = _state.Error ?? "";
        HasError = _state.Error is not null;

        if (_state.Analysis is not { } analysis) return;
        var hw = analysis.Hardware;
        TelemetryTitle = hw.NvidiaGpu is { } nvidia
            ? $"TÉLÉMÉTRIE GPU · {Clean(nvidia.Name).Replace("NVIDIA ", "").ToUpperInvariant()} · EN DIRECT"
            : "TÉLÉMÉTRIE GPU · EN DIRECT";

        // Cockpit : les cartes numérotées, la moitié de chaque côté de la jauge
        Hardware = BuildCards(analysis).Select((c, i) => c with { Label = $"{i + 1:00} · {c.Title.ToUpperInvariant()}" }).ToList();
        var half = (Hardware.Count + 1) / 2;
        LeftHardware = Hardware.Take(half).ToList();
        RightHardware = Hardware.Skip(half).ToList();

        var findings = analysis.Findings.Select(f => new FindingItem(f.Title, f.Detail, f.Advice, f.Steps ?? [], f.Severity)).ToList();
        Attention = findings.Where(f => f.Severity != Severity.Ok).OrderByDescending(f => f.Severity).ToList();
        Conform = findings.Where(f => f.Severity == Severity.Ok).ToList();
        HasAttention = Attention.Count > 0;
        ToFixPreview = Preview(Attention.Where(f => f.Severity >= Severity.Warning), "Rien à corriger");
        InfoPreview = Preview(Attention.Where(f => f.Severity < Severity.Warning), "Aucune remarque");
        ConformPreview = Preview(Conform, "Aucun point conforme");

        var toFix = Attention.Count(f => f.Severity >= Severity.Warning);
        var info = Attention.Count - toFix;
        Summary = $"Analysé à {analysis.At:HH:mm} · {toFix} à corriger · {info} pour information · {Conform.Count} conformes";

        // Bandeau d'accueil : un vrai décompte des points du diagnostic, pas un score inventé
        ToFixCount = toFix;
        InfoCount = info;
        ConformCount = Conform.Count;
        var total = findings.Count;
        ConformRatio = total == 0 ? 0 : (double)Conform.Count / total;
        ConformTotalText = $"/{total}";
        // Couleur de l'anneau, en proportion du total (sur 12 points : 0-4 rouge, 5-8 orange, 9+ vert)
        ScoreLevel = ConformRatio <= 1.0 / 3 ? ScoreLevel.Low : ConformRatio <= 2.0 / 3 ? ScoreLevel.Medium : ScoreLevel.High;
        StatusLine = toFix > 0
            ? $"{toFix} POINT{(toFix > 1 ? "S" : "")} À CORRIGER · ANALYSÉ À {analysis.At:HH:mm}"
            : $"SYSTÈME OPÉRATIONNEL · ANALYSÉ À {analysis.At:HH:mm}";
        var board = analysis.Bios.IsGeneric ? hw.Board.Manufacturer : analysis.Bios.Brand.Brand;
        SystemLine = $"{hw.Os.Name} {hw.Os.DisplayVersion} · {board} {hw.Board.Product}";

        ShowBiosAccess = analysis.Findings.Any(f => f.NeedsBios);
        var brand = analysis.Bios.Brand;
        BiosAccessText = $"Carte mère {(analysis.Bios.IsGeneric ? hw.Board.Manufacturer : brand.Brand)} : touche {brand.SetupKey} au démarrage" +
                         (brand.AdvancedModeKey is { } key ? $", puis {key} pour le mode avancé." : ".");
        CanRestartToBios = hw.Firmware.BootMode == BootMode.Uefi;

        DetectionWarnings = string.Join("\n", hw.Warnings);
        HasDetectionWarnings = hw.Warnings.Count > 0;
    }

    private static IReadOnlyList<HardwareCard> BuildCards(AnalysisResult analysis)
    {
        var hw = analysis.Hardware;
        var cards = new List<HardwareCard>();

        // Processeur
        var cpu = hw.Cpu;
        var cpuDetails = new List<string>
        {
            cpu.IsHybrid
                ? $"{cpu.PerformanceCores} P-cores + {cpu.EfficientCores} E-cores · {cpu.Threads} threads"
                : $"{cpu.Cores} cœurs · {cpu.Threads} threads"
        };
        if (cpu.MaxClockMHz > 0) cpuDetails.Add($"Fréquence de base {cpu.MaxClockMHz / 1000.0:0.0#} GHz");
        if (cpu.Microcode is { } microcode) cpuDetails.Add($"Microcode 0x{microcode:X}");
        cards.Add(new("Processeur", SymbolRegular.DeveloperBoard24, Clean(cpu.Name), cpuDetails));

        // Carte graphique : la NVIDIA en vedette, les autres en complément
        var main = hw.NvidiaGpu ?? hw.Gpus.FirstOrDefault();
        var gpuDetails = new List<string>();
        if (main is not null)
        {
            if (!main.IsIntegrated) gpuDetails.Add($"{Format.Gb(main.VramBytes)} de mémoire vidéo");
            gpuDetails.Add($"Pilote {main.VendorDriverVersion ?? main.DriverVersion}");
            if (main == hw.NvidiaGpu && hw.NvidiaLink is { } link)
                gpuDetails.Add($"PCIe Gen{link.PcieGenMax} x{link.PcieWidthMax} · Resizable BAR {(link.Bar1TotalMiB > 256 ? "actif" : "inactif")}");
        }
        gpuDetails.AddRange(hw.Gpus.Where(g => g != main).Select(g => $"Aussi : {Clean(g.Name)}{(g.IsIntegrated ? " (intégrée)" : "")}"));
        cards.Add(new("Carte graphique", SymbolRegular.Games24, main is null ? "Non détectée" : Clean(main.Name), gpuDetails));

        // Mémoire
        var mem = hw.Memory;
        var memDetails = new List<string> { $"{mem.Modules.Count} barrette(s) · {mem.ConfiguredSpeedMTs} MT/s" };
        if (mem.Modules.FirstOrDefault() is { } module) memDetails.Add($"{module.Manufacturer} {module.PartNumber}".Trim());
        cards.Add(new("Mémoire", SymbolRegular.Ram20, $"{Format.Gb(mem.TotalBytes)} {mem.Generation}", memDetails));

        // Écrans
        var screens = hw.Displays.OrderByDescending(d => d.IsPrimary).ToList();
        cards.Add(new(screens.Count > 1 ? "Écrans" : "Écran", SymbolRegular.Desktop24,
            screens.FirstOrDefault()?.MonitorName ?? "Non détecté",
            screens.SelectMany(d =>
            {
                var prefix = screens.Count > 1 ? d.MonitorName + " : " : "";
                var lines = new List<string> { $"{prefix}{d.Width}x{d.Height} · {d.CurrentRefreshHz} Hz (max {d.MaxRefreshHz})" };
                var link = new[]
                {
                    DisplayLinkCheck.ConnectionName(d.Connection),
                    d.HdrEnabled == true ? "HDR actif" : d.HdrSupported == true ? "HDR disponible" : null
                }.Where(x => x is not null).ToList();
                if (link.Count > 0) lines.Add(string.Join(" · ", link));
                lines.Add($"Branché sur {Clean(d.AdapterName)}");
                return lines;
            }).ToList()));

        // Carte mère et firmware
        var fw = hw.Firmware;
        var brand = analysis.Bios.IsGeneric ? hw.Board.Manufacturer : analysis.Bios.Brand.Brand;
        cards.Add(new("Carte mère", SymbolRegular.Board24, $"{brand} {hw.Board.Product}",
        [
            hw.Board.BiosDate is { } date ? $"BIOS {hw.Board.BiosVersion} du {date:dd/MM/yyyy}" : $"BIOS {hw.Board.BiosVersion}",
            $"{(fw.BootMode == BootMode.Uefi ? "UEFI" : fw.BootMode == BootMode.Legacy ? "Ancien mode (Legacy)" : "Démarrage ?")}" +
            $" · Secure Boot {State(fw.SecureBoot)} · TPM {(fw.TpmReady == true ? fw.TpmVersion ?? "actif" : State(fw.TpmReady))}"
        ]));

        // Stockage, avec la santé des SSD
        var disks = hw.Disks;
        var total = disks.Aggregate(0UL, (sum, d) => sum + d.SizeBytes);
        cards.Add(new("Stockage", SymbolRegular.Storage24,
            disks.Count == 0 ? "Non détecté" : $"{disks.Count} disque(s) · {Format.DiskSize(total)}",
            disks.SelectMany(d =>
            {
                var letters = d.DriveLetters.Count > 0 ? string.Join(" ", d.DriveLetters.Select(l => $"{l}:")) + " " : "";
                var lines = new List<string> { $"{letters}{d.Model} · {Media(d)} · {Format.DiskSize(d.SizeBytes)}" };
                var health = SsdHealthCheck.Describe(d);
                if (health.Length > 0) lines.Add($"   {health}");
                return lines;
            }).ToList()));

        // Réseau
        var net = hw.Network;
        var first = net.FirstOrDefault();
        cards.Add(new("Réseau", SymbolRegular.Wifi124,
            first is null ? "Non connecté" : $"{(first.Kind == NetworkKind.Ethernet ? "Ethernet" : "Wi-Fi")} · {NetworkCheck.Speed(first.SpeedBps)}",
            net.SelectMany(a =>
            {
                var lines = new List<string> { a.Description };
                if (a.Kind == NetworkKind.WiFi)
                {
                    var wifi = string.Join(" · ", new[] { a.WifiStandard, a.WifiSignalPercent is { } p ? $"signal {p} %" : null }.Where(x => x is not null));
                    if (wifi.Length > 0) lines.Add(wifi);
                }
                if (a != first) lines.Insert(0, $"Aussi : {(a.Kind == NetworkKind.Ethernet ? "Ethernet" : "Wi-Fi")} · {NetworkCheck.Speed(a.SpeedBps)}");
                return lines;
            }).ToList()));

        // Système
        var systemDetails = new List<string> { $"Build {hw.Os.Build} · {(hw.IsLaptop ? "PC portable" : "PC fixe")}" };
        if (hw.SystemState.SystemProtection is { } protection)
            systemDetails.Add($"Points de restauration : {(protection ? "actifs" : "désactivés")}");
        if (hw.SystemState.PendingReboot == true) systemDetails.Add("Redémarrage en attente");
        if (hw.Battery is { } battery) systemDetails.Add($"Batterie : {battery.HealthPercent} % de sa capacité d'origine");
        if (hw.Peripherals.GameControllers.Count > 0) systemDetails.Add($"Manette : {hw.Peripherals.GameControllers[0]}");
        cards.Add(new("Système", SymbolRegular.Window24, $"{hw.Os.Name} {hw.Os.DisplayVersion}".Trim(), systemDetails));

        return cards;
    }

    /// <summary>Aperçu d'un bloc du cockpit : les deux premiers titres, ou un message si la liste est vide.</summary>
    private static string Preview(IEnumerable<FindingItem> items, string empty)
    {
        var titles = items.Select(f => f.Title).ToList();
        return titles.Count switch
        {
            0 => empty,
            <= 2 => string.Join(" · ", titles),
            _ => string.Join(" · ", titles.Take(2)) + $" · +{titles.Count - 2}"
        };
    }

    /// <summary>"12th Gen Intel(R) Core(TM) i7-12700K" → "12th Gen Intel Core i7-12700K".</summary>
    private static string Clean(string name) => name.Replace("(R)", "").Replace("(TM)", "").Replace("  ", " ").Trim();

    private static string State(bool? value) => value switch { true => "actif", false => "inactif", null => "?" };

    private static string Media(DiskInfo d) => (d.Media, d.Bus) switch
    {
        (_, StorageBus.NVMe) => "SSD NVMe",
        (StorageMedia.Ssd, _) => "SSD",
        (StorageMedia.Hdd, _) => "Disque dur",
        (_, StorageBus.Usb) => "USB",
        _ => "Disque"
    };
}
