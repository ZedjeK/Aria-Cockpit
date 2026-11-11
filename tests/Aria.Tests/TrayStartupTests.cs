using System.Xml.Linq;
using Aria.Core.Nvidia;
using Aria.Core.Settings;
using Aria.Core.Tray;

namespace Aria.Tests;

/// <summary>« Démarrer avec Windows » : tâche de l'ouverture de session, reprise de la courbe, passage de relais entre versions.</summary>
public class TrayStartupTests
{
    private static readonly IReadOnlyList<FanCurvePoint> Points = [new(45, 60), new(70, 100)];

    // ---- Courbe à relancer ----

    [Fact]
    public void CurveRunningWhenAriaClosedIsStillToResume()
    {
        var running = new FanCurveChoice("Ma config", Points, Active: true, Resume: true);

        var closed = FanCurveStore.Stopped(running, byUser: false);

        Assert.False(closed.Active);
        Assert.True(closed.Resume);
    }

    [Fact]
    public void CurveStoppedByTheUserIsNotResumed()
    {
        var running = new FanCurveChoice("Ma config", Points, Active: true, Resume: true);

        Assert.False(FanCurveStore.Stopped(running, byUser: true).Resume);
    }

    [Fact]
    public void ResumeNeedsTheStartupOption()
    {
        var saved = new FanCurveChoice("Ma config", Points, Active: false, Resume: true);

        Assert.Equal("Ma config", TrayStartup.CurveToResume(saved, enabled: true)?.Name);
        Assert.Null(TrayStartup.CurveToResume(saved, enabled: false));
    }

    [Fact]
    public void NothingIsResumedWithoutACurveToResume()
    {
        Assert.Null(TrayStartup.CurveToResume(null, enabled: true));
        Assert.Null(TrayStartup.CurveToResume(new FanCurveChoice("Ma config", Points, Active: false), enabled: true));
    }

    [Fact]
    public void ResumeFlagSurvivesTheFile()
    {
        var saved = new FanCurveChoice("Ma config", Points, Active: false, Resume: true);

        Assert.True(FanCurveStore.FromJson(FanCurveStore.ToJson(saved))!.Resume);
    }

    [Fact]
    public void FileFromBeforeTheOptionResumesNothing()
    {
        // ventilateur.json écrit par une version sans « Resume »
        const string json = """{ "Name": "Ma config", "Points": [ { "TemperatureC": 45, "Percent": 60 }, { "TemperatureC": 70, "Percent": 100 } ], "Active": false }""";

        var loaded = FanCurveStore.FromJson(json);

        Assert.NotNull(loaded);
        Assert.False(loaded.Resume);
    }

    // ---- Second lancement ----

    [Theory]
    [InlineData("0.24.0", "0.25.0", SecondLaunch.TakeOver)]
    [InlineData("0.25.0", "0.24.0", SecondLaunch.Refuse)]
    [InlineData("0.25.0", "0.25.0", SecondLaunch.Activate)]
    [InlineData("0.9.0", "0.10.0", SecondLaunch.TakeOver)]
    [InlineData(null, "0.25.0", SecondLaunch.Activate)]
    [InlineData("0.25.0", "?", SecondLaunch.Activate)]
    public void ANewerLaunchTakesOverAnOlderOne(string? running, string? launched, SecondLaunch expected) =>
        Assert.Equal(expected, TrayStartup.OnSecondLaunch(running, launched));

    // ---- Tâche de l'ouverture de session ----

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    private static XElement Task(string exe = @"C:\Program Files\Aria Cockpit\Aria-Cockpit.exe") =>
        XDocument.Parse(LogonExe.LongRunningTaskXml(exe, TrayStartup.Argument, @"PC\nom", "S-1-5-21-1-2-3-1001")).Root!;

    private static string? Value(XElement task, params string[] path) =>
        path.Aggregate((XElement?)task, (e, name) => e?.Element(Ns + name))?.Value;

    [Fact]
    public void TheTaskLaunchesTheProtectedCopyInTheTray()
    {
        var task = Task();

        Assert.Equal(@"C:\Program Files\Aria Cockpit\Aria-Cockpit.exe", Value(task, "Actions", "Exec", "Command"));
        Assert.Equal("--tray", Value(task, "Actions", "Exec", "Arguments"));
    }

    [Fact]
    public void TheTaskRunsElevatedAtTheUsersLogon()
    {
        var task = Task();

        Assert.Equal(@"PC\nom", Value(task, "Triggers", "LogonTrigger", "UserId"));
        Assert.Equal("S-1-5-21-1-2-3-1001", Value(task, "Principals", "Principal", "UserId"));
        Assert.Equal("HighestAvailable", Value(task, "Principals", "Principal", "RunLevel"));
    }

    [Fact]
    public void TheTaskIsNeitherStoppedAfterThreeDaysNorOnBattery()
    {
        // Les deux réglages par défaut du planificateur qui arrêteraient un exemplaire censé rester ouvert
        var task = Task();

        Assert.Equal("PT0S", Value(task, "Settings", "ExecutionTimeLimit"));
        Assert.Equal("false", Value(task, "Settings", "DisallowStartIfOnBatteries"));
        Assert.Equal("false", Value(task, "Settings", "StopIfGoingOnBatteries"));
    }

    [Fact]
    public void AnExePathWithSpecialCharactersStaysValidXml()
    {
        var task = Task(@"C:\Program Files\Aria & Cie <test>\Aria-Cockpit.exe");

        Assert.Equal(@"C:\Program Files\Aria & Cie <test>\Aria-Cockpit.exe", Value(task, "Actions", "Exec", "Command"));
    }
}
