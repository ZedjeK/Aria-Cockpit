using System.Diagnostics;
using Microsoft.Win32;
using Aria.Core.Models;
using Aria.Core.Settings;
using Aria.Core.Windows;

namespace Aria.Core.Privacy;

/// <summary>
/// Groupe « Confidentialité » : moins de données envoyées à Microsoft, moins de publicités et de suggestions,
/// quelques services inutiles coupés selon le matériel. Tout est sauvegardé et restaurable.
/// Les Widgets n'y figurent pas : leur stratégie officielle (Dsh\AllowNewsAndInterests) est verrouillée par Windows,
/// même pour un administrateur (constaté sur Windows 11 25H2 le 09/10/2026).
/// Sources : Policy CSP Microsoft (System, Experience, Privacy) et « Manage connections from
/// Windows operating system components to Microsoft services » (learn.microsoft.com).
/// </summary>
public static class PrivacySettings
{
    public static IEnumerable<ISetting> Build(HardwareProfile hw)
    {
        yield return new DiagnosticDataSetting();
        yield return new FeedbackSetting();
        yield return new TailoredExperiencesSetting();
        yield return new AdvertisingIdSetting();
        yield return new ActivityHistorySetting();
        yield return new StartWebSearchSetting();
        yield return new StartRecommendationsSetting();
        yield return new WindowsSuggestionsSetting();
        yield return new AppLaunchTrackingSetting();
        yield return new LanguageListSetting();
        yield return new CopilotAppSetting();

        yield return new ServiceStartSetting("DiagTrack", "Service de télémétrie (Expériences utilisateur connectées)",
            selected: true, "Envoie les données de diagnostic à Microsoft. Le couper n'a aucun effet sur le fonctionnement de Windows.");
        yield return new SpoolerSetting();
        yield return new ServiceStartSetting("Fax", "Service de télécopie (Fax)",
            selected: true, "Envoi et réception de fax par modem : inutile sans ligne de fax.");
        foreach (var xbox in XboxServices)
            yield return new XboxServiceSetting(xbox.Name, xbox.DisplayName);
        yield return new ServiceStartSetting("lfsvc", "Service de géolocalisation",
            selected: false, "Position du PC pour les applications. Sur un PC fixe, sert surtout au fuseau horaire automatique et à la météo locale.",
            whyNot: hw2 => hw2.IsLaptop ? "Portable : la position sert à Localiser mon appareil" : null);
    }

    /// <summary>Services Xbox Live. Pas le service des accessoires (XboxGipSvc), utile aux manettes Xbox filaires.</summary>
    private static readonly (string Name, string DisplayName)[] XboxServices =
    [
        ("XblAuthManager", "Service Xbox : authentification Xbox Live"),
        ("XblGameSave", "Service Xbox : sauvegardes de jeu dans le cloud"),
        ("XboxNetApiSvc", "Service Xbox : réseau Xbox Live")
    ];
}

// ================================================================ Registre (une valeur)

/// <summary>Réglage de confidentialité stocké dans une valeur DWORD du registre.</summary>
public abstract class PrivacyRegistrySetting(RegistryKey hive, string keyPath, string valueName, int? valueWhenMissing)
    : RegistryDwordSetting(hive, keyPath, valueName, valueWhenMissing)
{
    public override SettingCategory Category => SettingCategory.Privacy;
}

public sealed class DiagnosticDataSetting() : PrivacyRegistrySetting(
    Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "AllowTelemetry", valueWhenMissing: null)
{
    public override string Id => "privacy.telemetry";
    public override string DisplayName => "Données de diagnostic envoyées à Microsoft";
    protected override string Describe(int value) => value switch
    {
        0 or 1 => "Obligatoires uniquement (minimum)",
        3 => "Facultatives (complètes)",
        _ => $"Niveau {value}"
    };

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(1), "Le minimum autorisé par Windows 11 Pro : seules les données nécessaires à la sécurité et aux mises à jour. " +
                      "(Le niveau zéro est réservé aux éditions Entreprise et Éducation.)");
}

public sealed class FeedbackSetting() : PrivacyRegistrySetting(
    Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", valueWhenMissing: 0)
{
    public override string Id => "privacy.feedback";
    public override string DisplayName => "Demandes de commentaires de Windows";
    protected override string Describe(int value) => value == 1 ? "Jamais" : "Autorisées";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(1), "Plus de fenêtres « Que pensez-vous de… ? » de Windows.");
}

public sealed class TailoredExperiencesSetting() : PrivacyRegistrySetting(
    Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\CloudContent", "DisableTailoredExperiencesWithDiagnosticData", valueWhenMissing: 0)
{
    public override string Id => "privacy.tailored";
    public override string DisplayName => "Expériences personnalisées (pubs et conseils selon vos données)";
    protected override string Describe(int value) => value == 1 ? "Désactivées" : "Activées";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(1), "Microsoft n'utilise plus vos données de diagnostic pour vous proposer des conseils, offres et publicités.");
}

