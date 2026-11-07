using Aria.Core.Settings;

namespace Aria.Tests;

/// <summary>
/// Copie protégée de l'exe lancée par les tâches de démarrage : jamais l'exe dézippé par l'utilisateur, qu'un programme
/// sans droits pourrait remplacer pour obtenir les droits administrateur à l'ouverture de session.
/// </summary>
public sealed class LogonExeTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "aria-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _downloads;
    private readonly string _protected;
    private readonly string _source;

    public LogonExeTests()
    {
        _downloads = Directory.CreateDirectory(Path.Combine(_temp, "Telechargements")).FullName;
        _protected = Path.Combine(_temp, "Program Files", "Aria Cockpit");
        _source = Path.Combine(_downloads, "Aria-Cockpit.exe");
        File.WriteAllText(_source, "version 1");
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);

    [Fact]
    public void TheCopyLandsInTheProtectedFolderUnderTheSameName()
    {
        var copy = LogonExe.Install(_source, _protected);

        Assert.Equal(Path.Combine(_protected, "Aria-Cockpit.exe"), copy);
        Assert.Equal("version 1", File.ReadAllText(copy));
        Assert.True(LogonExe.IsSameFile(_source, copy));
    }

    [Fact]
    public void ANewVersionReplacesTheCopyWithoutLeavingATemporaryFile()
    {
        var copy = LogonExe.Install(_source, _protected);
        File.WriteAllText(_source, "version 2, plus longue");

        Assert.False(LogonExe.IsSameFile(_source, copy));
        LogonExe.Install(_source, _protected);

        Assert.Equal("version 2, plus longue", File.ReadAllText(copy));
        Assert.Equal([copy], Directory.GetFiles(_protected));
    }

    [Fact]
    public void AriaLaunchedFromTheCopyDoesNotCopyItself()
    {
        var copy = LogonExe.Install(_source, _protected);
        File.Delete(_source);

        Assert.Equal(copy, LogonExe.Install(copy, _protected));
        Assert.Equal("version 1", File.ReadAllText(copy));
    }

    [Fact]
    public void AMissingExeIsAnError()
    {
        Assert.Throws<FileNotFoundException>(() => LogonExe.Install(Path.Combine(_downloads, "absent.exe"), _protected));
        Assert.False(Directory.Exists(_protected));
    }

    [Fact]
    public void TheCopyLivesInProgramFiles()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

        Assert.Equal(Path.Combine(programFiles, "Aria Cockpit", "Aria-Cockpit.exe"), LogonExe.PathFor(_source));
    }

    [Fact]
    public void RemovingTheCopyOnlyDeletesWhatAriaPutThere()
    {
        LogonExe.Install(_source, _protected);
        File.WriteAllText(Path.Combine(_protected, "Aria-Cockpit.exe.nouveau"), "copie interrompue");

        LogonExe.RemoveCopies(_protected);
        Assert.False(Directory.Exists(_protected));

        LogonExe.Install(_source, _protected);
        File.WriteAllText(Path.Combine(_protected, "notes.txt"), "pas à Aria");
        LogonExe.RemoveCopies(_protected);

        Assert.Equal([Path.Combine(_protected, "notes.txt")], Directory.GetFiles(_protected));
    }
}
