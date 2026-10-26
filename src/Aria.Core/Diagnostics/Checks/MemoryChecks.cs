using Aria.Core.Models;

namespace Aria.Core.Diagnostics.Checks;

/// <summary>Profil XMP/EXPO : déduit de la vitesse configurée et de la référence des barrettes.</summary>
public sealed partial class MemorySpeedCheck : IDiagnosticCheck
{
    public string Name => "vitesse de la RAM";

    // Vitesses de base JEDEC typiques d'une RAM "gaming" laissée sans profil
    private static readonly HashSet<int> Ddr4Base = [2133, 2400, 2666];
    private static readonly HashSet<int> Ddr5Base = [4800, 5200, 5600];

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var mem = ctx.Hardware.Memory;
        if (mem.Modules.Count == 0) yield break;

        var configured = MemoryParser.NormalizeSpeed(mem.ConfiguredSpeedMTs, mem.Generation);
        if (configured == 0) yield break;

        var profile = ctx.Hardware.Cpu.IsAmd ? "EXPO" : "XMP";
        var kitSpeeds = mem.Modules.Select(m => MemoryParser.KitSpeedFromPartNumber(m.PartNumber)).ToList();
        var kitSpeed = kitSpeeds.All(s => s.HasValue) ? kitSpeeds.Min(s => s!.Value) : (int?)null;

        // Portables : RAM généralement soudée ou sans profil réglable
        if (ctx.Hardware.IsLaptop)
        {
            yield return new("memory.speed", $"RAM à {configured} MT/s", Severity.Ok,
                "PC portable : la vitesse de la RAM est fixée par le fabricant.");
            yield break;
        }

        // 4 barrettes DDR5 : les processeurs garantissent officiellement une vitesse bien plus basse qu'avec 2
        // (ex. DDR5-4000 sur Intel 12e génération). Une vitesse réduite est donc normale, pas forcément un oubli.
        if (kitSpeed > configured + 100 && mem.Generation == "DDR5" && mem.Modules.Count >= 4)
        {
            var kitSizes = mem.Modules.Select(m => MemoryParser.KitSizeFromPartNumber(m.PartNumber)).ToList();
            var mixedKits = kitSizes.All(k => k.HasValue) && kitSizes.Max() < mem.Modules.Count
                ? $" Vos barrettes viennent de plusieurs kits de {kitSizes.Max()} : les mélanger augmente le risque d'instabilité."
                : "";
            var intel12 = ctx.Hardware.Cpu.IsIntel && ctx.Hardware.Cpu.Name.Contains("12th Gen", StringComparison.OrdinalIgnoreCase)
                ? " Les Intel de 12e génération, les premiers à gérer la DDR5, sont les plus capricieux avec 4 barrettes."
                : "";

            yield return new("memory.speed",
                $"RAM à {configured} MT/s avec {mem.Modules.Count} barrettes DDR5 (kit vendu pour {kitSpeed})", Severity.Info,
                "Avec 4 barrettes DDR5 (deux par canal), le contrôleur mémoire du processeur et la carte mère tiennent nettement moins vite " +
                $"qu'avec 2 : les fabricants garantissent en général 3600 à 4000 MT/s dans ce cas, contre 6000 et plus avec 2 barrettes.{intel12}" +
                $"{mixedKits} Le profil {profile} ({kitSpeed} MT/s) dépasse donc souvent ce que la plateforme tient : plantages, écrans bleus " +
                "ou PC qui ne démarre plus sont fréquents, sans que la RAM soit défectueuse. Cette vitesse est normale. " +
                "Gain à attendre en montant : quelques pour cent, surtout dans les jeux limités par le processeur ; " +
                "presque rien quand la carte graphique limite (hautes définitions).",
                "Le plus sûr : garder cette vitesse. Pour gagner un peu, monter la fréquence à la main par paliers plutôt que d'activer " +
                $"le profil {profile} (étapes ci-dessous). Si 32 Go suffisent, ne garder que 2 barrettes : elles tiennent en général " +
                $"le profil {profile} sans difficulté.",
                FourDimmSteps(ctx.Hardware.Cpu.IsIntel, profile), NeedsBios: true);
        }
        else if (kitSpeed > configured + 100 && mem.Modules.All(m => MemoryParser.IsJedecOnly(m.PartNumber)))
        {
            yield return new("memory.speed",
                $"RAM à {configured} MT/s (barrettes vendues pour {kitSpeed})", Severity.Info,
                $"Ces barrettes n'ont pas de profil {profile} : leur vitesse est bridée par le processeur ou la carte mère, " +
                "ce qui est normal. Il n'y a rien à activer.");
        }
        else if (kitSpeed > configured + 100)
        {
            yield return new("memory.speed",
                $"RAM sous-exploitée : profil {profile} probablement inactif", Severity.Warning,
                $"Configurée à {configured} MT/s alors que les barrettes sont vendues pour {kitSpeed} MT/s.",
                $"Activer le profil {profile} dans le BIOS. En cas d'instabilité, essayer le profil 2 ou revenir au réglage précédent.",
                ctx.PlatformSteps("memory_profile"), NeedsBios: true);
        }
        else if (kitSpeed is null && (mem.Generation == "DDR5" ? Ddr5Base : Ddr4Base).Contains(configured))
        {
            yield return new("memory.speed",
                $"RAM à vitesse standard ({configured} MT/s)", Severity.Info,
                $"Référence des barrettes non reconnue. Si elles sont vendues pour une vitesse plus élevée, le profil {profile} est inactif.",
                $"Vérifier la vitesse indiquée sur la boîte ou l'étiquette des barrettes.",
                ctx.PlatformSteps("memory_profile"));
        }
        else
        {
            var detail = kitSpeed is null ? "" : $"Conforme à la vitesse du kit ({kitSpeed} MT/s).";
            yield return new("memory.speed", $"RAM à {configured} MT/s", Severity.Ok, detail);
        }
    }
}

