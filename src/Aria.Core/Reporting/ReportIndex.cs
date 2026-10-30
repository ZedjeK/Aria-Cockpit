using System.Globalization;

namespace Aria.Core.Reporting;

public enum ReportKind { Settings, Simulation, Restore, Updates, PcSheet, Cleanup, GpuTuning }

/// <summary>Un rapport enregistré : type, date et ligne de bilan.</summary>
public sealed record ReportInfo(string Path, ReportKind Kind, DateTime At, string Summary);

/// <summary>Liste des rapports de C:\ProgramData\AriaCockpit\Rapports, du plus récent au plus ancien.</summary>
public static class ReportIndex
{
    private const string SettingsPrefix = "rapport_";
    private const string UpdatesPrefix = "mises-a-jour_";
    internal const string CleanupPrefix = "nettoyage_";

    public static IReadOnlyList<ReportInfo> List(string? folder = null)
    {
        folder ??= ReportFile.Folder;
        if (!Directory.Exists(folder)) return [];

        var reports = new List<ReportInfo>();
        var files = Directory.EnumerateFiles(folder, "*.txt")
            .Concat(Directory.EnumerateFiles(folder, $"{HtmlReport.FilePrefix}*.html"));
        foreach (var path in files)
        {
            IReadOnlyList<string> lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                lines = []; // fichier en cours d'écriture ou verrouillé : listé sans bilan
            }
            if (Describe(path, lines) is { } info) reports.Add(info);
        }
        return reports.OrderByDescending(r => r.At).ToList();
    }

    /// <summary>Lit le type (nom du fichier et ligne « Mode : »), la date (nom du fichier) et le dernier bilan.</summary>
    internal static ReportInfo? Describe(string path, IReadOnlyList<string> lines)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        ReportKind kind;
        string stamp;

        if (name.StartsWith(HtmlReport.FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!DateTime.TryParseExact(name[HtmlReport.FilePrefix.Length..], "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var sheetAt))
                return null;
            // Bilan écrit dans la balise <meta name="description"> de la fiche
            var meta = lines.Take(10).FirstOrDefault(l => l.StartsWith(HtmlReport.SummaryMarker, StringComparison.Ordinal)) ?? "";
            var text = meta.Length > 0 ? System.Net.WebUtility.HtmlDecode(meta[HtmlReport.SummaryMarker.Length..].TrimEnd('>', '"', ' ')) : "";
            if (text.StartsWith("Bilan :", StringComparison.Ordinal)) text = text["Bilan :".Length..].Trim();
            return new ReportInfo(path, ReportKind.PcSheet, sheetAt, text);
        }

        if (name.StartsWith(GpuTuningReport.FilePrefix, StringComparison.OrdinalIgnoreCase))
        {
            kind = ReportKind.GpuTuning;
            stamp = name[GpuTuningReport.FilePrefix.Length..];
        }
        else if (name.StartsWith(CleanupPrefix, StringComparison.OrdinalIgnoreCase))
        {
            kind = ReportKind.Cleanup;
            stamp = name[CleanupPrefix.Length..];
        }
        else if (name.StartsWith(UpdatesPrefix, StringComparison.OrdinalIgnoreCase))
        {
            kind = ReportKind.Updates;
            stamp = name[UpdatesPrefix.Length..];
        }
        else if (name.StartsWith(SettingsPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var mode = lines.Take(5).FirstOrDefault(l => l.StartsWith("Mode :", StringComparison.Ordinal)) ?? "";
            kind = mode.Contains("simulation", StringComparison.OrdinalIgnoreCase) ? ReportKind.Simulation
                 : mode.Contains("restauration", StringComparison.OrdinalIgnoreCase) ? ReportKind.Restore
                 : ReportKind.Settings;
            stamp = name[SettingsPrefix.Length..];
        }
        else return null;

        if (!DateTime.TryParseExact(stamp, "yyyy-MM-dd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var at))
            return null;

        // Le dernier « Bilan » est celui des réglages ou des mises à jour (le premier, dans un rapport de réglages, est celui du diagnostic)
        var summary = lines.LastOrDefault(l => l.TrimStart().StartsWith("Bilan :", StringComparison.Ordinal))?.Trim() ?? "";
        if (summary.StartsWith("Bilan :", StringComparison.Ordinal)) summary = summary["Bilan :".Length..].Trim();

        return new ReportInfo(path, kind, at, summary);
    }
}
