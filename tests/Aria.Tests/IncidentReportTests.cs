using Aria.Core.Nvidia;

namespace Aria.Tests;

/// <summary>Incidents du pilote commentés, et rapport à coller dans une IA.</summary>
public class IncidentReportTests
{
    private static readonly string[] Log =
    [
        "2026-10-30 14:06:46 Appliqué : 320 W · plafond 2685 MHz · GPU +190 MHz · mémoire +0 MHz (avant : 320 W · plafond 2685 MHz · GPU +160 MHz · mémoire +0 MHz)",
        "2026-10-30 14:38:43 Appliqué : 320 W · plafond 2685 MHz · GPU +145 MHz · mémoire +750 MHz · au démarrage (avant : 320 W · plafond 2685 MHz · GPU +145 MHz · mémoire +750 MHz)",
        "2026-11-08 10:30:42 Réappliqué : 320 W · plafond 2685 MHz · GPU +145 MHz · mémoire +750 MHz · au démarrage",
        "2026-11-09 08:11:37 Réappliqué : 320 W · plafond 2685 MHz · GPU +145 MHz · mémoire +750 MHz · au démarrage",
        "ligne sans date, ignorée"
    ];

    private const string Zeros = @"\Device\Video3 | Error occurred on GPUID: 100 | 0 | 0 | 0 | 0 | 2 | 0 | 48 | 0 | 0 | 0 | 0 | 0 | 153";

    private static DriverIncidentDetail Incident(string at, int id = 153, string data = Zeros) =>
        DriverIncidents.GroupDetailed([new DriverEvent(DateTime.Parse(at), "nvlddmkm", id, data)])[0];

    private static IncidentReportInput Input(IReadOnlyList<DriverIncidentDetail> incidents, IReadOnlyDictionary<string, string>? notes = null) => new(
        GeneratedAt: new DateTime(2026, 11, 12, 11, 0, 0),
        AppVersion: "0.26.0",
        GpuName: "NVIDIA GeForce RTX 4080 SUPER",
        DriverVersion: "621.14",
        Windows: "Windows 11 (26300)",
        Current: new GpuTuningState(320_000, 2685, 145, 750, true),
        Origin: new GpuTuningState(320_000, 0, 0, 0, false),
        AppliedAt: new DateTime(2026, 11, 7, 23, 1, 0),
        Balanced: "Équilibre du 30/10 sur le banc d'essai Aria",
        FanCurve: "Ma config",
        Profiles: [("Mon réglage équilibré", "320 W · plafond 2685 MHz · GPU +145 MHz · mémoire +750 MHz")],
        TuningLog: Log,
        Incidents: incidents,
        Notes: notes ?? new Dictionary<string, string>(),
        Days: 30);

    // ---- Notes ----

    [Fact]
    public void ANoteIsKeptUnderTheIncidentTime()
    {
        var at = new DateTime(2026, 11, 11, 21, 46, 45);

        var notes = IncidentNotes.With(new Dictionary<string, string>(), at, "  En jeu, Cyberpunk  ");

        Assert.Equal("En jeu, Cyberpunk", notes["2026-11-11T21:46:45"]);
    }

    [Fact]
    public void AnEmptyNoteErasesIt()
    {
        var at = new DateTime(2026, 11, 11, 21, 46, 45);
        var notes = IncidentNotes.With(new Dictionary<string, string>(), at, "En jeu");

        Assert.Empty(IncidentNotes.With(notes, at, "   "));
    }

    [Fact]
    public void ANoteIsCutAtTheMaximumLength() =>
        Assert.Equal(IncidentNotes.MaxLength,
            IncidentNotes.With(new Dictionary<string, string>(), DateTime.Now, new string('x', 2000)).Values.Single().Length);

    [Fact]
    public void NotesSurviveTheFile()
    {
        var notes = IncidentNotes.With(new Dictionary<string, string>(), new DateTime(2026, 11, 11, 21, 46, 45), "IA locale, « ComfyUI »");

        Assert.Equal(notes, IncidentNotes.FromJson(IncidentNotes.ToJson(notes)));
    }

    [Fact]
    public void UnreadableNotesFileGivesNoNotes() => Assert.Empty(IncidentNotes.FromJson("pas du json"));

    // ---- Réglage en place au moment d'un incident ----

    [Fact]
    public void TuningAtAnIncidentIsTheLastOneAppliedBefore() =>
        Assert.Equal("320 W · plafond 2685 MHz · GPU +190 MHz · mémoire +0 MHz",
            IncidentReport.TuningAt(Log, DateTime.Parse("2026-10-30 14:20:00")));

    [Fact]
    public void AReappliedTuningCounts() =>
        Assert.Equal("320 W · plafond 2685 MHz · GPU +145 MHz · mémoire +750 MHz · au démarrage",
            IncidentReport.TuningAt(Log, DateTime.Parse("2026-11-11 21:46:45")));

    [Fact]
    public void NoTuningIsKnownBeforeTheFirstLine() => Assert.Null(IncidentReport.TuningAt(Log, DateTime.Parse("2026-10-01 00:00:00")));

    // ---- Lignes du pilote ----

    [Fact]
    public void LongRunsOfZerosAreSummed() =>
        Assert.Equal(@"\Device\Video3 | Error occurred on GPUID: 100 | 0 ×4 | 2 | 0 | 48 | 0 ×5 | 153", IncidentReport.Compact(Zeros));

    [Fact]
    public void AUserFolderLosesTheUserName()
    {
        var compact = IncidentReport.Compact(@"13 | C:\Users\alice\AppData\Local\jeu.exe | Out Of Range Address");

        Assert.DoesNotContain("alice", compact);
        Assert.Contains(@"\Users\…\AppData\Local\jeu.exe", compact);
    }

