using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aria.Core.Performance;

/// <summary>Un programme qui occupe de la mémoire vidéo dédiée.</summary>
public sealed record VramUser(string Name, long Bytes)
{
    /// <summary>« llama-server (10,5 Go) ».</summary>
    public override string ToString() => $"{Name} ({(Bytes / 1073741824.0).ToString("0.0", CultureInfo.GetCultureInfo("fr-FR"))} Go)";
}

/// <summary>
/// Programmes qui occupent beaucoup de mémoire vidéo (un LLM chargé, un jeu resté ouvert…), d'après le compteur Windows
/// « GPU Process Memory » : pendant l'assistant, ils réduisent le test mémoire et peuvent fausser chaque mesure.
/// </summary>
public static partial class VramUsers
{
    /// <summary>En dessous, un programme ne gêne pas (navigateur, Steam, fond d'écran animé).</summary>
    public const long MinBytes = 1L << 30;

    /// <summary>Le système (composition des fenêtres) et Aria elle-même ne comptent pas.</summary>
    private static readonly string[] Ignored = ["dwm", "csrss", "Aria-Cockpit", "Aria-Bench"];

    /// <summary>Programmes au-delà de <see cref="MinBytes"/>, le plus gros d'abord ; liste vide si le compteur est illisible.</summary>
    public static IReadOnlyList<VramUser> Read()
    {
        try
        {
            var data = new PerformanceCounterCategory("GPU Process Memory").ReadCategory()["Dedicated Usage"];
            if (data is null) return [];
            var samples = data.Values.Cast<InstanceData>().Select(i => (i.InstanceName, i.RawValue)).ToList();
            return Heavy(samples, NameOf, Environment.ProcessId);
        }
        catch (Exception e) when (e is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Regroupe les instances du compteur (« pid_12004_luid_0x00000000_0x00009ece_phys_0 ») par processus et garde les gros.
    /// </summary>
    public static IReadOnlyList<VramUser> Heavy(IEnumerable<(string Instance, long Bytes)> samples, Func<int, string?> nameOf, int self)
    {
        var users = new List<VramUser>();
        foreach (var process in samples
                     .Select(s => (Match: Pid().Match(s.Instance), s.Bytes))
                     .Where(s => s.Match.Success)
                     .GroupBy(s => int.Parse(s.Match.Groups[1].Value, CultureInfo.InvariantCulture), s => Math.Max(0, s.Bytes)))
        {
            var bytes = process.Sum();
            if (bytes < MinBytes || process.Key == self) continue;
            if (nameOf(process.Key) is not { } name || Ignored.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            users.Add(new VramUser(name, bytes));
        }
        return users.OrderByDescending(u => u.Bytes).ToList();
    }

    /// <summary>« llama-server (10,5 Go), Cyberpunk2077 (6,1 Go) ».</summary>
    public static string Describe(IEnumerable<VramUser> users) => string.Join(", ", users);

    private static string? NameOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return null; // fermé entre-temps
        }
    }

    [GeneratedRegex(@"^pid_(\d+)_", RegexOptions.IgnoreCase)]
    private static partial Regex Pid();
}
