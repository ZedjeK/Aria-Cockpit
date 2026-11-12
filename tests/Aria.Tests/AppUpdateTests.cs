using System.Security.Cryptography;
using System.Text;
using Aria.Core.Updates;

namespace Aria.Tests;

/// <summary>
/// Mise à jour d'Aria Cockpit : lecture de la release, choix du fichier, et contrôle de ce qui est téléchargé.
/// Rien ne sort sur le réseau : le flux reçu est simulé.
/// </summary>
public sealed class AppUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "aria-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private static readonly Version Installed = new(0, 26, 0);
    private static readonly string Sha = new('a', 64);

    private const string Base = "https://github.com/ZedjeK/Aria-Cockpit/releases/download/v0.27.0/";

    private static string Release(string assets, string body = "") => $$"""
        {
          "tag_name": "v0.27.0",
          "html_url": "https://github.com/ZedjeK/Aria-Cockpit/releases/tag/v0.27.0",
          "body": {{System.Text.Json.JsonSerializer.Serialize(body)}},
          "assets": [{{assets}}]
        }
        """;

    private static string Asset(string name, string? url = null, long size = 1000, string? digest = null) => $$"""
        { "name": "{{name}}", "browser_download_url": "{{url ?? Base + name}}", "size": {{size}}, "digest": "{{digest ?? "sha256:" + Sha}}" }
        """;

    private static AppUpdateAsset AssetFor(byte[] content, string name = "Aria-Cockpit-0.27.0-leger.zip") =>
        new(name, Base + name, content.Length, Convert.ToHexStringLower(SHA256.HashData(content)));

    // ----- Lecture de la release -----

    [Fact]
    public void TheReleaseListsItsFilesWithSizeAndFingerprint()
    {
        var update = AppUpdateChecker.Parse(Release(Asset("Aria-Cockpit-0.27.0-leger.zip", size: 4811361)), Installed);

        var asset = Assert.Single(update!.Assets);
        Assert.Equal("Aria-Cockpit-0.27.0-leger.zip", asset.Name);
        Assert.Equal(4811361, asset.Size);
        Assert.Equal(Sha, asset.Sha256);
    }

    [Fact]
    public void AFileHostedElsewhereIsIgnored()
    {
        var json = Release(Asset("Aria-Cockpit-0.27.0-leger.zip", url: "https://exemple.test/Aria-Cockpit-0.27.0-leger.zip"));

        Assert.Empty(AppUpdateChecker.Parse(json, Installed)!.Assets);
    }

    [Fact]
    public void AFileFromAnotherRepositoryIsIgnored()
    {
        var json = Release(Asset("x.zip", url: "https://github.com/ZedjeK/Aria-Cockpit-autre/releases/download/v0.27.0/x.zip"));

        Assert.Empty(AppUpdateChecker.Parse(json, Installed)!.Assets);
    }

    [Theory]
    [InlineData("")]
    [InlineData("md5:0123456789abcdef0123456789abcdef")]
    [InlineData("sha256:abc")]
    [InlineData("sha256:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void AFileWithoutAUsableFingerprintIsIgnored(string digest)
    {
        var json = Release(Asset("Aria-Cockpit-0.27.0-leger.zip", digest: digest));

        Assert.Empty(AppUpdateChecker.Parse(json, Installed)!.Assets);
    }

    [Fact]
    public void TheFingerprintIsReadInLowerCase() =>
        Assert.Equal(Sha, AppUpdateChecker.ParseSha256("SHA256:" + Sha.ToUpperInvariant()));

    [Fact]
    public void AReleaseWithoutFilesIsStillAnUpdate()
    {
        var update = AppUpdateChecker.Parse("""{ "tag_name": "v0.27.0" }""", Installed);

        Assert.NotNull(update);
        Assert.Empty(update.Assets);
        Assert.Empty(update.Notes);
    }

    [Fact]
    public void TheNotesAreTheBulletsOfTheNewsSection()
    {
        var body = "Présentation\n\n- puce d'avant\n\n## Nouveautés de la 0.27.0\n\n- **Fond animé** : réglable dans `Paramètres`\n- Journal\n\n## Contenu\n\n- autre chose";

        var notes = AppUpdateChecker.ReadNotes(body);

        Assert.Equal(["Fond animé : réglable dans Paramètres", "Journal"], notes);
    }

    [Fact]
    public void WithoutANewsSectionTheFirstBulletsAreUsed() =>
        Assert.Equal(["un", "deux"], AppUpdateChecker.ReadNotes("Texte\n- un\n* deux\n"));

    [Fact]
    public void TheNotesAreCappedInNumberAndLength()
    {
        var body = "## Nouveautés\n" + string.Concat(Enumerable.Range(0, 9).Select(i => $"- {new string('x', 300)}{i}\n"));

        var notes = AppUpdateChecker.ReadNotes(body);

        Assert.Equal(AppUpdateChecker.MaxNotes, notes.Count);
        Assert.All(notes, n => Assert.True(n.Length <= AppUpdateChecker.MaxNoteLength && n.EndsWith('…')));
    }

    // ----- Choix du fichier -----

    [Fact]
    public void ABigExeIsTheSelfContainedOneAndASmallOneTheLightOne()
    {
        Assert.Equal(AppVariant.SelfContained, AppUpdatePackage.VariantOf(190_000_000));
        Assert.Equal(AppVariant.Light, AppUpdatePackage.VariantOf(9_000_000));
    }

    [Fact]
    public void EachVariantGetsItsOwnFile()
    {
        var update = AppUpdateChecker.Parse(
            Release(Asset("Aria-Cockpit-0.27.0-autonome.zip") + "," + Asset("Aria-Cockpit-0.27.0-leger.zip")), Installed)!;

        Assert.Equal("Aria-Cockpit-0.27.0-autonome.zip", AppUpdatePackage.Choose(update, AppVariant.SelfContained)!.Name);
        Assert.Equal("Aria-Cockpit-0.27.0-leger.zip", AppUpdatePackage.Choose(update, AppVariant.Light)!.Name);
    }

    [Fact]
    public void AFileWithAnUnexpectedNameIsNeverChosen()
    {
        var update = AppUpdateChecker.Parse(
            Release(Asset("Aria-Cockpit-0.27.0-leger.exe") + "," + Asset("../Aria-Cockpit-0.27.0-leger.zip")), Installed)!;

        Assert.Null(AppUpdatePackage.Choose(update, AppVariant.Light));
    }

    [Fact]
    public void AFileOfAnotherVersionIsNeverChosen()
    {
        // Bon nom, mais rangé sous le tag d'une autre version
        var update = AppUpdateChecker.Parse(Release(Asset("Aria-Cockpit-0.27.0-leger.zip",
            url: "https://github.com/ZedjeK/Aria-Cockpit/releases/download/v0.1.0/Aria-Cockpit-0.27.0-leger.zip")), Installed)!;

        Assert.Null(AppUpdatePackage.Choose(update, AppVariant.Light));
    }

    // ----- Adresses suivies -----

    [Theory]
    [InlineData("https://github.com/ZedjeK/Aria-Cockpit/releases/download/v0.27.0/x.zip", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2", true)]
    [InlineData("http://github.com/ZedjeK/Aria-Cockpit/releases/download/v0.27.0/x.zip", false)]
    [InlineData("https://github.com:8443/x.zip", false)]
    [InlineData("https://github.com.exemple.test/x.zip", false)]
    [InlineData("https://exemple.test/x.zip", false)]
    [InlineData("file:///C:/x.zip", false)]
    public void OnlyGitHubOverHttpsIsFollowed(string url, bool trusted) =>
        Assert.Equal(trusted, AppUpdatePackage.IsTrusted(new Uri(url)));

    // ----- Contrôle du fichier reçu -----

    [Fact]
    public async Task AFileThatMatchesTheReleaseIsKept()
    {
        var content = Encoding.UTF8.GetBytes("le bon fichier");
        var asset = AssetFor(content);
        var seen = new List<long>();

        var result = await AppUpdatePackage.SaveAsync(new MemoryStream(content), asset, _root, new Collect(seen), CancellationToken.None);

        Assert.Equal(DownloadOutcome.Ready, result.Outcome);
        Assert.Equal(AppUpdatePackage.FileFor(_root, asset), result.File);
        Assert.Equal(content, File.ReadAllBytes(result.File!));
        Assert.Equal(content.Length, seen[^1]);
        Assert.Equal(result.File, AppUpdatePackage.AlreadyDownloaded(_root, asset));
    }

    [Fact]
    public async Task AFileWithAnotherFingerprintIsDeleted()
    {
        var expected = Encoding.UTF8.GetBytes("le bon fichier");
        var received = Encoding.UTF8.GetBytes("le BON fichier");
        var asset = AssetFor(expected);

        var result = await AppUpdatePackage.SaveAsync(new MemoryStream(received), asset, _root, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Mismatch, result.Outcome);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(received)), result.Actual);
        Assert.Null(result.File);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task ATruncatedFileIsDeleted()
    {
        var expected = Encoding.UTF8.GetBytes("le bon fichier");
        var asset = AssetFor(expected);

        var result = await AppUpdatePackage.SaveAsync(new MemoryStream(expected[..5]), asset, _root, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Mismatch, result.Outcome);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task AFileBiggerThanAnnouncedIsStoppedAndDeleted()
    {
        var asset = AssetFor(new byte[10]);

        var result = await AppUpdatePackage.SaveAsync(new MemoryStream(new byte[500_000]), asset, _root, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Mismatch, result.Outcome);
        Assert.NotNull(result.Error);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task ACancelledDownloadLeavesNothingBehind()
    {
        var content = new byte[200_000];
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        var result = await AppUpdatePackage.SaveAsync(new MemoryStream(content), AssetFor(content), _root, null, cancel.Token);

        Assert.Equal(DownloadOutcome.Cancelled, result.Outcome);
        Assert.Empty(Directory.GetFiles(_root));
    }

    [Fact]
    public async Task ANewDownloadReplacesAnOlderOne()
    {
        var content = Encoding.UTF8.GetBytes("le bon fichier");
        var asset = AssetFor(content);
        Directory.CreateDirectory(_root);
        File.WriteAllText(AppUpdatePackage.FileFor(_root, asset), "reste d'un essai précédent");

        var result = await AppUpdatePackage.SaveAsync(new MemoryStream(content), asset, _root, null, CancellationToken.None);

        Assert.Equal(DownloadOutcome.Ready, result.Outcome);
        Assert.Equal(content, File.ReadAllBytes(result.File!));
    }

    [Fact]
    public void AFileChangedAfterTheDownloadIsNoLongerTrusted()
    {
        var content = Encoding.UTF8.GetBytes("le bon fichier");
        var asset = AssetFor(content);
        Directory.CreateDirectory(_root);
        // Même taille, autre contenu
        File.WriteAllText(AppUpdatePackage.FileFor(_root, asset), "le BON fichier");

        Assert.Null(AppUpdatePackage.AlreadyDownloaded(_root, asset));
    }

    [Fact]
    public void TheFileGoesToTheCacheUnderARecognisableName() =>
        Assert.Equal("maj-Aria-Cockpit-0.27.0-leger.zip", Path.GetFileName(AppUpdatePackage.FileFor(_root, AssetFor([1]))));

    private sealed class Collect(List<long> seen) : IProgress<long>
    {
        public void Report(long value) => seen.Add(value);
    }
}
