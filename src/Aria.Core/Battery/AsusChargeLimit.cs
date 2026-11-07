using Microsoft.Win32;
using Aria.Core.Models;

namespace Aria.Core.Battery;

/// <summary>
/// Limite de charge des portables ASUS (ROG, TUF, Zenbook, Vivobook…), par le pilote ACPI d'ASUS (\\.\ATKACPI),
/// installé avec les pilotes du fabricant. ASUS ne documente pas cette interface : le protocole (méthodes DSTS et DEVS,
/// fonction 0x00120057, « Relative state of charge ») est celui que le noyau Linux (asus-wmi) et G-Helper utilisent.
/// Le BIOS remet la charge complète à chaque démarrage : Aria réapplique la limite à l'ouverture de session.
/// La valeur est aussi notée là où MyASUS la range, pour que le logiciel d'ASUS ne la contredise pas.
/// </summary>
internal sealed class AsusChargeLimit : IChargeLimitControl
{
    private const string DevicePath = @"\\.\ATKACPI";
    private const uint ControlCode = 0x0022240C;

    /// <summary>« DSTS » : lire l'état d'une fonction. « DEVS » : la régler. (Codes ASCII, petit-boutiste.)</summary>
    internal const uint Dsts = 0x53545344;
    internal const uint Devs = 0x53564544;

    internal const uint BatteryLimitDevice = 0x00120057;

    /// <summary>Bit de DSTS indiquant que la fonction existe sur ce modèle.</summary>
    internal const int PresenceBit = 0x00010000;

    internal const int Minimum = 40;
    internal const int Recommended80 = 80;
    internal const int Full = 100;

    /// <summary>Au-delà, la batterie reste trop longtemps presque pleine pour que la limite serve vraiment (même seuil que Dell).</summary>
    internal const int MaxProtectiveStop = 85;

    private const string MyAsusKey = @"SOFTWARE\ASUS\ASUS System Control Interface\AsusOptimization\ASUS Keyboard Hotkeys";
    private const string MyAsusValue = "ChargingRate";

    public ChargeLimitVendor Vendor => ChargeLimitVendor.Asus;
    public bool ReappliesAtLogon => true;

    /// <summary>Le pilote ne relit pas la limite : au démarrage, le BIOS a remis 100 % quoi qu'en dise la valeur notée.</summary>
    public bool CanReadBack => false;

    public ChargeLimitInfo? Read()
    {
        using var handle = DriverIo.Open(DevicePath);
        if (handle is null) return null;
        try
        {
            var status = Send(handle, Dsts, BatteryLimitDevice, 0);
            if (!IsPresent(status)) return null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }

        // Le pilote ne relit pas la limite : celle de MyASUS, sinon celle qu'Aria a posée, sinon la charge complète du BIOS
        var limit = MyAsusLimit() ?? SavedLimit() ?? Full;
        return Info(limit);
    }

    public ChargeLimitInfo Recommended() => Info(Recommended80);

    public ChargeLimitInfo InfoFor(string raw) => Info(Parse(raw));

    public string? Blocker() => null;

    public void Write(ChargeLimitInfo current, string raw)
    {
        var limit = Parse(raw);
        using var handle = DriverIo.Open(DevicePath) ?? throw new InvalidOperationException("Pilote ASUS (ATKACPI) introuvable.");
        var result = Send(handle, Devs, BatteryLimitDevice, (uint)limit);
        if (result != 1) throw new InvalidOperationException($"Le pilote ASUS a refusé la limite de {limit} % (code {result}).");

        // MyASUS installé : même valeur de son côté, sinon son service remettrait la sienne
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MyAsusKey, writable: true);
            key?.SetValue(MyAsusValue, limit, RegistryValueKind.DWord);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }

    private static int Send(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint method, uint device, uint value)
    {
        var output = new byte[16];
        DriverIo.Call(handle, ControlCode, Request(method, device, value), output);
        return BitConverter.ToInt32(output, 0);
    }

    /// <summary>
    /// Commande ATKACPI : identifiant de la méthode, taille des arguments (8), puis les arguments : la fonction visée et
    /// sa valeur (0 pour une lecture). Entiers de 32 bits, petit-boutistes.
    /// </summary>
    internal static byte[] Request(uint method, uint device, uint value)
    {
        var buffer = new byte[16];
        BitConverter.GetBytes(method).CopyTo(buffer, 0);
        BitConverter.GetBytes(8u).CopyTo(buffer, 4);
        BitConverter.GetBytes(device).CopyTo(buffer, 8);
        BitConverter.GetBytes(value).CopyTo(buffer, 12);
        return buffer;
    }

    /// <summary>
    /// Fonction présente : bit 16 posé. Une valeur négative (0xFFFFFFFE, « méthode inconnue ») a tous ses bits posés
    /// et ne compte pas : même règle que G-Helper, qui exige un résultat positif.
    /// </summary>
    internal static bool IsPresent(int status) => status > 0 && (status & PresenceBit) != 0;

    internal static ChargeLimitInfo Info(int limit) =>
        new(ChargeLimitVendor.Asus, limit.ToString(), DescribeLimit(limit), limit <= MaxProtectiveStop);

    internal static string DescribeLimit(int limit) => limit >= Full ? "Jusqu'à 100 %" : $"Arrêt à {limit} %";

    internal static int Parse(string raw) =>
        int.TryParse(raw.Trim(), out var limit) && limit is >= Minimum and <= Full
            ? limit
            : throw new FormatException($"Limite de charge ASUS illisible : « {raw} » (attendu {Minimum} à {Full}).");

    private static int? MyAsusLimit()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(MyAsusKey);
            return key?.GetValue(MyAsusValue) is int value && value is >= Minimum and <= Full ? value : null;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return null; }
    }

    private static int? SavedLimit() =>
        ChargeLimits.SavedRaw(ChargeLimitVendor.Asus) is { } raw && int.TryParse(raw, out var value) ? value : null;
}
