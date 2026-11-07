using Aria.Core.Updates;

namespace Aria.Tests;

/// <summary>
/// Détails des mises à jour : liens sûrs, lien Windows Update, description, failles, historique et notes de version
/// winget (sorties réelles, en français et en anglais). Aucun appel à Windows Update ni à winget.
/// </summary>
public class UpdateDetailsTests
{
    // ---- Adresses ----

    [Theory]
    [InlineData("https://support.microsoft.com/help/5127936", "https://support.microsoft.com/help/5127936")]
    [InlineData("  http://download.anydesk.com/changelog.txt ", "http://download.anydesk.com/changelog.txt")]
    [InlineData("https://go.microsoft.com/fwlink/?linkid=2181179&amp;clcid=0x409", "https://go.microsoft.com/fwlink/?linkid=2181179&clcid=0x409")]
    [InlineData(@"C:\Windows\System32\cmd.exe", null)]
    [InlineData("file:///C:/Windows/System32/calc.exe", null)]
    [InlineData("calc.exe", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void OnlyWebAddressesAreKept(string? text, string? expected)
    {
        Assert.Equal(expected, UpdateDetails.SafeUrl(text));
    }

    [Theory]
    [InlineData("2026-10 Mise à jour de la préversion (KB5127936) (26300.9818)", "5127936")]
    [InlineData("Mise à jour pour Microsoft Defender Antivirus – 2267602 Ko (version 1.463.172.0)", "2267602")]
    [InlineData("9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2", null)]
    public void KbNumberIsFoundInBothLanguages(string title, string? kb)
    {
        Assert.Equal(kb, UpdateDetails.KbOf(title));
    }

    [Fact]
    public void WindowsUpdateLinkPrefersMicrosoftsOwnPage()
    {
        Assert.Equal("https://go.microsoft.com/fwlink/?linkid=2007160",
            UpdateDetails.WindowsUpdateLink(["https://go.microsoft.com/fwlink/?linkid=2007160"], "2267602", null));
        // Pas de page jointe : l'article KB, sauf pour les définitions Defender qui n'en ont pas
        Assert.Equal("https://support.microsoft.com/help/5127936", UpdateDetails.WindowsUpdateLink([null, "pas une adresse"], "5127936", null));
        Assert.Equal("https://go.microsoft.com/fwlink/?LinkId=52661",
            UpdateDetails.WindowsUpdateLink([], "2267602", "https://go.microsoft.com/fwlink/?LinkId=52661"));
        Assert.Null(UpdateDetails.WindowsUpdateLink([], null, null));
    }

    [Theory]
    [InlineData("Installer cette mise à jour pour réviser   les fichiers\r\n utilisés.", "Titre", "Installer cette mise à jour pour réviser les fichiers utilisés.")]
    [InlineData("9NRZT3Q9R3DL-1152921505702239086", "9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2", null)]   // identifiant du Store
    [InlineData("Pilote audio", "Pilote audio", null)]   // ne fait que répéter le titre
    [InlineData("   ", "Titre", null)]
    public void DescriptionIsCleaned(string description, string title, string? expected)
    {
        Assert.Equal(expected, UpdateDetails.CleanDescription(description, title));
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "Corrige 1 faille de sécurité (CVE)")]
    [InlineData(37, "Corrige 37 failles de sécurité (CVE)")]
    public void FixedFlawsAreCounted(int count, string? expected)
    {
        Assert.Equal(expected, UpdateDetails.CveSummary(count));
    }

    // ---- Historique ----

    [Fact]
    public void StoreUpdatesLoseTheirTechnicalIdentifier()
    {
        Assert.Equal("Microsoft Store · Microsoft.WindowsAppRuntime.2", UpdateDetails.TidyTitle("9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2"));
        Assert.Equal("2026-10 Mise à jour cumulative (KB5127001)", UpdateDetails.TidyTitle("2026-10 Mise à jour cumulative (KB5127001)"));
    }

    [Theory]
    [InlineData("Aria Cockpit", true)]
    [InlineData("Aria", true)]   // versions 0.13 et 0.14
    [InlineData("MoUpdateOrchestrator", false)]
    [InlineData("Windows Defender", false)]
    public void UpdatesInstalledFromAriaAreRecognised(string clientId, bool byAria)
    {
        var entry = WindowsUpdateHistory.Map(1, 2, new DateTime(2026, 11, 7), "Mise à jour (KB5127001)", clientId, null);

        Assert.Equal(byAria, entry.ByAria);
    }

    [Fact]
    public void HistoryEntryGetsItsArticleAndStatus()
    {
        var patch = WindowsUpdateHistory.Map(1, 2, DateTime.Today, "2026-10 Mise à jour de la préversion (KB5127936)", "MoUpdateOrchestrator", null);
        var defender = WindowsUpdateHistory.Map(1, 4, DateTime.Today, "Defender – 2267602 Ko (version 1.463)", "Windows Defender",
            "https://go.microsoft.com/fwlink/?LinkId=52661");
        var removal = WindowsUpdateHistory.Map(2, 2, DateTime.Today, "Mise à jour (KB5000001)", "MoUpdateOrchestrator", null);

        Assert.Equal("https://support.microsoft.com/help/5127936", patch.Url);
        Assert.Equal(HistoryResult.Succeeded, patch.Result);
        Assert.Equal("https://go.microsoft.com/fwlink/?LinkId=52661", defender.Url);   // pas d'article pour Defender
        Assert.Equal(HistoryResult.Failed, defender.Result);
        Assert.True(removal.IsUninstall);
    }

    [Fact]
    public void DefenderDefinitionsAreGroupedSoTheRestStaysVisible()
    {
        UpdateHistoryEntry Entry(int day, string title) => WindowsUpdateHistory.Map(1, 2, new DateTime(2026, 11, day), title, "Windows Defender", null);
        var entries = new[]
        {
            Entry(7, "Defender – 2267602 Ko (version 1.463.172.0)"),
            Entry(6, "Defender – 2267602 Ko (version 1.463.163.0)"),
            Entry(6, "Mise à jour pour Windows Security platform - KB5007651"),   // plateforme : gardée
            Entry(5, "Defender – 2267602 Ko (version 1.463.153.0)"),
            Entry(4, "2026-11 Mise à jour cumulative (KB5130000)"),
            Entry(3, "2026-10 Mise à jour cumulative (KB5127001)")
        };

        var view = WindowsUpdateHistory.Group(entries, max: 3);

        Assert.Equal(["Defender – 2267602 Ko (version 1.463.172.0)", "Mise à jour pour Windows Security platform - KB5007651",
            "2026-11 Mise à jour cumulative (KB5130000)"], view.Entries.Select(e => e.Title));
        Assert.Equal(2, view.GroupedDefinitions);
    }

    // ---- Notes de version (winget show) ----

    /// <summary>Sortie réelle de winget en français (espace insécable avant les deux-points), raccourcie.</summary>
    private const string FrenchShow =
        "Trouvé Notepad++ [Notepad++.Notepad++]\r\n" +
        "Version\u00A0: 8.9.9\r\n" +
        "Publisher\u00A0: Notepad++ Team\r\n" +
        "Description\u00A0:\r\n" +
        "  Notepad++ is a free source code editor.\r\n" +
        "Page d’accueil : https://notepad-plus-plus.org/\r\n" +
        "URL des notes de publication\u00A0: https://github.com/notepad-plus-plus/notepad-plus-plus/releases/tag/v8.9.9\r\n" +
        "Documentation :\r\n" +
        "  Wiki: https://github.com/notepad-plus-plus/notepad-plus-plus/wiki\r\n" +
        "Mots-clés\u00A0:\r\n" +
        "  editor\r\n";

    [Fact]
    public void FrenchReleaseNotesLinkIsRead()
    {
        var notes = UpdateDetails.ParseWingetShow(FrenchShow);

        Assert.Equal("https://github.com/notepad-plus-plus/notepad-plus-plus/releases/tag/v8.9.9", notes.Url);
        Assert.Null(notes.Text);   // le Wiki en retrait sous « Documentation » n'est pas pris pour des notes
    }

    [Fact]
    public void EnglishReleaseNotesTextAndLinkAreRead()
    {
        const string show =
            "Found PowerToys [Microsoft.PowerToys]\n" +
            "Version: 0.102.3101\n" +
            "Release Notes:\n" +
            "  ## Highlights\n" +
            "  - New Command Palette extensions\n" +
            "\n" +
            "  - Fixed FancyZones crash\n" +
            "Release Notes Url: https://github.com/microsoft/PowerToys/releases/tag/v0.102.3101\n" +
            "Tags:\n" +
            "  powertoys\n";

        var notes = UpdateDetails.ParseWingetShow(show);

        Assert.Equal("https://github.com/microsoft/PowerToys/releases/tag/v0.102.3101", notes.Url);
        // La ligne vide au milieu des notes ne les coupe pas ; « Tags » (hors notes) n'y entre pas
        Assert.Equal("## Highlights\n- New Command Palette extensions\n- Fixed FancyZones crash", notes.Text);
    }

    [Fact]
    public void ProgressSpinnerBeforeTheOutputIsIgnored()
    {
        var notes = UpdateDetails.ParseWingetShow("   - \r   \\ \r   | \rRelease Notes Url: https://example.org/notes\r\n");

        Assert.Equal("https://example.org/notes", notes.Url);
    }

    [Fact]
    public void LocalPathInAManifestIsNeverOffered()
    {
        Assert.Null(UpdateDetails.ParseWingetShow("Release Notes Url: C:\\Windows\\System32\\cmd.exe\n").Url);
    }

    [Fact]
    public void LongNotesAreShortened()
    {
        var lines = Enumerable.Range(1, 20).Select(i => $"Ligne {i}").ToList();
        var text = UpdateDetails.Shorten(lines)!;

        Assert.Equal(UpdateDetails.MaxNoteLines, text.Split('\n').Length);
        Assert.EndsWith(" …", text);
        Assert.True(UpdateDetails.Shorten([new string('x', 2000)])!.Length <= UpdateDetails.MaxNoteChars + 2);
        Assert.Null(UpdateDetails.Shorten([]));
    }
}
