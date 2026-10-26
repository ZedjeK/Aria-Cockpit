using System.Text.RegularExpressions;
using Aria.Core.Models;

namespace Aria.Core.Diagnostics.Checks;

/// <summary>
/// Intel Core 13e et 14e génération (bureau, 65 W et plus) : défaut d'instabilité « Vmin Shift »,
/// corrigé par le microcode livré dans les mises à jour du BIOS. Recommandation actuelle d'Intel : 0x12F ou plus.
/// </summary>
public sealed partial class MicrocodeCheck : IDiagnosticCheck
{
    public string Name => "microcode du processeur";

    internal const uint Recommended = 0x12F;
    internal const uint FirstFix = 0x12B;
    /// <summary>Puce Raptor Lake (CPUID modèle 0xB7), seule touchée ; les i5 sans K sur puce Alder Lake ont une autre lignée de microcode (0x3x).</summary>
    internal const int RaptorLakeModel = 183;

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var cpu = ctx.Hardware.Cpu;
        if (!IsAffectedModel(cpu.Name) || !IsRaptorLakeDie(cpu.Model)) yield break;

        var steps = new List<string> { ctx.Bios.Brand.UpdateTool, "Puis, dans le BIOS : charger le profil « Intel Default Settings »." };
        const string context = "Les Core i5, i7 et i9 de 13e et 14e génération pour PC fixe peuvent devenir instables " +
                               "puis s'abîmer (« Vmin Shift »). Intel corrige le problème par le microcode, livré avec les mises à jour du BIOS.";

