using System.Diagnostics;
using Microsoft.Win32;
using Aria.Core.Models;
using Aria.Core.Settings;

namespace Aria.Core.Windows;

/// <summary>
/// Veille prolongée de Windows, et le démarrage rapide qui en dépend. Activée, elle réserve sur le disque système un fichier
/// (hiberfil.sys) de la taille d'une bonne part de la mémoire : 25 Go sur un PC à 64 Go. Sur une tour, elle sert peu.
/// Option décochée, réservée aux PC fixes : sur un portable, la veille prolongée sauve le travail en cours quand la batterie
/// se vide. Réglée par powercfg, l'outil officiel ; la restauration la réactive (« powercfg /hibernate on »).
/// Valeur technique : « on » ou « off ».
/// </summary>
public sealed class HibernationSetting : SettingBase
{
    private const string On = "on", Off = "off";
    private const string PowerKey = @"SYSTEM\CurrentControlSet\Control\Power";

    public override string Id => "windows.hibernation";
    public override string DisplayName => "Veille prolongée et démarrage rapide";
    public override SettingCategory Category => SettingCategory.Windows;
    public override bool SelectedByDefault => false;

    public override string? WhyNotApplicable(HardwareProfile hw) =>
        hw.IsLaptop ? "Portable : la veille prolongée sauve le travail en cours quand la batterie se vide" : null;

    public override SettingValue ReadCurrent(HardwareProfile hw) => Value(IsEnabled());

    public override SettingTarget ComputeTarget(HardwareProfile hw) => new(Value(false), Reason(FileBytes()));

    public override void Apply(HardwareProfile hw, SettingValue value)
    {
        if (value.Raw is not (On or Off)) throw new FormatException($"Valeur inattendue : {value.Raw}");
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), $"/hibernate {value.Raw}")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("powercfg.exe introuvable.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        if (!process.WaitForExit(60_000))
        {
            try { process.Kill(); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            throw new InvalidOperationException("powercfg.exe ne répond pas.");
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"powercfg a refusé le changement (code {process.ExitCode}) : {string.Join(" ", output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))}".Trim());
    }

    /// <summary>
    /// La veille prolongée est-elle active ? Windows n'écrit « HibernateEnabled » qu'après un premier réglage : tant qu'elle
    /// est absente, c'est la présence du fichier hiberfil.sys qui le dit. Fonction pure (testable).
    /// </summary>
    internal static bool IsEnabled(int? hibernateEnabled, bool filePresent) => hibernateEnabled is { } value ? value != 0 : filePresent;

    /// <summary>Ce que le réglage change, avec la place rendue quand la taille du fichier est lisible. Fonction pure (testable).</summary>
    internal static string Reason(long? fileBytes)
    {
        var freed = fileBytes is > 0
            ? $"Libère {(fileBytes.Value / 1073741824.0).ToString("0.#", System.Globalization.CultureInfo.GetCultureInfo("fr-FR"))} Go sur le disque système (fichier hiberfil.sys). "
            : "Libère le fichier hiberfil.sys du disque système. ";
        return freed + "Le démarrage rapide, qui s'appuie sur la veille prolongée, est coupé avec elle : « Arrêter » éteint alors vraiment " +
               "Windows, et le PC démarre à froid, quelques secondes de plus. La veille simple reste disponible. " +
               "Restaurer ce réglage réactive la veille prolongée et recrée le fichier.";
    }

    private static bool IsEnabled()
    {
        using var key = Registry.LocalMachine.OpenSubKey(PowerKey);
        return IsEnabled(key?.GetValue("HibernateEnabled") as int?, FileBytes() is not null);
    }

    /// <summary>Taille de hiberfil.sys sur le disque système ; null s'il est absent ou illisible.</summary>
    private static long? FileBytes()
    {
        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var file = new FileInfo(Path.Combine(Path.GetPathRoot(windows) ?? @"C:\", "hiberfil.sys"));
            return file.Exists ? file.Length : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static SettingValue Value(bool enabled) => new(enabled ? On : Off, enabled ? "Activés" : "Désactivés");
}
