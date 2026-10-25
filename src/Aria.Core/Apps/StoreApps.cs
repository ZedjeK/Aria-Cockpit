using System.Diagnostics;
using Aria.Core.Models;
using Aria.Core.Privacy;
using Aria.Core.Settings;

namespace Aria.Core.Apps;

/// <summary>
/// Applications préinstallées par Windows qu'on peut désinstaller pour son compte. Toutes décochées par défaut.
/// Restent hors liste : Xbox et Game Bar (jeux), Store, Photos, Calculatrice, Bloc-notes, Lecteur multimédia,
/// Terminal, Capture d'écran, Pense-bêtes, Caméra, Horloge, Paint, Magnétophone. Les applis tierces en promotion
/// (Spotify, Disney+…) ne sont proposées que si elles sont réellement installées, comme les autres.
/// </summary>
public static class StoreApps
{
    /// <summary>Explication affichée une fois sous le titre du groupe Applications.</summary>
    public const string GroupNote =
        "La désinstallation ne concerne que votre compte. Pour réinstaller une application, Restauration ouvre sa page " +
        "dans le Microsoft Store. Une grosse mise à jour de Windows peut en réinstaller certaines.";

    public static IEnumerable<ISetting> Build() =>
    [
        new StoreAppSetting("clipchamp", "Clipchamp", "Montage vidéo de Microsoft", "Clipchamp", "Clipchamp.Clipchamp"),
        new StoreAppSetting("news", "Microsoft Actualités", "Fil d'actualités MSN", "Microsoft News", "Microsoft.BingNews"),
        new StoreAppSetting("weather", "Météo MSN", "Prévisions météo", "MSN Weather", "Microsoft.BingWeather"),
        new StoreAppSetting("solitaire", "Microsoft Solitaire Collection", "Jeux de cartes, avec publicités", "Microsoft Solitaire Collection",
            "Microsoft.MicrosoftSolitaireCollection"),
        new StoreAppSetting("tips", "Astuces", "Conseils de prise en main de Windows", "Microsoft Tips", "Microsoft.Getstarted"),
        new StoreAppSetting("feedback", "Hub de commentaires", "Envoi de commentaires à Microsoft", "Feedback Hub", "Microsoft.WindowsFeedbackHub"),
        new StoreAppSetting("gethelp", "Obtenir de l'aide", "Assistance Microsoft, aussi utilisée par certains utilitaires de résolution des problèmes",
            "Get Help", "Microsoft.GetHelp"),
        new StoreAppSetting("todo", "Microsoft To Do", "Listes de tâches", "Microsoft To Do", "Microsoft.Todos"),
        new StoreAppSetting("outlook", "Outlook (nouveau)", "Messagerie et calendrier", "Outlook for Windows", "Microsoft.OutlookForWindows"),
        new StoreAppSetting("teams", "Microsoft Teams", "Conversations et réunions", "Microsoft Teams", "MSTeams", "MicrosoftTeams"),
        new StoreAppSetting("family", "Microsoft Family Safety", "Contrôle parental", "Microsoft Family Safety",
            "MicrosoftCorporationII.MicrosoftFamily"),
        new StoreAppSetting("office", "Microsoft 365 Copilot", "Lanceur des applications Office (ex-« Microsoft 365 »)", "Microsoft 365 Copilot", "Microsoft.MicrosoftOfficeHub"),
        new StoreAppSetting("powerautomate", "Power Automate", "Automatisation de tâches", "Power Automate", "Microsoft.PowerAutomateDesktop"),
        new StoreAppSetting("maps", "Cartes", "Cartes et itinéraires Windows", "Windows Maps", "Microsoft.WindowsMaps"),
        new StoreAppSetting("bingsearch", "Recherche Bing", "Application de recherche Bing", "Bing", "Microsoft.BingSearch"),
        new StoreAppSetting("linkedin", "LinkedIn", "Réseau professionnel", "LinkedIn", "7EE7776C.LinkedInforWindows"),
        new StoreAppSetting("devhome", "Dev Home", "Tableau de bord pour développeurs", "Dev Home", "Microsoft.Windows.DevHome"),
        new StoreAppSetting("cortana", "Cortana", "Ancien assistant vocal de Windows", "Cortana", "Microsoft.549981C3F5F10"),
        new StoreAppSetting("phonelink", "Mobile connecté", "Lien entre votre téléphone et le PC", "Phone Link", "Microsoft.YourPhone"),
        new StoreAppSetting("quickassist", "Assistance rapide", "Prise de contrôle à distance du PC ; très utilisée par les arnaques au faux support technique",
            "Quick Assist", "MicrosoftCorporationII.QuickAssist"),
        // Anciennes installations : applications abandonnées ou remplacées par Microsoft
        new StoreAppSetting("filmstv", "Films et TV", "Ancien lecteur vidéo, remplacé par le Lecteur multimédia", "Films & TV", "Microsoft.ZuneVideo"),
        new StoreAppSetting("mail", "Courrier et Calendrier", "Abandonné par Microsoft fin 2024, remplacé par Outlook", "Mail and Calendar",
            "microsoft.windowscommunicationsapps"),
        new StoreAppSetting("people", "Contacts", "Ancien carnet d'adresses de Windows", "Microsoft People", "Microsoft.People"),
        new StoreAppSetting("skype", "Skype", "Service fermé par Microsoft en mai 2025", "Skype", "Microsoft.SkypeApp"),
        // Applications tierces installées en promotion sur les PC neufs
        new StoreAppSetting("spotify", "Spotify", "Installée en promotion par Windows", "Spotify", "SpotifyAB.SpotifyMusic"),
        new StoreAppSetting("disney", "Disney+", "Installée en promotion par Windows", "Disney+", "Disney.37853FC22B2CE"),
        new StoreAppSetting("netflix", "Netflix", "Installée en promotion par Windows", "Netflix", "4DF9E0F8.Netflix"),
        new StoreAppSetting("primevideo", "Prime Video", "Installée en promotion par Windows", "Prime Video", "AmazonVideo.PrimeVideo"),
        new StoreAppSetting("tiktok", "TikTok", "Installée en promotion par Windows", "TikTok", "BytedancePte.Ltd.TikTok"),
        new StoreAppSetting("candycrush", "Candy Crush", "Jeux installés en promotion par Windows", "Candy Crush",
            "king.com.CandyCrushSaga", "king.com.CandyCrushSodaSaga", "king.com.CandyCrushFriends"),
        new StoreAppSetting("facebook", "Facebook", "Installée en promotion par Windows", "Facebook", "FACEBOOK.FACEBOOK"),
        new StoreAppSetting("instagram", "Instagram", "Installée en promotion par Windows", "Instagram", "Facebook.Instagram"),
        new StoreAppSetting("whatsapp", "WhatsApp", "Messagerie souvent installée en promotion par Windows", "WhatsApp", "5319275A.WhatsAppDesktop")
    ];
}