public sealed class AdvertisingIdSetting() : PrivacyRegistrySetting(
    Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo", "DisabledByGroupPolicy", valueWhenMissing: 0)
{
    public override string Id => "privacy.advertisingid";
    public override string DisplayName => "Identifiant de publicité";
    protected override string Describe(int value) => value == 1 ? "Désactivé" : "Au choix des applications";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(1), "Les applications ne peuvent plus vous suivre d'une application à l'autre pour cibler leurs publicités.");
}

public sealed class ActivityHistorySetting() : PrivacyRegistrySetting(
    Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", valueWhenMissing: 1)
{
    public override string Id => "privacy.activity";
    public override string DisplayName => "Historique d'activité";
    protected override string Describe(int value) => value == 0 ? "Désactivé" : "Activé";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(0), "Windows ne garde plus l'historique des applications, fichiers et sites utilisés.");
}

public sealed class StartWebSearchSetting() : PrivacyRegistrySetting(
    Registry.CurrentUser, @"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", valueWhenMissing: 0)
{
    public override string Id => "privacy.startwebsearch";
    public override string DisplayName => "Recherche Bing dans le menu Démarrer";
    public override bool RequiresReboot => true;
    protected override string Describe(int value) => value == 1 ? "Désactivée" : "Activée";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(1), "La recherche du menu Démarrer reste sur le PC, sans résultats Bing. Effet secondaire : plus de recherches récentes " +
                      "dans la barre de recherche de l'Explorateur. Effet à la prochaine ouverture de session.");
}

public sealed class StartRecommendationsSetting() : PrivacyRegistrySetting(
    Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_IrisRecommendations", valueWhenMissing: 1)
{
    public override string Id => "privacy.startrecommendations";
    public override string DisplayName => "Recommandations du menu Démarrer (astuces, applications…)";
    protected override string Describe(int value) => value == 0 ? "Masquées" : "Affichées";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(0), "Plus d'astuces ni d'applications mises en avant par Microsoft dans le menu Démarrer. Vos fichiers récents restent.");
}

public sealed class AppLaunchTrackingSetting() : PrivacyRegistrySetting(
    Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs", valueWhenMissing: 1)
{
    public override string Id => "privacy.applaunch";
    public override string DisplayName => "Suivi des applications lancées";
    protected override string Describe(int value) => value == 0 ? "Désactivé" : "Activé";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(0), "Windows ne note plus les applications que vous lancez. La liste « Les plus utilisées » du menu Démarrer disparaît.");
}

public sealed class LanguageListSetting() : PrivacyRegistrySetting(
    Registry.CurrentUser, @"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", valueWhenMissing: 0)
{
    public override string Id => "privacy.languagelist";
    public override string DisplayName => "Accès des sites web à votre liste de langues";
    protected override string Describe(int value) => value == 1 ? "Refusé" : "Autorisé";

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Value(1), "Les sites ne reçoivent plus la liste de vos langues installées, une information qui aide à vous identifier.");
}

// ================================================================ Registre (plusieurs valeurs)

/// <summary>
/// Suggestions et « astuces » de Windows (Paramètres, écran de verrouillage, notifications).
/// Les stratégies équivalentes sont réservées aux éditions Entreprise et Éducation : on écrit donc les options
/// des Paramètres elles-mêmes (ContentDeliveryManager), ainsi que la stratégie des suggestions tierces, valable sur Pro.
/// Valeur technique : les valeurs dans l'ordre, séparées par « ; » (« absent » si non définie).
/// </summary>
public sealed class WindowsSuggestionsSetting : SettingBase
{
    private const string Absent = "absent";
    private const string Cdm = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";

    // (chemin, nom, valeur si absente, valeur voulue)
    private static readonly (string Path, string Name, int Missing, int Wanted)[] Values =
    [
        (Cdm, "SubscribedContent-338393Enabled", 1, 0),   // contenu suggéré dans Paramètres
        (Cdm, "SubscribedContent-353694Enabled", 1, 0),   // contenu suggéré dans Paramètres
        (Cdm, "SubscribedContent-353696Enabled", 1, 0),   // contenu suggéré dans Paramètres
        (Cdm, "SubscribedContent-338389Enabled", 1, 0),   // astuces et suggestions pendant l'utilisation
        (Cdm, "SubscribedContent-338387Enabled", 1, 0),   // anecdotes et astuces de l'écran de verrouillage
        (Cdm, "RotatingLockScreenOverlayEnabled", 1, 0),  // idem (superposition)
        (Cdm, "SystemPaneSuggestionsEnabled", 1, 0),      // suggestions d'applications
        (@"Software\Policies\Microsoft\Windows\CloudContent", "DisableThirdPartySuggestions", 0, 1)
    ];

    public override string Id => "privacy.suggestions";
    public override string DisplayName => "Suggestions et astuces de Windows (Paramètres, écran de verrouillage)";
    public override SettingCategory Category => SettingCategory.Privacy;