public sealed partial class MemorySpeedCheck
{
    /// <summary>
    /// Montée prudente avec 4 barrettes DDR5 : par paliers, sans profil, avec un test de stabilité à chaque fois.
    /// Tensions d'après les retours d'utilisateurs de Z690 en 4 barrettes (pas de consigne officielle) : rester prudent.
    /// </summary>
    internal static IReadOnlyList<string> FourDimmSteps(bool intel, string profile)
    {
        var steps = new List<string>
        {
            $"1. Dans le BIOS, laisser le profil {profile} désactivé et régler la fréquence de la RAM à la main, un cran au-dessus " +
            "de la vitesse actuelle (ex. 4400 MT/s). Laisser timings et tensions en automatique.",
            "2. Premier démarrage long, écran noir possible : la carte mère « entraîne » la mémoire. Patienter avant de couper.",
            "3. Tester la stabilité : une nuit de MemTest86, puis vos jeux habituels. Une RAM instable peut corrompre des fichiers sans planter.",
            "4. Si tout est stable, essayer le cran suivant (ex. 4800 MT/s). Au premier problème, revenir au cran précédent."
        };
        if (intel)
            steps.Add("5. Pour aller plus loin (utilisateurs avertis) : tension VDD/VDDQ de la RAM sans dépasser 1,35 V, et tension " +
                      "System Agent (SA) fixée à la main vers 1,20-1,25 V (en automatique, elle monte souvent trop haut). Au-delà, " +
                      "risque d'abîmer le processeur avec le temps.");
        return steps;
    }
}

/// <summary>Dual channel : une barrette seule, ou plusieurs barrettes dans le même canal, divisent la bande passante par deux.</summary>
public sealed class MemoryChannelCheck : IDiagnosticCheck
{
    public string Name => "canaux mémoire";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var modules = ctx.Hardware.Memory.Modules;
        if (modules.Count == 0) yield break;

        // Mémoire soudée (portables, consoles portables) : souvent annoncée comme un seul module, avec plusieurs canaux
        // internes et aucun emplacement libre ; « une seule barrette » n'a pas de sens
        if (modules.All(IsSoldered))
        {
            yield return new("memory.channel", "Mémoire soudée (LPDDR)", Severity.Ok,
                "Mémoire intégrée à la carte mère, organisée en plusieurs canaux par le fabricant : rien à ajouter ni à déplacer.");
            yield break;
        }

        if (modules.Count == 1)
        {
            yield return new("memory.channel", "Une seule barrette : mode simple canal",
                ctx.Hardware.IsLaptop ? Severity.Info : Severity.Warning,
                "La bande passante mémoire est divisée par deux, ce qui pénalise surtout les jeux limités par le processeur.",
                ctx.Hardware.IsLaptop
                    ? "Ajouter une seconde barrette identique si le portable a un emplacement libre."
                    : "Ajouter une seconde barrette identique (idéalement un kit de deux).");
            yield break;
        }

        var keys = modules.Select(m => MemoryParser.ChannelKey(m.DeviceLocator, m.BankLabel)).ToList();
        if (keys.Any(k => k is null)) yield break; // emplacements non interprétables : on ne conclut rien

        if (keys.Distinct().Count() == 1)
        {
            yield return new("memory.channel", "Barrettes dans le même canal : mode simple canal", Severity.Warning,
                $"Emplacements détectés : {string.Join(", ", modules.Select(m => m.DeviceLocator))}.",
                "Déplacer les barrettes dans les emplacements conseillés par le manuel de la carte mère (souvent A2 et B2, les 2e et 4e en partant du processeur).");
        }
        else
        {
            yield return new("memory.channel", "Dual channel actif", Severity.Ok,
                $"{modules.Count} barrettes réparties sur plusieurs canaux.");
        }
    }

    /// <summary>Types SMBIOS LPDDR, LPDDR2, LPDDR3, LPDDR4 et LPDDR5 (27 à 30, 35) : mémoire soudée.</summary>
    internal static bool IsSoldered(MemoryModule module) => module.SmbiosType is 27 or 28 or 29 or 30 or 35;
}
