using Aria.Core.Models;

namespace Aria.Core.Diagnostics.Checks;

/// <summary>Profil XMP/EXPO : déduit de la vitesse configurée et de la référence des barrettes.</summary>
public sealed class MemorySpeedCheck : IDiagnosticCheck
{
    public string Name => "vitesse de la RAM";

    // Vitesses de base JEDEC typiques d'une RAM "gaming" laissée sans profil
    private static readonly HashSet<int> Ddr4Base = [2133, 2400, 2666];
    private static readonly HashSet<int> Ddr5Base = [4800, 5200, 5600];

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var mem = ctx.Hardware.Memory;
        if (mem.Modules.Count == 0) yield break;

        var configured = MemoryParser.NormalizeSpeed(mem.ConfiguredSpeedMTs);
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
                ? " Sur les Intel de 12e génération, la limite officielle avec 4 barrettes est de 4000 MT/s."
                : "";

            yield return new("memory.speed",
                $"RAM à {configured} MT/s avec {mem.Modules.Count} barrettes DDR5 (kit vendu pour {kitSpeed})", Severity.Info,
                $"Avec 4 barrettes DDR5, la vitesse garantie par le processeur est nettement plus basse qu'avec 2.{intel12}" +
                $" Cette vitesse est donc normale sans profil {profile}.{mixedKits}",
                $"Le profil {profile} peut être tenté, mais il est souvent instable avec 4 barrettes : régler alors la fréquence " +
                "à la main, un cran au-dessus de la vitesse actuelle (ex. 4400 puis 4800 MT/s). Après chaque changement, tester " +
                "la stabilité (ex. MemTest86) : une RAM instable peut corrompre des fichiers sans prévenir. Gain en jeu modeste.",
                ctx.PlatformSteps("memory_profile"), NeedsBios: true);
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

/// <summary>Dual channel : une barrette seule, ou plusieurs barrettes dans le même canal, divisent la bande passante par deux.</summary>
public sealed class MemoryChannelCheck : IDiagnosticCheck
{
    public string Name => "canaux mémoire";

    public IEnumerable<DiagnosticFinding> Run(DiagnosticContext ctx)
    {
        var modules = ctx.Hardware.Memory.Modules;
        if (modules.Count == 0) yield break;

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
}
