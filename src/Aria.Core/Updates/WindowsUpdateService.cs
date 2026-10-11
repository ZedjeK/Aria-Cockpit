using System.Runtime.InteropServices;

namespace Aria.Core.Updates;

/// <summary>
/// Windows Update, via l'interface officielle de Windows (« Windows Update Agent »).
/// Les objets COM obtenus à la recherche sont gardés pour l'installation : utiliser la même instance.
/// </summary>
public sealed class WindowsUpdateService
{
    // Classifications (identifiants fixes, indépendants de la langue de Windows)
    internal const string SecurityUpdates = "0FA1201D-4330-4FA8-8AE9-B877473B6441";
    internal const string DefinitionUpdates = "E0789628-CE08-4437-BE74-2495B842F43B";
    internal const string Upgrades = "3689BDC8-B205-4AF4-8D4A-A63924C5E9D5";
    internal const string FeaturePacks = "B54E7D24-7ADD-428F-8B75-90A396FA584F";
    internal const string Drivers = "EBFC1FC5-71A4-4F7B-9ACA-3B9A503104A0";

    private object? _session;

    /// <summary>Recherche bloquante (10 à 60 s) : à lancer hors du fil de l'interface.</summary>
    public ProviderResult Search()
    {
        try
        {
            _session = Com.Create("Microsoft.Update.Session");
            Com.Set(_session, "ClientApplicationID", "Aria");
            var searcher = Com.Call(_session, "CreateUpdateSearcher")!;
            var result = Com.Call(searcher, "Search", "IsInstalled=0 and IsHidden=0")!;

            var code = Convert.ToInt32(Com.Get(result, "ResultCode"));
            if (code is 4 or 5) return ProviderResult.Failed("La recherche Windows Update a échoué.");

            var items = Com.Items(Com.Get(result, "Updates")).Select(ToItem).ToList();
            return new ProviderResult(items, [], null);
        }
        catch (Exception e)
        {
            return ProviderResult.Failed(Describe(e));
        }
    }

    /// <summary>Télécharge puis installe une mise à jour trouvée par <see cref="Search"/>.</summary>
    public InstallResult Install(UpdateItem item)
    {
        if (_session is null || item.ComUpdate is not { } update)
            return InstallResult.Fail(item, "Recherche expirée : relancez la recherche des mises à jour.");

        try
        {
            if (Com.Get(update, "EulaAccepted") is false) Com.Call(update, "AcceptEula");

            var collection = Com.Create("Microsoft.Update.UpdateColl");
            Com.Call(collection, "Add", update);

            if (Com.Get(update, "IsDownloaded") is not true)
            {
                var downloader = Com.Call(_session, "CreateUpdateDownloader")!;
                Com.SetObject(downloader, "Updates", collection);
                var download = Com.Call(downloader, "Download")!;
                var downloadCode = Convert.ToInt32(Com.Get(download, "ResultCode"));
                if (downloadCode is not (2 or 3))
                    return InstallResult.Fail(item, $"Téléchargement impossible ({ResultText(downloadCode)}).");
            }

            var installer = Com.Call(_session, "CreateUpdateInstaller")!;
            Com.SetObject(installer, "Updates", collection);
            var install = Com.Call(installer, "Install")!;

            var code = Convert.ToInt32(Com.Get(install, "ResultCode"));
            var reboot = Com.Get(install, "RebootRequired") is true;
            if (code is 2 or 3) return InstallResult.Ok(item, reboot);

            var hresult = Convert.ToInt32(Com.Get(Com.Call(install, "GetUpdateResult", 0)!, "HResult"));
            return InstallResult.Fail(item, hresult != 0 ? DescribeHResult(hresult) : $"Installation {ResultText(code)}.");
        }
        catch (Exception e)
        {
            return InstallResult.Fail(item, Describe(e));
        }
    }

