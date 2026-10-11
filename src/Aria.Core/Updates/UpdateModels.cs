namespace Aria.Core.Updates;

public enum UpdateSource { WindowsUpdate, Application }

[Flags]
public enum UpdateFlags
{
    None = 0,
    Security = 1,
    Reboot = 2,
    /// <summary>Nouvelle version de Windows (mise à jour des fonctionnalités).</summary>
    Major = 4,
    /// <summary>Mise à jour facultative (préversion).</summary>
    Optional = 8,
    /// <summary>Pilote proposé par Windows Update.</summary>
    Driver = 16,
    /// <summary>winget ne connaît pas la version installée.</summary>
    UnknownVersion = 32,
    /// <summary>Définitions antivirus Microsoft Defender.</summary>
    Definitions = 64
}

/// <summary>Une mise à jour proposée, prête à être cochée puis installée.</summary>
public sealed class UpdateItem
{
    public required string Key { get; init; }
    public required UpdateSource Source { get; init; }
    public required string Title { get; init; }
    public string? CurrentVersion { get; init; }
    /// <summary>Nouvelle version (applications) ou numéro KB (Windows Update).</summary>
    public string? NewVersion { get; init; }
    public required string Reason { get; init; }
    public long? SizeBytes { get; init; }
    public UpdateFlags Flags { get; init; }
    public bool SelectedByDefault { get; init; }

    // Données d'installation, propres à chaque source
    internal string? PackageId { get; init; }
    internal string? PackageSource { get; init; }
    internal object? ComUpdate { get; init; }
}

/// <summary>Élément trouvé mais non proposé (ligne illisible, identifiant tronqué…).</summary>
public sealed record SkippedUpdate(string Title, string Reason);

public sealed record ProviderResult(IReadOnlyList<UpdateItem> Items, IReadOnlyList<SkippedUpdate> Skipped, string? Error)
{
    public static ProviderResult Failed(string error) => new([], [], error);
}

public enum InstallStatus { Installed, UpToDate, Failed, Canceled }

public sealed record InstallResult(UpdateItem Item, InstallStatus Status, string? Error, bool RebootRequired)
{
    public static InstallResult Ok(UpdateItem item, bool reboot = false) => new(item, InstallStatus.Installed, null, reboot);
    public static InstallResult Fail(UpdateItem item, string error) => new(item, InstallStatus.Failed, error, false);
}

/// <param name="Detail">Précision sur l'élément en cours (ex. « Téléchargement · 45 % »).</param>
/// <param name="Fraction">Avancement de l'élément en cours (0 à 1), si connu.</param>
public sealed record InstallStep(int Done, int Total, string Text, string? Detail = null, double? Fraction = null);

/// <param name="ClosedApps">Ce qui est arrivé aux applications ouvertes fermées avant leur mise à jour.</param>
public sealed record InstallOutcome(
    IReadOnlyList<InstallResult> Results,
    bool RestorePointCreated,
    string RestorePointMessage,
    IReadOnlyList<string>? ClosedApps = null);