        if (cpu.Microcode is not { } microcode)
        {
            yield return new("cpu.microcode", "Microcode du processeur illisible", Severity.Info,
                $"{context} Aria n'a pas pu lire la version installée.",
                "Vérifier que le BIOS est à jour : la note de version doit mentionner le microcode 0x12F ou plus récent.", steps);
        }
        else if (microcode < FirstFix)
        {
            yield return new("cpu.microcode", $"Microcode 0x{microcode:X} : correctif d'instabilité Intel absent", Severity.Critical,
                $"{context} Le microcode installé est antérieur au premier correctif complet (0x{FirstFix:X}).",
                $"Mettre à jour le BIOS vers une version contenant le microcode 0x{Recommended:X} ou plus récent, " +
                "puis charger le profil Intel Default Settings. Ne jamais couper le courant pendant la mise à jour.",
                steps, NeedsBios: true);
        }
        else if (microcode < Recommended)
        {
            yield return new("cpu.microcode", $"Microcode 0x{microcode:X} : un correctif plus récent existe", Severity.Info,
                $"{context} Votre version contient le correctif principal ; Intel recommande désormais 0x{Recommended:X} ou plus " +
                "(il améliore la stabilité des PC allumés longtemps à faible charge).",
                "Installer la dernière version du BIOS si elle contient le microcode 0x12F ou plus récent.", steps);
        }
        else
        {
            yield return new("cpu.microcode", $"Microcode 0x{microcode:X} à jour", Severity.Ok,
                "Correctif d'instabilité Intel des 13e et 14e générations installé.");
        }
    }

    /// <summary>
    /// Core i5/i7/i9 13xxx ou 14xxx de bureau (sans suffixe, ou K, KF, KS, F) ; exclut les portables (H, HX, P, U)
    /// et les versions basse consommation T (35 W).
    /// </summary>
    internal static bool IsAffectedModel(string name) => AffectedModel().IsMatch(name);

    /// <summary>Puce Raptor Lake, ou modèle illisible (on reste prudent : la vérification s'applique).</summary>
    internal static bool IsRaptorLakeDie(int? model) => model is null or RaptorLakeModel;

    [GeneratedRegex(@"\bi[579]-1[34]\d{3}(K|KF|KS|F)?(?![A-Z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex AffectedModel();
}

/// <summary>Usure et température des SSD (compteurs de fiabilité du disque).</summary>
public sealed class SsdHealthCheck : IDiagnosticCheck
{
    public string Name => "santé des SSD";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        foreach (var disk in ctx.Hardware.Disks.Where(d => d.Media == StorageMedia.Ssd || d.Bus == StorageBus.NVMe))
        {
            if (disk.WearPercent is null && disk.TemperatureC is null) continue;

            var label = disk.DriveLetters.Count > 0 ? $"SSD {string.Join(" ", disk.DriveLetters.Select(l => $"{l}:"))}" : $"SSD {disk.Model}";
            var facts = Describe(disk);

            if (disk.WearPercent >= 90)
                yield return new("ssd.wear", $"{label} en fin de vie (usure {disk.WearPercent} %)", Severity.Critical,
                    $"{disk.Model} · {facts}. Au-delà de 100 %, le fabricant ne garantit plus la fiabilité.",
                    "Sauvegarder les données importantes dès maintenant et prévoir le remplacement du SSD.");
            else if (disk.WearPercent >= 70)
                yield return new("ssd.wear", $"{label} : usure avancée ({disk.WearPercent} %)", Severity.Warning,
                    $"{disk.Model} · {facts}.",
                    "Vérifier que les sauvegardes sont à jour et surveiller l'évolution.");
            else if (disk.TemperatureC >= HotThreshold(disk))
                yield return new("ssd.temperature", $"{label} chaud ({disk.TemperatureC} °C)", Severity.Warning,
                    $"{disk.Model} · {facts}. " + (disk.TemperatureMaxC is { } max
                        ? $"Le disque indique {max} °C comme température maximale normale : au-delà, il ralentit pour se protéger."
                        : "Au-delà de 75-80 °C, un SSD ralentit pour se protéger."),
                    "Installer le dissipateur M.2 fourni avec la carte mère, ou améliorer le flux d'air du boîtier.");
            else
                yield return new("ssd.health", $"{label} en bonne santé", Severity.Ok, $"{disk.Model} · {facts}.");
        }
    }

    /// <summary>
    /// Seuil d'alerte : la température maximale normale annoncée par le disque, sinon 75 °C (beaucoup de NVMe Gen4 et Gen5
    /// tournent à 70-75 °C en charge et ne ralentissent qu'au-delà de 80 °C).
    /// </summary>
    internal static int HotThreshold(DiskInfo disk) => disk.TemperatureMaxC ?? 75;

    public static string Describe(DiskInfo d)
    {
        var parts = new List<string>();
        if (d.WearPercent is { } wear) parts.Add($"usure {wear} %");
        if (d.TemperatureC is { } temp) parts.Add($"{temp} °C");
        if (d.PowerOnHours is { } hours)
            parts.Add($"{hours.ToString("#,0", System.Globalization.CultureInfo.InvariantCulture).Replace(',', ' ')} h de fonctionnement");
        return string.Join(" · ", parts);
    }
}

/// <summary>Type de connexion des écrans et HDR.</summary>
public sealed class DisplayLinkCheck : IDiagnosticCheck
{
    public string Name => "connexion des écrans";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        foreach (var d in ctx.Hardware.Displays)
        {
            // Rien à dire sur la connexion d'un écran intégré de portable
            if (ConnectionName(d.Connection) is { } connection && d.Connection != DisplayConnection.Internal)
            {
                var highResolution = (long)d.Width * d.Height >= 2560L * 1440;

                if (d.Connection == DisplayConnection.Hdmi && highResolution && d.MaxRefreshHz <= 60)
                    yield return new("display.link", $"{d.MonitorName} en HDMI à {d.MaxRefreshHz} Hz", Severity.Info,
                        $"En {d.Width}x{d.Height}, la connexion HDMI n'offre pas plus de {d.MaxRefreshHz} Hz. Normal pour un écran " +
                        $"ou un téléviseur {d.MaxRefreshHz} Hz ; s'il est vendu pour une fréquence plus élevée, c'est le câble ou la version HDMI qui la bride.",
                        "Utiliser un câble DisplayPort, ou un câble HDMI 2.1 certifié « Ultra High Speed ».");
                else if (d.Connection is DisplayConnection.Vga or DisplayConnection.Dvi)
                    yield return new("display.link", $"{d.MonitorName} : connexion {connection} ancienne", Severity.Info,
                        $"Le {connection} limite la résolution et la fréquence, et ne gère ni le HDR ni G-Sync / FreeSync.",
                        "Passer en DisplayPort ou HDMI si l'écran le permet.");
                else
                    yield return new("display.link", $"{d.MonitorName} en {connection}", Severity.Ok,
                        $"{d.Width}x{d.Height} à {d.CurrentRefreshHz} Hz.");
            }

            if (d.HdrSupported == true && d.HdrEnabled == false)
                yield return new("display.hdr", $"{d.MonitorName} : HDR disponible mais désactivé", Severity.Info,
                    "L'écran accepte le HDR. Activé, il rend les jeux et films compatibles plus lumineux et contrastés ; " +
                    "sur le bureau, les couleurs peuvent paraître un peu ternes selon l'écran.",
                    "Paramètres → Système → Écran → Utiliser le HDR. L'application « Étalonnage HDR de Windows » " +
                    "(gratuite, Microsoft Store) aide à le régler. Le HDR automatique peut ensuite l'appliquer aux jeux plus anciens.");
            else if (d.HdrEnabled == true)
                yield return new("display.hdr", $"{d.MonitorName} : HDR actif", Severity.Ok, "");
        }
    }

    public static string? ConnectionName(DisplayConnection c) => c switch
    {
        DisplayConnection.DisplayPort => "DisplayPort",
        DisplayConnection.Hdmi => "HDMI",
        DisplayConnection.Dvi => "DVI",
        DisplayConnection.Vga => "VGA",
        DisplayConnection.Internal => "écran intégré",
        DisplayConnection.Wireless => "sans fil",
        _ => null
    };
}

