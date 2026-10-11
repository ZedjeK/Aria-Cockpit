using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aria.Core.Models;

namespace Aria.Core.Updates;

/// <summary>Dernier pilote Game Ready disponible, comparé au pilote installé.</summary>
public sealed record NvidiaDriverCheck(
    string GpuName,
    string? InstalledVersion,
    string? LatestVersion,
    DateTime? LatestDate,
    string? DownloadUrl,
    string? DetailsUrl,
    string? FileSize,
    string? Error)
{
    public bool UpdateAvailable =>
        Error is null && NvidiaParser.IsNewer(LatestVersion, InstalledVersion);
}

/// <summary>Une entrée « Game Ready » renvoyée par le service de NVIDIA.</summary>
public sealed record NvidiaDriverInfo(string Version, DateTime? Date, string DownloadUrl, string? DetailsUrl, string? FileSize);

/// <summary>
/// Vérification via le service de recherche du site NVIDIA (non officiel, celui qu'utilise la page de téléchargement).
/// Le service attend un identifiant de modèle (« pfid ») : il vient de la table publique ZenitH-AT/nvidia-data
/// (domaine public), téléchargée à jour si possible, sinon la copie intégrée à Aria.
/// </summary>
public static class NvidiaDriverService
{
    private const string LookupUrl =
        "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php?func=DriverManualLookup";
    private const string GpuTableUrl = "https://raw.githubusercontent.com/ZenitH-AT/nvidia-data/main/gpu-data.json";

    private static readonly HttpClient Http = CreateClient();

    public static async Task<NvidiaDriverCheck?> CheckAsync(HardwareProfile hw, CancellationToken cancellation = default)
    {
        if (hw.NvidiaGpu is not { } gpu) return null;

        var installed = gpu.VendorDriverVersion;
        var name = NvidiaParser.NormalizeGpuName(gpu.Name);
        NvidiaDriverCheck Fail(string error) => new(name, installed, null, null, null, null, null, error);

        try
        {
            using var table = await LoadGpuTableAsync(cancellation);
            if (NvidiaParser.FindProductId(table, name, hw.IsLaptop) is not { } pfid)
                return Fail("Modèle absent de la table des cartes NVIDIA (carte très récente ?) : vérifiez avec l'application NVIDIA.");

            // 135 = Windows 11, 57 = Windows 10 64 bits (identifiants du site NVIDIA)
            var osId = Environment.OSVersion.Version.Build >= 22000 ? 135 : 57;
            var json = await Http.GetStringAsync($"{LookupUrl}&pfid={pfid}&osID={osId}&dch=1&numberOfResults=10&languageCode=1078", cancellation);

            return NvidiaParser.ParseLatestGameReady(json) is { } latest
                ? new NvidiaDriverCheck(name, installed, latest.Version, latest.Date, latest.DownloadUrl, latest.DetailsUrl, latest.FileSize, null)
                : Fail("Le site NVIDIA n'a renvoyé aucun pilote pour cette carte.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return Fail("Site NVIDIA injoignable : vérifiez la connexion internet (ou le pare-feu de l'entreprise).");
        }
        catch (JsonException)
        {
            return Fail("Réponse du site NVIDIA illisible : le service a peut-être changé. Vérifiez avec l'application NVIDIA.");
        }
    }

    /// <summary>Table à jour depuis GitHub, sinon la copie intégrée.</summary>
    private static async Task<JsonDocument> LoadGpuTableAsync(CancellationToken cancellation)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            return JsonDocument.Parse(await Http.GetStringAsync(GpuTableUrl, timeout.Token));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Aria.Nvidia.gpu-data.json")
                               ?? throw new InvalidOperationException("Table des cartes NVIDIA absente.");
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellation);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) Aria");
        return client;
    }
}

public static partial class NvidiaParser
{
    /// <summary>« NVIDIA GeForce RTX 4080 Super (TM) 16GB » → « GeForce RTX 4080 SUPER » (format de la table).</summary>
    public static string NormalizeGpuName(string name)
    {
        var n = name.Trim();
        if (n.StartsWith("NVIDIA ", StringComparison.OrdinalIgnoreCase)) n = n[7..];
        n = Suffixes().Replace(n, "");
        n = SuperWord().Replace(n, "SUPER");
        return n.Trim();
    }

    /// <summary>Identifiant de modèle : d'abord dans la catégorie de la machine (portable / fixe), puis dans l'autre (carte externe).</summary>
    public static int? FindProductId(JsonDocument table, string gpuName, bool isLaptop)
    {
        foreach (var category in isLaptop ? new[] { "notebook", "desktop" } : ["desktop", "notebook"])
        {
            if (!table.RootElement.TryGetProperty(category, out var models)) continue;
            foreach (var model in models.EnumerateObject())
            {
                if (!string.Equals(model.Name, gpuName, StringComparison.OrdinalIgnoreCase)) continue;
                if (model.Value.ValueKind == JsonValueKind.Number && model.Value.TryGetInt32(out var id)) return id;
                if (model.Value.ValueKind == JsonValueKind.String && int.TryParse(model.Value.GetString(), out id)) return id;
            }
        }
        return null;
    }

    /// <summary>Premier pilote « Game Ready » stable (IsCRD = 0 : Game Ready ; 1 : Studio) de la réponse.</summary>
    public static NvidiaDriverInfo? ParseLatestGameReady(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array) return null;

        foreach (var entry in ids.EnumerateArray())
        {
            if (!entry.TryGetProperty("downloadInfo", out var info)) continue;
            if (Text(info, "IsCRD") != "0" || Text(info, "IsBeta") == "1") continue;

            var version = Text(info, "Version");
            var url = Text(info, "DownloadURL");
            if (version is null || url is null) continue;

            return new NvidiaDriverInfo(
                version,
                ParseDate(Text(info, "ReleaseDateTime")),
                url,
                Localize(Text(info, "DetailsURL")),
                Text(info, "DownloadURLFileSize"));
        }
        return null;
    }

    public static bool IsNewer(string? candidate, string? installed) =>
        Version.TryParse(candidate, out var a) && Version.TryParse(installed, out var b) && a > b;

    /// <summary>« 2026 October 6 » (format du service), avec quelques variantes par sécurité.</summary>
    internal static DateTime? ParseDate(string? text) =>
        text is not null && DateTime.TryParseExact(text.Trim(),
            ["yyyy MMMM d", "yyyy MMMM dd", "ddd MMM d, yyyy", "ddd MMM dd, yyyy", "yyyy-MM-dd"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>Page de détails en français plutôt qu'en anglais britannique.</summary>
    private static string? Localize(string? url) => url is null ? null : LocaleSegment().Replace(url, "/fr-fr/drivers/", 1);

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value)
            ? value.ValueKind switch
            {
                JsonValueKind.String => Uri.UnescapeDataString(value.GetString() ?? ""),
                JsonValueKind.Number => value.GetRawText(),
                _ => null
            }
            : null;

    [GeneratedRegex(@"\s+\([A-Z]+\)$|\s+\d+GB$|\s+with Max-Q Design$|\s+COLLECTORS EDITION$", RegexOptions.IgnoreCase)]
    private static partial Regex Suffixes();

    [GeneratedRegex(@"\bSuper\b", RegexOptions.IgnoreCase)]
    private static partial Regex SuperWord();

    [GeneratedRegex(@"/[a-z]{2}-[a-z]{2}/drivers/")]
    private static partial Regex LocaleSegment();
}