    public override SettingValue ReadCurrent(HardwareProfile hw)
    {
        var raw = Values.Select(v =>
        {
            using var key = Registry.CurrentUser.OpenSubKey(v.Path);
            return key?.GetValue(v.Name) is int i ? i.ToString() : Absent;
        }).ToList();
        return new SettingValue(string.Join(";", raw), Describe(raw));
    }

    public override SettingTarget ComputeTarget(HardwareProfile hw)
    {
        var raw = Values.Select(v => v.Wanted.ToString()).ToList();
        return new(new SettingValue(string.Join(";", raw), Describe(raw)),
            "Plus de contenus « suggérés » ni de publicités dans les Paramètres, les notifications et l'écran de verrouillage.");
    }

    public override bool IsAtTarget(SettingValue current, SettingValue target) =>
        Effective(current.Raw).SequenceEqual(Effective(target.Raw));

    public override void Apply(HardwareProfile hw, SettingValue value)
    {
        var parts = value.Raw.Split(';');
        if (parts.Length != Values.Length) throw new FormatException($"Valeur inattendue : {value.Raw}");
        for (var i = 0; i < Values.Length; i++)
        {
            using var key = Registry.CurrentUser.CreateSubKey(Values[i].Path, writable: true);
            if (parts[i] == Absent) key.DeleteValue(Values[i].Name, throwOnMissingValue: false);
            else key.SetValue(Values[i].Name, int.Parse(parts[i]), RegistryValueKind.DWord);
        }
    }

    /// <summary>Valeurs effectives (« absent » remplacé par la valeur par défaut de Windows).</summary>
    internal static IEnumerable<int> Effective(string raw)
    {
        var parts = raw.Split(';');
        return Values.Select((v, i) => i < parts.Length && int.TryParse(parts[i], out var n) ? n : v.Missing);
    }

    private static string Describe(IReadOnlyList<string> raw)
    {
        var effective = Effective(string.Join(";", raw)).ToList();
        var off = effective.Select((value, i) => value == Values[i].Wanted).Count(x => x);
        return off == Values.Length ? "Désactivées" : off == 0 ? "Activées" : $"En partie ({off}/{Values.Length} désactivées)";
    }
}

// ================================================================ Copilot

/// <summary>
/// Application Copilot. La stratégie « Désactiver Copilot » ne concerne plus la nouvelle application :
/// la seule méthode fiable est de la désinstaller (pour cet utilisateur). Réinstallable depuis le Microsoft Store.
/// </summary>
public sealed class CopilotAppSetting : SettingBase
{
    private const string Package = "Microsoft.Copilot";
    private static readonly SettingValue Installed = new("installed", "Installée");
    private static readonly SettingValue Removed = new("removed", "Désinstallée");

    public override string Id => "privacy.copilot";
    public override string DisplayName => "Application Copilot";
    public override SettingCategory Category => SettingCategory.Privacy;
    public override bool SelectedByDefault => false;

    public override string? WhyNotApplicable(HardwareProfile hw) =>
        AppPackages.IsInstalled(Package) ? null : "Application Copilot absente";

    public override SettingValue ReadCurrent(HardwareProfile hw) => AppPackages.IsInstalled(Package) ? Installed : Removed;

    public override SettingTarget ComputeTarget(HardwareProfile hw) =>
        new(Removed, "Désinstalle l'application Copilot pour votre compte. Réinstallable à tout moment depuis le Microsoft Store.");

    public override void Apply(HardwareProfile hw, SettingValue value)
    {
        if (value.Raw == Removed.Raw)
        {
            var (code, output) = AppPackages.PowerShell($"Get-AppxPackage -Name '{Package}' | Remove-AppxPackage -ErrorAction Stop");
            if (code != 0) throw new InvalidOperationException($"Désinstallation refusée : {output}".Trim());
            return;
        }

        // Restauration : la réinstallation passe par le Microsoft Store, ouvert sans droits administrateur
        Process.Start(new ProcessStartInfo("explorer.exe", "\"ms-windows-store://search/?query=Microsoft%20Copilot\"") { UseShellExecute = false });
        throw new InvalidOperationException("Le Microsoft Store vient de s'ouvrir sur Copilot : cliquez sur « Installer » pour terminer la restauration.");
    }
}

/// <summary>Applications du Microsoft Store installées pour l'utilisateur courant (lecture rapide dans le registre).</summary>
internal static class AppPackages
{
    private const string Repository = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public static bool IsInstalled(params string[] packageNames)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(Repository);
            return key?.GetSubKeyNames().Any(full => packageNames.Any(name => full.StartsWith(name + "_", StringComparison.OrdinalIgnoreCase))) == true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    public static (int Code, string Output) PowerShell(string command)
    {
        using var process = Process.Start(new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command}\"")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("PowerShell introuvable.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2))) { process.Kill(); return (-1, "délai dépassé"); }
        return (process.ExitCode, output.Trim());
    }
}
