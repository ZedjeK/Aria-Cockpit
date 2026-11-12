using System.Security.Cryptography;
using Aria.Core.Backup;

namespace Aria.Core.Updates;

/// <summary>Les deux fichiers d'une release : l'exe autonome (runtime .NET inclus) et l'exe léger.</summary>
public enum AppVariant
{
    SelfContained,
    Light
}

public enum DownloadOutcome
{
    /// <summary>Fichier complet, taille et empreinte identiques à celles de la release.</summary>
    Ready,
    /// <summary>Taille ou empreinte différente : le fichier a été supprimé.</summary>
    Mismatch,
    /// <summary>Réseau, disque ou adresse refusée : rien n'est gardé.</summary>
    Failed,
    /// <summary>Annulé par l'utilisateur : le fichier partiel a été supprimé.</summary>
    Cancelled
}

/// <param name="File">Chemin du fichier contrôlé, quand <see cref="Outcome"/> vaut Ready.</param>
/// <param name="Actual">Empreinte calculée sur ce qui a été reçu, quand elle a pu l'être.</param>
/// <param name="Error">Raison de l'échec, en clair.</param>
public sealed record DownloadResult(DownloadOutcome Outcome, string? File = null, string? Actual = null, string? Error = null);

/// <summary>
/// Téléchargement d'une nouvelle version d'Aria Cockpit : le fichier vient de la release de ce dépôt, en HTTPS, il est
/// écrit dans le cache du dossier des données (modifiable par les seuls administrateurs), et il n'est gardé que si sa
/// taille et son empreinte SHA-256 sont celles que GitHub annonce. Rien n'est installé ici.
/// L'empreinte prouve que le fichier est arrivé intact, pas qui l'a publié.
/// </summary>
public static class AppUpdatePackage
{
    /// <summary>Au-delà de cette taille, l'exe est l'autonome (une centaine de Mo) ; en dessous, le léger (quelques Mo).</summary>
    public const long SelfContainedThreshold = 40L * 1024 * 1024;

    /// <summary>Préfixe des fichiers de mise à jour dans le cache.</summary>
    public const string FilePrefix = "maj-";

    internal const int MaxRedirects = 5;

    /// <summary>Sans octet reçu pendant ce délai, le téléchargement est abandonné.</summary>
    internal static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Hôtes acceptés : github.com pour l'adresse de la release, puis le serveur de fichiers vers lequel GitHub
    /// redirige (relevé le 12/11/2026 : release-assets.githubusercontent.com).
    /// </summary>
    private static readonly string[] TrustedHosts =
        ["github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"];

    private static readonly HttpClient Http = CreateClient();

    public static AppVariant VariantOf(long exeSize) => exeSize >= SelfContainedThreshold ? AppVariant.SelfContained : AppVariant.Light;