    [Fact]
    public void DriverBytesAreShownInHexWithoutTrailingZeros()
    {
        // Le bloc d'octets que nvlddmkm joint à une erreur 153, tel que le journal le donne
        byte[] bytes = [0, 0, 0, 0, 2, 0, 0x30, 0, 0, 0, 0, 0, 0x99, 0, 0, 0, 0, 0];

        Assert.Equal("0x00000000020030000000000099", DriverIncidents.Text(bytes));
        Assert.Equal("(octets nuls)", DriverIncidents.Text(new byte[8]));
        Assert.Equal(@"\Device\Video3", DriverIncidents.Text(@"\Device\Video3"));
        Assert.Equal("", DriverIncidents.Text(null));
    }

    // ---- Rapport ----

    [Fact]
    public void TheReportTiesEachIncidentToItsTuningAndItsNote()
    {
        var incident = Incident("2026-11-11 21:46:45");
        var notes = IncidentNotes.With(new Dictionary<string, string>(), incident.Incident.At, "En jeu, 4K, depuis une heure");

        var report = IncidentReport.Build(Input([incident], notes));

        Assert.Contains("### 1. mercredi 11/11/2026 à 21:46:45 : erreur du pilote", report);
        Assert.Contains("- Réglage en place à ce moment-là : 320 W · plafond 2685 MHz · GPU +145 MHz · mémoire +750 MHz · au démarrage", report);
        Assert.Contains("- Ce que je faisais : En jeu, 4K, depuis une heure", report);
        Assert.Contains("21:46:45 nvlddmkm 153 :", report);
    }

    [Fact]
    public void AnIncidentWithoutNoteSaysSo() =>
        Assert.Contains("- Ce que je faisais : non précisé", IncidentReport.Build(Input([Incident("2026-11-11 21:46:45")])));

    [Fact]
    public void TheReportDescribesTheCardAndTheTuning()
    {
        var report = IncidentReport.Build(Input([Incident("2026-11-11 21:46:45")]));

        Assert.Contains("- Carte graphique : NVIDIA GeForce RTX 4080 SUPER", report);
        Assert.Contains("- Pilote NVIDIA : 621.14", report);
        Assert.Contains("- En place : 320 W · plafond 2685 MHz · GPU +145 MHz · mémoire +750 MHz · au démarrage (posé le 07/11/2026 à 23:01)", report);
        Assert.Contains("- Valeurs d'usine : 320 W · sans plafond · GPU +0 MHz · mémoire +0 MHz", report);
        Assert.Contains("- Ventilateurs : courbe « Ma config » pilotée par Aria", report);
        Assert.Contains("- Profil enregistré « Mon réglage équilibré »", report);
    }

    [Fact]
    public void TheHistoryListsChangesAndCountsReapplications()
    {
        var report = IncidentReport.Build(Input([]));

        Assert.Contains("- 30/10 14:38 : Appliqué : 320 W · plafond 2685 MHz · GPU +145 MHz", report);
        Assert.Contains("- Le réglage gardé a été réappliqué 2 fois à l'ouverture de session.", report);
        Assert.Contains("Aucun incident du pilote sur la période.", report);
    }

    [Fact]
    public void TheHistoryDropsTheNoise()
    {
        // Rappel « (avant : … ) » retiré, et une ligne qui repose le même réglage n'est pas répétée
        string[] log =
        [
            "2026-11-01 10:00:00 Appliqué : 320 W · plafond 2700 MHz · GPU +215 MHz · mémoire +0 MHz (avant : 320 W · sans plafond · GPU +0 MHz · mémoire +0 MHz)",
            "2026-11-01 10:03:00 Appliqué : 320 W · plafond 2700 MHz · GPU +215 MHz · mémoire +0 MHz (avant : 320 W · plafond 2700 MHz · GPU +215 MHz · mémoire +0 MHz)",
            "2026-11-01 10:06:00 Appliqué : 320 W · plafond 2700 MHz · GPU +230 MHz · mémoire +0 MHz (avant : 320 W · plafond 2700 MHz · GPU +215 MHz · mémoire +0 MHz)"
        ];

        var report = IncidentReport.Build(Input([]) with { TuningLog = log });

        Assert.DoesNotContain("(avant :", report);
        Assert.DoesNotContain("01/11 10:03", report);
        Assert.Contains("- 01/11 10:00 : Appliqué : 320 W · plafond 2700 MHz · GPU +215 MHz · mémoire +0 MHz", report);
        Assert.Contains("- 01/11 10:06 : Appliqué : 320 W · plafond 2700 MHz · GPU +230 MHz · mémoire +0 MHz", report);
    }

    [Fact]
    public void ALongHistoryKeepsTheLastChangesAndSaysSo()
    {
        var log = Enumerable.Range(0, IncidentReport.MaxChanges + 5)
            .Select(i => $"2026-11-01 {10 + i / 60:00}:{i % 60:00}:00 Appliqué : 320 W · plafond 2700 MHz · GPU +{i} MHz · mémoire +0 MHz")
            .ToArray();

        var report = IncidentReport.Build(Input([]) with { TuningLog = log });

        Assert.Contains("(5 changements plus anciens non listés", report);
        Assert.DoesNotContain("GPU +4 MHz ·", report);
        Assert.Contains("GPU +5 MHz ·", report);
    }

    [Fact]
    public void TheReportEndsWithWhatIsExpectedFromTheReader() =>
        Assert.Contains("## Ce que j'attends", IncidentReport.Build(Input([])));
}