/// <summary>Connexion réseau : lien Ethernet bridé, Wi-Fi faible.</summary>
public sealed class NetworkCheck : IDiagnosticCheck
{
    public string Name => "réseau";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var adapters = ctx.Hardware.Network;
        var ethernet = adapters.Where(a => a.Kind == NetworkKind.Ethernet).ToList();
        var wifi = adapters.FirstOrDefault(a => a.Kind == NetworkKind.WiFi);

        foreach (var e in ethernet)
        {
            if (e.SpeedBps is > 0 and < 1_000_000_000)
                yield return new("network.ethernet", $"Ethernet bridé à {Speed(e.SpeedBps)}", Severity.Warning,
                    $"{e.Description}. Un lien en dessous de 1 Gb/s vient presque toujours d'un câble abîmé ou trop ancien " +
                    "(catégorie 5), d'une prise mal enfoncée, ou d'un équipement limité à 100 Mb/s : switch, port de box, " +
                    "ou adaptateur CPL (courant porteur), dont beaucoup n'ont que des ports à 100 Mb/s.",
                    "Essayer un autre câble (catégorie 5e, 6 ou plus) et un autre port de la box ou du switch. " +
                    "Avec un adaptateur CPL, vérifier sa fiche : s'il est limité à 100 Mb/s, seul un modèle à port gigabit lèvera la limite.");
            else if (e.SpeedBps > 0)
                yield return new("network.ethernet", $"Ethernet à {Speed(e.SpeedBps)}", Severity.Ok, e.Description);
        }