    /// <summary>Variante de l'exe en cours, d'après sa taille ; null si elle est illisible.</summary>
    public static AppVariant? CurrentVariant()
    {
        try
        {
            return Environment.ProcessPath is { } exe ? VariantOf(new FileInfo(exe).Length) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Fichier de la release pour cette variante. Le nom et l'adresse doivent être exactement ceux qu'Aria publie :
    /// rien d'autre n'est téléchargé, même si la release contient d'autres fichiers.
    /// </summary>
    public static AppUpdateAsset? Choose(AppUpdate update, AppVariant variant)
    {
        var version = update.Latest.ToString(3);
        var name = $"Aria-Cockpit-{version}-{(variant == AppVariant.SelfContained ? "autonome" : "leger")}.zip";
        var url = $"{AppUpdateChecker.DownloadPrefix}v{version}/{name}";
        return update.Assets.FirstOrDefault(a => a.Name == name && a.Url == url);
    }

    /// <summary>Emplacement du fichier dans le cache.</summary>
    public static string FileFor(AppUpdateAsset asset) => FileFor(DataCache.Folder, asset);

    internal static string FileFor(string folder, AppUpdateAsset asset) => Path.Combine(folder, FilePrefix + asset.Name);

    /// <summary>Chemin du fichier s'il est déjà dans le cache, complet et conforme ; null sinon.</summary>
    public static string? AlreadyDownloaded(AppUpdateAsset asset) => AlreadyDownloaded(DataCache.Folder, asset);

    internal static string? AlreadyDownloaded(string folder, AppUpdateAsset asset)
    {
        try
        {
            var file = new FileInfo(FileFor(folder, asset));
            if (!file.Exists || file.Length != asset.Size) return null;
            return Sha256Of(file.FullName) == asset.Sha256 ? file.FullName : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string Sha256Of(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>HTTPS, port par défaut, et un hôte de GitHub : seules adresses qu'Aria accepte de suivre.</summary>
    internal static bool IsTrusted(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && TrustedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Télécharge le fichier dans le cache et le contrôle. Ne lève jamais : le résultat dit ce qui s'est passé.
    /// </summary>
    /// <param name="progress">Octets reçus, au fil du téléchargement.</param>
    public static Task<DownloadResult> DownloadAsync(AppUpdateAsset asset, IProgress<long>? progress, CancellationToken cancel) =>
        DownloadAsync(asset, DataCache.Folder, progress, cancel);

    internal static async Task<DownloadResult> DownloadAsync(
        AppUpdateAsset asset, string folder, IProgress<long>? progress, CancellationToken cancel)
    {
        if (!asset.Url.StartsWith(AppUpdateChecker.DownloadPrefix, StringComparison.Ordinal))
            return new DownloadResult(DownloadOutcome.Failed, Error: "L'adresse du fichier n'est pas celle d'une release d'Aria Cockpit.");
        try
        {
            var uri = new Uri(asset.Url);
            for (var hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!IsTrusted(uri))
                    return new DownloadResult(DownloadOutcome.Failed, Error: $"Adresse refusée : « {uri.Host} » n'est pas un serveur de GitHub.");
                using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                if (status is >= 300 and < 400 && response.Headers.Location is { } next)
                {
                    uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    return new DownloadResult(DownloadOutcome.Failed, Error: $"GitHub a répondu {status}.");
                await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                return await SaveAsync(stream, asset, folder, progress, cancel).ConfigureAwait(false);
            }
            return new DownloadResult(DownloadOutcome.Failed, Error: "Trop de redirections.");
        }
        catch (OperationCanceledException)
        {
            return cancel.IsCancellationRequested
                ? new DownloadResult(DownloadOutcome.Cancelled)
                : new DownloadResult(DownloadOutcome.Failed, Error: "GitHub n'a pas répondu à temps.");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UriFormatException)
        {
            return new DownloadResult(DownloadOutcome.Failed, Error: e.Message);
        }
    }

    /// <summary>
    /// Écrit le flux dans <paramref name="folder"/> en calculant son empreinte, puis ne garde le fichier que si la
    /// taille et l'empreinte sont celles de la release. Dans tous les autres cas, le fichier partiel est supprimé.
    /// </summary>
    internal static async Task<DownloadResult> SaveAsync(
        Stream source, AppUpdateAsset asset, string folder, IProgress<long>? progress, CancellationToken cancel)
    {
        var final = FileFor(folder, asset);
        var partial = final + ".partiel";
        try
        {
            Directory.CreateDirectory(folder);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            long total = 0;
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    var read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    total += read;
                    // Plus gros qu'annoncé : inutile de remplir le disque pour le constater à la fin
                    if (total > asset.Size) break;
                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                    progress?.Report(total);
                }
            }
            if (total > asset.Size)
            {
                Delete(partial);
                return new DownloadResult(DownloadOutcome.Mismatch, Error: "Le fichier reçu est plus gros que celui de la release.");
            }
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (total != asset.Size || actual != asset.Sha256)
            {
                Delete(partial);
                return new DownloadResult(DownloadOutcome.Mismatch, Actual: actual);
            }
            File.Move(partial, final, overwrite: true);
            return new DownloadResult(DownloadOutcome.Ready, final, actual);
        }
        catch (OperationCanceledException)
        {
            Delete(partial);
            return cancel.IsCancellationRequested
                ? new DownloadResult(DownloadOutcome.Cancelled)
                : new DownloadResult(DownloadOutcome.Failed, Error: "Le téléchargement s'est arrêté : plus rien reçu depuis une minute.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Delete(partial);
            return new DownloadResult(DownloadOutcome.Failed, Error: e.Message);
        }
    }

    private static void Delete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fichier encore tenu : le ménage du cache le retirera
        }
    }

    private static HttpClient CreateClient()
    {
        // Les redirections sont suivies à la main, pour contrôler chaque hôte
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Aria-Cockpit");
        return client;
    }
}
