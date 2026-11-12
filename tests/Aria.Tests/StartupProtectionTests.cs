using Aria.Core.Diagnostics.Checks;
using Aria.Core.Models;
using Aria.Core.Startup;

namespace Aria.Tests;

/// <summary>
/// Programmes de démarrage : jamais proposés à la désactivation pour la sécurité de Windows et les pilotes (dont l'audio),
/// vrai nom des programmes lancés par rundll32, et point d'information du diagnostic. Repris de CTI-Cockpit.
/// </summary>
public class StartupProtectionTests
{
    private static StartupEntry Entry(string name, string command, string? publisher = null, StartupSource source = StartupSource.MachineRun) =>
        new(source, name, command, name, Enabled: true, publisher);

    public static TheoryData<string, string, string?> ProtectedEntries => new()
    {
        // Sécurité de Windows
        { "SecurityHealth", @"C:\windows\system32\SecurityHealthSystray.exe", "Microsoft Corporation" },
        // Pilotes audio : dans le magasin de pilotes ou reconnus à leur éditeur
        { "RtkAudUService", @"""C:\windows\System32\DriverStore\FileRepository\realtekservice.inf_amd64_d4e2f40b8460b254\RtkAudUService64.exe"" -background", "Realtek Semiconductor" },
        { "WavesSvc", @"""C:\windows\System32\DriverStore\FileRepository\wavesapo14de.inf_amd64_33cdf81f4a6d253e\WavesSvc64.exe"" -Jack", "Waves Audio Ltd." },
        { "RtkAudUService", @"""C:\Program Files\Realtek\Audio\RtkAudUService64.exe""", "Realtek Semiconductor" },
    };

    [Theory]
    [MemberData(nameof(ProtectedEntries))]
    public void SecurityAndDriverComponentsAreNeverOffered(string name, string command, string? publisher)
    {
        Assert.True(Entry(name, command, publisher).IsProtected);
    }

    public static TheoryData<string, string, string?> OfferedEntries => new()
    {
        { "Opera GX Browser Assistant", @"""C:\Users\u\AppData\Local\Programs\Opera GX\assistant\browser_assistant.exe""", "Opera Software" },
        { "SteelSeriesGG", @"""C:\Program Files\SteelSeries\GG\SteelSeriesGGEZ.exe"" -dataPath=""C:\ProgramData\SteelSeries\GG""", "SteelSeries A/S" },
        { "Logitech Download Assistant", @"C:\Windows\system32\rundll32.exe C:\Windows\System32\LogiLDA.dll,LogiFetch", "Microsoft Corporation" },
        // Outils du quotidien : à chacun de décider, ils restent proposés (tous décochés par défaut)
        { "OneDrive", @"""C:\Program Files\Microsoft OneDrive\OneDrive.exe"" /background", "Microsoft Corporation" },
        { "1Password", @"""C:\Users\u\AppData\Local\1Password\app\8\1Password.exe"" --silent", "AgileBits Inc." },
        { "Discord", @"C:\Users\u\AppData\Local\Discord\Update.exe --processStart Discord.exe", "Discord Inc." },
    };

    [Theory]
    [MemberData(nameof(OfferedEntries))]
    public void OrdinaryProgramIsOffered(string name, string command, string? publisher)
    {
        Assert.False(Entry(name, command, publisher).IsProtected);
    }

    // ---- Programmes lancés par rundll32 ----

    [Theory]
    [InlineData(@"C:\Windows\system32\rundll32.exe C:\Windows\System32\LogiLDA.dll,LogiFetch", @"C:\Windows\System32\LogiLDA.dll")]
    [InlineData(@"""C:\Windows\System32\rundll32.exe"" ""C:\Program Files\Outil\outil.dll"",Start /x", @"C:\Program Files\Outil\outil.dll")]
    [InlineData(@"rundll32.exe C:\a.dll", @"C:\a.dll")]
    [InlineData(@"""C:\Program Files\1Password\app\8\1Password.exe"" --silent", null)]
    [InlineData(@"C:\Windows\system32\rundll32.exe", null)]
    public void LibraryRunByRundll32IsFound(string command, string? expected)
    {
        Assert.Equal(expected, StartupApps.HostedLibraryPath(command));
    }

    [Fact]
    public void DriverComponentRunByRundll32IsProtected()
    {
        var entry = Entry("IntelHelper",
            @"C:\Windows\system32\rundll32.exe C:\Windows\System32\DriverStore\FileRepository\iigd.inf_amd64_1\helper.dll,Run",
            "Microsoft Corporation");

        Assert.True(entry.IsProtected);
    }

    // ---- Point d'information du diagnostic ----

    private static StartupEntry Kept(string name) =>
        Entry(name, $@"""C:\windows\System32\DriverStore\FileRepository\{name}.inf_amd64_1\{name}.exe""", "Microsoft Corporation");

    private static StartupEntry Optional(string name, bool enabled = true) =>
        new(StartupSource.UserRun, name, $@"""C:\Program Files\{name}\{name}.exe""", name, enabled, "Éditeur");

    [Fact]
    public void ProtectedProgramsAreCountedApartFromOptionalOnes()
    {
        var finding = StartupCheck.Evaluate(
            [Kept("Realtek"), Kept("Waves"), Optional("Spotify"), Optional("Opera GX"), Optional("Zoom", enabled: false)]);

        Assert.NotNull(finding);
        Assert.Equal("4 programmes au démarrage, dont 2 désactivables", finding.Title);
        Assert.Equal("Désactivables : Opera GX, Spotify. Gardés (sécurité de Windows, pilotes) : Realtek, Waves.", finding.Detail);
        Assert.Equal(Severity.Ok, finding.Severity);   // sous le seuil : pas de conseil
        Assert.Null(finding.Advice);
    }

    [Fact]
    public void ManyOptionalProgramsAreWorthReviewing()
    {
        var entries = Enumerable.Range(1, StartupCheck.ReviewThreshold).Select(i => Optional($"Outil {i}")).Append(Kept("Realtek"));
        var finding = StartupCheck.Evaluate(entries)!;

        Assert.Equal(Severity.Info, finding.Severity);
        Assert.Equal("Optimisation → Démarrage : 5 programmes à revoir, désactivation réversible.", finding.Advice);
    }

    [Fact]
    public void ManyProtectedProgramsAloneAreNoConcern()
    {
        var finding = StartupCheck.Evaluate(Enumerable.Range(1, 20).Select(i => Kept($"Pilote{i}")))!;

        Assert.Equal("20 programmes au démarrage, tous gardés", finding.Title);
        Assert.Equal(Severity.Ok, finding.Severity);
    }

    [Fact]
    public void NothingEnabledGivesNoFinding()
    {
        Assert.Null(StartupCheck.Evaluate([Optional("Zoom", enabled: false)]));
    }
}