    private static UpdateItem ToItem(object update)
    {
        var classes = Com.Items(Com.Get(update, "Categories"))
            .Where(c => Com.Str(c, "Type") == "UpdateClassification")
            .Select(c => Com.Str(c, "CategoryID").ToUpperInvariant())
            .ToHashSet();

        var kb = Com.Items(Com.Get(update, "KBArticleIDs")).Select(k => k.ToString()).FirstOrDefault(k => !string.IsNullOrEmpty(k));
        var behavior = Com.Get(update, "InstallationBehavior");
        var reboot = behavior is not null && Convert.ToInt32(Com.Get(behavior, "RebootBehavior")) != 0;

        var (flags, selected, reason) = Classify(
            isDriver: Convert.ToInt32(Com.Get(update, "Type")) == 2,
            browseOnly: Com.Get(update, "BrowseOnly") is true,
            classifications: classes,
            msrcSeverity: Com.Get(update, "MsrcSeverity") as string,
            title: Com.Str(update, "Title"),
            requiresReboot: reboot);

        return new UpdateItem
        {
            Key = $"wu:{Com.Str(Com.Get(update, "Identity")!, "UpdateID")}",
            Source = UpdateSource.WindowsUpdate,
            Title = Com.Str(update, "Title"),
            NewVersion = kb is null ? null : $"KB{kb}",
            Reason = reason,
            SizeBytes = Convert.ToInt64(Com.Get(update, "MaxDownloadSize")),
            Flags = flags,
            SelectedByDefault = selected,
            ComUpdate = update
        };
    }

    /// <summary>
    /// Ce qui est coché d'office : sécurité, correctifs et antivirus. Jamais les pilotes (ils peuvent remplacer
    /// un pilote NVIDIA plus récent), les nouvelles versions de Windows ni les préversions facultatives.
    /// </summary>
    internal static (UpdateFlags Flags, bool Selected, string Reason) Classify(
        bool isDriver, bool browseOnly, IReadOnlySet<string> classifications, string? msrcSeverity, string title, bool requiresReboot)
    {
        var reboot = requiresReboot ? UpdateFlags.Reboot : UpdateFlags.None;

        if (isDriver || classifications.Contains(Drivers))
            return (UpdateFlags.Driver | reboot, false,
                "Pilote proposé par Windows Update : il peut remplacer un pilote plus récent installé par le fabricant (NVIDIA, Intel…). À installer en connaissance de cause.");

        if (classifications.Contains(Upgrades) || classifications.Contains(FeaturePacks) ||
            title.Contains("Feature update", StringComparison.OrdinalIgnoreCase) ||
            title.Contains("fonctionnalités", StringComparison.OrdinalIgnoreCase))
            return (UpdateFlags.Major | UpdateFlags.Reboot, false,
                "Nouvelle version de Windows : installation longue avec plusieurs redémarrages. À lancer quand vous avez le temps.");

        if (browseOnly)
            return (UpdateFlags.Optional | reboot, false,
                "Mise à jour facultative (préversion) : elle anticipe le correctif du mois suivant ; mieux vaut attendre sa version définitive.");

        if (classifications.Contains(SecurityUpdates) || !string.IsNullOrWhiteSpace(msrcSeverity))
        {
            var level = msrcSeverity switch
            {
                "Critical" => " critiques",
                "Important" => " importantes",
                _ => ""
            };
            return (UpdateFlags.Security | reboot, true, $"Corrige des failles de sécurité{level}.");
        }

        if (classifications.Contains(DefinitionUpdates))
            return (UpdateFlags.Definitions | reboot, true, "Définitions antivirus de Microsoft Defender.");

        return (reboot, true, "Correctifs de fiabilité de Windows ou de ses composants.");
    }

    private static string ResultText(int code) => code switch
    {
        3 => "réussie avec des erreurs",
        4 => "échouée",
        5 => "interrompue",
        _ => $"code {code}"
    };

    internal static string Describe(Exception e) => e is COMException com ? DescribeHResult(com.HResult) : e.Message;

    internal static string DescribeHResult(int hresult) => unchecked((uint)hresult) switch
    {
        0x8024402C or 0x80072EFD or 0x80072EE2 or 0x80072EE7 or 0x8024401C or 0x80244022
            => "Windows Update est injoignable (connexion internet ou proxy).",
        0x8024002E => "Windows Update est désactivé par une stratégie de l'organisation.",
        0x80240016 => "Une autre installation est en cours, ou un redémarrage est attendu : redémarrez puis réessayez.",
        0x80070422 => "Le service Windows Update est désactivé.",
        0x8024001E => "Le service Windows Update s'est arrêté pendant l'opération.",
        0x80240044 or 0x80070005 => "Accès refusé : droits administrateur nécessaires.",
        0x80070070 => "Espace disque insuffisant.",
        var code => $"Erreur Windows Update 0x{code:X8}."
    };
}