        if (wifi is not null && ethernet.Count == 0)
        {
            var details = string.Join(" · ", new[]
            {
                wifi.WifiStandard,
                wifi.WifiSignalPercent is { } s ? $"signal {s} %" : null,
                wifi.SpeedBps > 0 ? $"liaison {Speed(wifi.SpeedBps)}" : null
            }.Where(x => x is not null));

            if (wifi.WifiSignalPercent is < 50)
                yield return new("network.wifi", $"Signal Wi-Fi faible ({wifi.WifiSignalPercent} %)", Severity.Warning,
                    $"{wifi.Description} · {details}. Un signal faible augmente la latence et les pertes de paquets en jeu.",
                    ctx.Hardware.IsLaptop
                        ? "Se rapprocher de la box, ou utiliser la bande 5 GHz / 6 GHz."
                        : "Pour un PC fixe, l'Ethernet (ou le CPL) reste le plus stable. Sinon, rapprocher la box ou ajouter un répéteur.");
            else if (!ctx.Hardware.IsLaptop)
                yield return new("network.wifi", "PC fixe connecté en Wi-Fi", Severity.Info,
                    $"{wifi.Description} · {details}.",
                    "Pour le jeu en ligne, un câble Ethernet donne une latence plus basse et plus régulière.");
            else
                yield return new("network.wifi",
                    wifi.WifiSignalPercent is { } signal ? $"Wi-Fi correct (signal {signal} %)" : "Connexion Wi-Fi active",
                    Severity.Ok, $"{wifi.Description} · {details}.");
        }

        if (adapters.Count == 0)
            yield return new("network.none", "Aucune connexion réseau détectée", Severity.Info,
                "Aucune carte Ethernet ou Wi-Fi n'est connectée.");
    }

    public static string Speed(long bps) =>
        bps >= 1_000_000_000 ? $"{bps / 1e9:0.#} Gb/s" : $"{bps / 1e6:0} Mb/s";
}

/// <summary>Protection du système, redémarrage en attente, batterie.</summary>
public sealed class SystemHealthCheck : IDiagnosticCheck
{
    public string Name => "état de Windows";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var state = ctx.Hardware.SystemState;
        var drive = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";

        if (state.SystemProtection == false)
            yield return new("system.protection", $"Protection du système désactivée sur {drive}", Severity.Warning,
                "Sans elle, Windows ne peut pas créer de points de restauration : ni avant les modifications d'Aria, " +
                "ni avant l'installation d'un pilote ou d'une mise à jour. (La sauvegarde propre à Aria reste disponible.)",
                "Rechercher « Créer un point de restauration » dans le menu Démarrer, sélectionner le disque " +
                $"{drive} (Système), Configurer → Activer la protection du système, avec environ 5 % d'espace disque.");
        else if (state.SystemProtection == true)
            yield return new("system.protection", $"Points de restauration actifs ({drive})", Severity.Ok, "");

        if (state.PendingReboot == true)
            yield return new("system.reboot", "Redémarrage en attente", Severity.Info,
                "Windows attend un redémarrage pour terminer l'installation de mises à jour.",
                "Redémarrer le PC quand c'est possible.");
        else if (state.PendingReboot == false)
            yield return new("system.reboot", "Aucun redémarrage en attente", Severity.Ok, "");

        if (ctx.Hardware.Battery is { } battery && ctx.Hardware.IsLaptop)
        {
            var health = battery.HealthPercent;
            var detail = $"Capacité actuelle : {battery.FullChargeCapacity / 1000.0:0.#} Wh sur {battery.DesignCapacity / 1000.0:0.#} Wh d'origine.";
            if (health < 60)
                yield return new("battery.health", $"Batterie usée ({health} % de sa capacité d'origine)", Severity.Warning,
                    detail, "Une batterie sous 60 % se remplace généralement : voir le service après-vente du fabricant.");
            else if (health < 80)
                yield return new("battery.health", $"Batterie à {health} % de sa capacité d'origine", Severity.Info,
                    detail, "Usure normale avec le temps. Limiter la charge à 80 % (option souvent proposée par le fabricant) la ralentit.");
            else
                yield return new("battery.health", $"Batterie à {health} % de sa capacité d'origine", Severity.Ok, detail);
        }
    }
}
