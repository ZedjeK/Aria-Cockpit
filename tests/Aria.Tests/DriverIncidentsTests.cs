using Aria.Core.Nvidia;

namespace Aria.Tests;

/// <summary>Garde-fou des plantages : incidents regroupés et typés, d'après le journal réel de la tour (26/10/2026).</summary>
public class DriverIncidentsTests
{
    private static DriverEvent Nv(string time, int id, string data) =>
        new(DateTime.Parse($"2026-10-26 {time}"), "nvlddmkm", id, @"\Device\Video3 | " + data);

    private const string OutOfRange = "Graphics SM Warp Exception on (GPC 1, TPC 1, SM 0): Out Of Range Address";
    private const string Gpuid = "Error occurred on GPUID: 100";

    /// <summary>Un incident tel que la tour l'a écrit : une exception, son détail, puis 4 erreurs 153, dans la même seconde.</summary>
    private static IEnumerable<DriverEvent> TowerIncident(string time) =>
    [
        Nv(time, 13, OutOfRange),
        Nv(time, 13, "Graphics Exception: ESR 0x50cf30=0xc01000e 0x50cf34=0x20"),
        Nv(time, 153, Gpuid), Nv(time, 153, Gpuid), Nv(time, 153, Gpuid), Nv(time, 153, Gpuid)
    ];

    private static readonly DriverEvent[] TowerLog =
        [.. TowerIncident("16:18:15"), .. TowerIncident("16:38:44"), .. TowerIncident("17:26:26"), .. TowerIncident("20:49:07")];

    [Fact]
    public void TwentyFourLinesAreFourIncidents()
    {
        var incidents = DriverIncidents.Group(TowerLog);

        Assert.Equal(24, TowerLog.Length);
        Assert.Equal(4, incidents.Count);
        Assert.All(incidents, i => Assert.Equal(DriverIncidentKind.MemoryAccess, i.Kind));
        Assert.All(incidents, i => Assert.Equal(6, i.Events));
    }

    [Fact]
    public void IncidentKindIsTheMostSevereLine()
    {
        var incidents = DriverIncidents.Group(
        [
            Nv("10:00:00", 153, Gpuid),
            new DriverEvent(DateTime.Parse("2026-10-26 10:00:02"), "Display", 4101, "nvlddmkm"),
            Nv("10:00:03", 13, OutOfRange)
        ]);

        Assert.Equal(DriverIncidentKind.Reset, Assert.Single(incidents).Kind);
    }

    [Fact]
    public void LinesFurtherApartAreSeparateIncidents()
    {
        var incidents = DriverIncidents.Group([Nv("10:00:00", 153, Gpuid), Nv("10:00:11", 153, Gpuid)]);

        Assert.Equal(2, incidents.Count);
        Assert.All(incidents, i => Assert.Equal(DriverIncidentKind.DriverError, i.Kind));
    }

    [Theory]
    [InlineData(13, OutOfRange, DriverIncidentKind.MemoryAccess)]
    [InlineData(13, "Graphics Exception: Illegal Address", DriverIncidentKind.MemoryAccess)]
    [InlineData(13, "Graphics Exception: ESR 0x50cf30=0xc01000e", DriverIncidentKind.GraphicsException)]
    [InlineData(153, Gpuid, DriverIncidentKind.DriverError)]
    public void LinesAreClassified(int id, string data, DriverIncidentKind kind)
    {
        Assert.Equal(kind, DriverIncidents.Classify(Nv("10:00:00", id, data)));
    }

    // ---- Conseil du bandeau ----

    private static readonly DateTime Applied = DateTime.Parse("2026-10-26 14:32");

    [Fact]
    public void MemoryAccessPointsAtTheCoreBeforeTheMemory()
    {
        // Tour : l'undervolt donnait 17 130 calculs faux à pleine puissance, la mémoire à +1000 MHz aucune donnée relue fausse
        var advice = DriverIncidents.Advise(DriverIncidents.Group(TowerLog), Applied, new GpuTuningState(320_000, 2685, 215, 1000, true));

        Assert.StartsWith("4 incidents du pilote NVIDIA depuis le réglage du 26/10 à 14:32 (4 × accès mémoire invalide), le dernier le 26/10 à 20:49.", advice);
        Assert.Contains("cœur graphique qui calcule faux", advice);
        Assert.Contains("Revenez au réglage d'origine, puis relancez l'assistant", advice);
        Assert.Contains("un calcul vérifié à pleine puissance et un test de la mémoire vidéo", advice);
    }

    [Fact]
    public void WithoutMemoryOverclockOnlyTheComputeCheckIsMentioned()
    {
        var advice = DriverIncidents.Advise(DriverIncidents.Group(TowerLog), Applied, new GpuTuningState(320_000, 2685, 215, 0, true));

        Assert.EndsWith("ses paliers passent désormais un calcul vérifié à pleine puissance.", advice);
    }

    [Fact]
    public void IsolatedDriverErrorsAreNotCalledInstability()
    {
        var advice = DriverIncidents.Advise(DriverIncidents.Group([Nv("15:00:00", 153, Gpuid)]), Applied,
            new GpuTuningState(320_000, 2685, 215, 1000, true));

        Assert.StartsWith("1 incident du pilote NVIDIA", advice);
        Assert.Contains("existent aussi sans aucun réglage", advice);
    }
}
