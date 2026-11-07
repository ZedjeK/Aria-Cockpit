using System.Buffers.Binary;
using Microsoft.Win32;
using Aria.Core.Models;

namespace Aria.Core.Battery;

/// <summary>
/// Mode conservation des portables Lenovo grand public (Legion, LOQ, IdeaPad, Yoga), par le pilote Lenovo Energy
/// Management (\\.\EnergyDrv) qu'utilise Lenovo Vantage. Lenovo ne documente pas cette interface : codes et lecture de
/// l'état sont ceux de Lenovo Legion Toolkit. Le plafond est fixé par Lenovo selon le modèle (pas de seuil au choix).
/// Les ThinkPad n'ont pas ce pilote : leur seuil se règle dans Lenovo Vantage.
/// </summary>
internal sealed class LenovoChargeLimit : IChargeLimitControl
{
    private const string DevicePath = @"\\.\EnergyDrv";
    internal const uint ChargeModeCode = 0x831020F8;
    internal const uint ReadArgument = 0xFF;

    internal enum Mode { Normal, Rapid, Conservation }

    // Commandes du pilote
    private const uint ConservationOn = 0x3;
    private const uint ConservationOff = 0x5;
    private const uint RapidOn = 0x7;
    private const uint RapidOff = 0x8;

    /// <summary>Valeur que Lenovo Vantage garde pour ce réglage : mise à jour pour qu'il ne remette pas la sienne.</summary>
    private const string VantageKey = @"Software\Lenovo\VantageService\AddinData\IdeaNotebookAddin";
    private const string VantageValue = "BatteryChargeMode";

    public ChargeLimitVendor Vendor => ChargeLimitVendor.Lenovo;
    public bool ReappliesAtLogon => true;

    public ChargeLimitInfo? Read()
    {
        try
        {
            return ReadMode() is { } mode ? Info(mode) : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    public ChargeLimitInfo Recommended() => Info(Mode.Conservation);

    public ChargeLimitInfo InfoFor(string raw) => Info(Parse(raw));

    public string? Blocker() => null;

    public void Write(ChargeLimitInfo current, string raw)
    {
        var target = Parse(raw);
        using var handle = DriverIo.Open(DevicePath) ?? throw new InvalidOperationException("Pilote Lenovo Energy Management introuvable.");
        var from = Decode(Send(handle, ReadArgument)) ?? throw new InvalidOperationException("Mode de charge Lenovo illisible.");
        foreach (var code in Codes(from, target)) Send(handle, code);

        // Le pilote applique la commande avec un léger décalage : relecture pendant une demi-seconde au plus
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if (Decode(Send(handle, ReadArgument)) == target)
            {
                RememberForVantage(target);
                return;
            }
            Thread.Sleep(50);
        }
        throw new InvalidOperationException($"Le pilote Lenovo n'a pas pris en compte le mode {DescribeMode(target).ToLowerInvariant()}.");
    }

    private static Mode? ReadMode()
    {
        using var handle = DriverIo.Open(DevicePath);
        return handle is null ? null : Decode(Send(handle, ReadArgument));
    }

    private static uint Send(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint argument)
    {
        var output = new byte[4];
        DriverIo.Call(handle, ChargeModeCode, BitConverter.GetBytes(argument), output);
        return BitConverter.ToUInt32(output, 0);
    }

    /// <summary>
    /// État renvoyé par le pilote, octets inversés : bit 17 = charge normale (bit 26 = charge rapide en plus),
    /// sinon bit 29 = mode conservation. Autre chose : état inconnu.
    /// </summary>
    internal static Mode? Decode(uint output)
    {
        var state = BinaryPrimitives.ReverseEndianness(output);
        if (Bit(state, 17)) return Bit(state, 26) ? Mode.Rapid : Mode.Normal;
        if (Bit(state, 29)) return Mode.Conservation;
        return null;
    }

    /// <summary>Commandes pour passer d'un mode à l'autre : la charge rapide et la conservation s'excluent.</summary>
    internal static uint[] Codes(Mode current, Mode target) => target switch
    {
        Mode.Conservation => current == Mode.Rapid ? [RapidOff, ConservationOn] : [ConservationOn],
        Mode.Normal => current == Mode.Conservation ? [ConservationOff] : [RapidOff],
        _ => current == Mode.Conservation ? [ConservationOff, RapidOn] : [RapidOn]
    };

    private static bool Bit(uint value, int n) => (value & (1u << n)) != 0;

    internal static ChargeLimitInfo Info(Mode mode) =>
        new(ChargeLimitVendor.Lenovo, Raw(mode), DescribeMode(mode), mode == Mode.Conservation);

    internal static string Raw(Mode mode) => mode switch
    {
        Mode.Conservation => "Conservation",
        Mode.Rapid => "Rapide",
        _ => "Normale"
    };

    internal static Mode Parse(string raw) => raw.Trim().ToLowerInvariant() switch
    {
        "conservation" => Mode.Conservation,
        "rapide" => Mode.Rapid,
        "normale" => Mode.Normal,
        _ => throw new FormatException($"Mode de charge Lenovo illisible : « {raw} ».")
    };

    internal static string DescribeMode(Mode mode) => mode switch
    {
        Mode.Conservation => "Mode conservation (plafond fixé par Lenovo selon le modèle)",
        Mode.Rapid => "Charge rapide, jusqu'à 100 %",
        _ => "Normale, jusqu'à 100 %"
    };

    private static void RememberForVantage(Mode mode)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(VantageKey, writable: true);
            key?.SetValue(VantageValue, mode switch { Mode.Conservation => "Storage", Mode.Rapid => "Quick", _ => "Normal" });
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }
}
