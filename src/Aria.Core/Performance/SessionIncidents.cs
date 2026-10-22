using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using Aria.Core.Nvidia;

namespace Aria.Core.Performance;

/// <summary>Un plantage ou un gel du jeu noté par Windows (journal Application).</summary>
public sealed record GameIncident(DateTime At, bool IsHang);

/// <summary>Ce qui s'est mal passé pendant une analyse : plantages du pilote graphique, plantage ou gel du jeu.</summary>
public sealed record SessionIncidents(IReadOnlyList<DateTime> DriverCrashes, string? Game, IReadOnlyList<GameIncident> GameIncidents)
{
    public static readonly SessionIncidents None = new([], null, []);

    public bool Any => DriverCrashes.Count > 0 || GameIncidents.Count > 0;

    /// <summary>Lit les journaux de Windows pour la période de l'analyse.</summary>
    public static SessionIncidents Collect(DateTime since, string? game)
    {
        var driver = GpuProfile.DriverCrashesSince(since);
        return new SessionIncidents(driver, game, game is null ? [] : GameEvents(since, game + ".exe"));
    }

    /// <summary>« Application Error » (1000, plantage) et « Application Hang » (1002, ne répond plus) du jeu.</summary>
    private static IReadOnlyList<GameIncident> GameEvents(DateTime since, string exe)
    {
        if (!OperatingSystem.IsWindows()) return [];
        var ms = (long)Math.Max(0, (DateTime.Now - since).TotalMilliseconds);
        var query = new EventLogQuery("Application", PathType.LogName,
            "*[System[((Provider[@Name='Application Error'] and EventID=1000) or (Provider[@Name='Application Hang'] and EventID=1002))" +
            $" and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
        var found = new List<GameIncident>();
        try
        {
            using var reader = new EventLogReader(query);
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
                using (e)
                {
                    // 1er champ des deux événements : nom de l'exécutable fautif
                    var app = e.Properties.Count > 0 ? e.Properties[0].Value?.ToString() : null;
                    if (e.TimeCreated is { } at && string.Equals(app, exe, StringComparison.OrdinalIgnoreCase))
                        found.Add(new GameIncident(at, e.Id == 1002));
                }
        }
        catch (Exception e) when (e is EventLogException or UnauthorizedAccessException) { }
        return found.OrderBy(i => i.At).ToList();
    }
}

/// <summary>
/// Repère le jeu pendant l'analyse : parmi les programmes qui affichent avec la carte, celui présent le plus
/// souvent pendant que la carte travaille, hors composants de Windows, navigateurs et lanceurs.
/// </summary>
public sealed class GameTracker
{
    /// <summary>Programmes qui utilisent la carte sans être des jeux.</summary>
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "dwm", "explorer", "csrss", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "SearchApp",
        "TextInputHost", "ApplicationFrameHost", "SystemSettings", "Widgets", "WidgetService", "LockApp", "RuntimeBroker",
        "ctfmon", "PhoneExperienceHost", "msedgewebview2", "Aria", "Aria-diagnostic-gpu",
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi",
        "Discord", "Teams", "ms-teams", "Spotify", "obs64", "obs32", "Medal", "XboxPcApp", "GameBar", "GameBarFTServer",
        "steam", "steamwebhelper", "EpicGamesLauncher", "EpicWebHelper", "Battle.net", "EADesktop", "EABackgroundService",
        "UbisoftConnect", "upc", "GalaxyClient", "RiotClientServices", "RiotClientUx",
        "NVIDIA Overlay", "NVIDIA App", "NVIDIA Share", "nvcontainer", "NVDisplay.Container", "nvsphelper64",
        "MSIAfterburner", "RTSS", "EncoderServer", "Code", "devenv", "Photoshop", "Lightroom",
        "wallpaper32", "wallpaper64", "Lively", "Rainmeter", "SignalRgb", "iCUE", "ArmouryCrate", "LGHUB"
    };

    private readonly Dictionary<string, int> _activeSeconds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, string?> _names = [];

    /// <param name="processes">Programmes qui affichent avec la carte à cet instant.</param>
    /// <param name="gpuActive">La carte travaille vraiment (charge suffisante) à cet instant.</param>
    public void Observe(IReadOnlyList<uint> processes, bool gpuActive)
    {
        if (!gpuActive) return;
        foreach (var pid in processes)
        {
            if (!_names.TryGetValue(pid, out var name)) _names[pid] = name = NameOf(pid);
            if (name is not null) _activeSeconds[name] = _activeSeconds.GetValueOrDefault(name) + 1;
        }
    }

    /// <summary>Nom du jeu (sans .exe) ; null si aucun candidat.</summary>
    public string? Game => Pick(_activeSeconds);

    /// <summary>
    /// Le jeu à cet instant : parmi les programmes qui affichent avec la carte, hors liste d'exclusion,
    /// celui qui occupe le plus de mémoire (un jeu dépasse largement les petits utilitaires). null si aucun.
    /// </summary>
    public static string? Current(IReadOnlyList<uint> processes)
    {
        string? best = null;
        long bestMemory = -1;
        foreach (var pid in processes)
        {
            try
            {
                using var p = Process.GetProcessById((int)pid);
                if (IsNotGame(p.ProcessName) || p.WorkingSet64 <= bestMemory) continue;
                best = p.ProcessName;
                bestMemory = p.WorkingSet64;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        return best;
    }

    internal static bool IsNotGame(string name) => NotGames.Contains(name);

    internal static string? Pick(IReadOnlyDictionary<string, int> activeSeconds) =>
        activeSeconds.Where(p => !NotGames.Contains(p.Key) && p.Value > 0)
            .OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => p.Key).FirstOrDefault();

    private static string? NameOf(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