/// <summary>
/// Une application du Microsoft Store. Valeur technique : « installed » ou « removed ».
/// Absente : non proposée (ni dans la liste, ni dans les non applicables), mais toujours restaurable.
/// </summary>
public sealed class StoreAppSetting(string key, string displayName, string description, string storeQuery, params string[] packages)
    : SettingBase
{
    private static readonly SettingValue Installed = new("installed", "Installée");
    private static readonly SettingValue Removed = new("removed", "Désinstallée");

    public IReadOnlyList<string> Packages { get; } = packages;

    public override string Id => $"apps.{key}";
    public override string DisplayName => displayName;
    public override SettingCategory Category => SettingCategory.Apps;
    public override bool SelectedByDefault => false;

    /// <summary>Une application absente n'encombre pas la liste des non applicables.</summary>
    public override bool HiddenWhenNotApplicable => true;

    public override string? WhyNotApplicable(HardwareProfile hardware) =>
        AppPackages.IsInstalled([.. Packages]) ? null : "Non installée";

    public override SettingValue ReadCurrent(HardwareProfile hardware) =>
        AppPackages.IsInstalled([.. Packages]) ? Installed : Removed;

    public override SettingTarget ComputeTarget(HardwareProfile hardware) => new(Removed, description);

    public override void Apply(HardwareProfile hardware, SettingValue value)
    {
        if (value.Raw == Removed.Raw)
        {
            // Noms de paquets fixes (aucune donnée extérieure dans la commande)
            foreach (var package in Packages.Where(p => AppPackages.IsInstalled(p)))
            {
                var (code, output) = AppPackages.PowerShell($"Get-AppxPackage -Name '{package}' | Remove-AppxPackage -ErrorAction Stop");
                if (code != 0) throw new InvalidOperationException($"Désinstallation refusée : {output}".Trim());
            }
            return;
        }

        // Restauration : la réinstallation passe par le Microsoft Store, ouvert sans droits administrateur
        Process.Start(new ProcessStartInfo("explorer.exe",
            $"\"ms-windows-store://search/?query={Uri.EscapeDataString(storeQuery)}\"") { UseShellExecute = false });
        throw new InvalidOperationException($"Le Microsoft Store vient de s'ouvrir sur « {displayName} » : cliquez sur « Installer » pour terminer la restauration.");
    }
}
