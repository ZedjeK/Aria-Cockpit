using Aria.Core.Reporting;

namespace Aria.Tests;

/// <summary>journal.json : une ligne par opération, 500 au plus, jamais de quoi faire échouer une opération.</summary>
public sealed class OperationJournalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aria-tests-" + Guid.NewGuid().ToString("N"));

    public OperationJournalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Report(string name, params string[] lines)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllLines(path, lines);
        return path;
    }

    [Fact]
    public void AnEmptyFolderHasAnEmptyJournal() => Assert.Empty(OperationJournal.Load(_root));

    [Fact]
    public void AnOperationIsRecordedWithItsKindSummaryAndReport()
    {
        var report = Report("nettoyage_2026-11-12_140200.txt", "Aria Cockpit — nettoyage", "  Bilan : 9,1 Go libérés");

        OperationJournal.Record(_root, report);

        var entry = Assert.Single(OperationJournal.Load(_root));
        Assert.Equal("nettoyage", entry.Kind);
        Assert.Equal(new DateTime(2026, 11, 12, 14, 2, 0), entry.At);
        Assert.Equal("9,1 Go libérés", entry.Summary);
        Assert.Equal("nettoyage_2026-11-12_140200.txt", entry.Report);
    }

    [Theory]
    [InlineData("Mode : optimisation", "optimisation")]
    [InlineData("Mode : restauration", "restauration")]
    [InlineData("Mode : simulation (rien n'a été modifié)", "simulation")]
    public void ASettingsReportIsNamedAfterItsMode(string mode, string expected)
    {
        OperationJournal.Record(_root, Report("rapport_2026-11-12_140300.txt", "Aria Cockpit — rapport", mode));

        Assert.Equal(expected, Assert.Single(OperationJournal.Load(_root)).Kind);
    }

    [Fact]
    public void TheFileUsesTheFrenchNamesOfTheAriaContract()
    {
        OperationJournal.Record(_root, Report("mises-a-jour_2026-11-12_150000.txt", "  Bilan : 3 installées"));

        var json = File.ReadAllText(Path.Combine(_root, "journal.json"));
        Assert.Contains("\"date\"", json);
        Assert.Contains("\"type\": \"mises-a-jour\"", json);
        Assert.Contains("\"bilan\"", json);
        Assert.Contains("\"rapport\"", json);
    }

    [Fact]
    public void OperationsAreKeptInOrderOldestFirst()
    {
        OperationJournal.Record(_root, Report("nettoyage_2026-11-10_100000.txt"));
        OperationJournal.Record(_root, Report("nettoyage_2026-11-11_100000.txt"));

        var entries = OperationJournal.Load(_root);
        Assert.Equal(2, entries.Count);
        Assert.True(entries[0].At < entries[1].At);
    }

    [Fact]
    public void OnlyTheLastFiveHundredOperationsAreKept()
    {
        var start = new DateTime(2026, 1, 1);
        for (var i = 0; i < OperationJournal.MaxEntries + 3; i++)
            OperationJournal.Record(_root, Report($"nettoyage_{start.AddMinutes(i):yyyy-MM-dd_HHmmss}.txt"));

        var entries = OperationJournal.Load(_root);
        Assert.Equal(OperationJournal.MaxEntries, entries.Count);
        Assert.Equal(start.AddMinutes(3), entries[0].At);
    }

    [Fact]
    public void APcSheetIsNotAnOperation()
    {
        OperationJournal.Record(_root, Report($"{HtmlReport.FilePrefix}2026-11-12_160000.html", "<html>"));

        Assert.Empty(OperationJournal.Load(_root));
    }

    [Fact]
    public void AFileThatIsNotAReportIsIgnored()
    {
        OperationJournal.Record(_root, Report("notes.txt", "rien"));

        Assert.Empty(OperationJournal.Load(_root));
    }

    [Fact]
    public void AMissingReportNeverThrows()
    {
        OperationJournal.Record(_root, Path.Combine(_root, "nettoyage_2026-11-12_170000.txt"));

        Assert.Empty(OperationJournal.Load(_root));
    }

    [Fact]
    public void AnUnreadableJournalIsStartedAgainWithoutThrowing()
    {
        File.WriteAllText(Path.Combine(_root, "journal.json"), "pas du json");

        OperationJournal.Record(_root, Report("nettoyage_2026-11-12_180000.txt"));

        Assert.Single(OperationJournal.Load(_root));
    }
}