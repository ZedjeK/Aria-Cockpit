using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Aria.Core.Inventory;

/// <summary>Cœurs performants / économes (processeurs hybrides) et microcode chargé.</summary>
internal static class CpuTopology
{
    private const int RelationProcessorCore = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref int length);

    /// <summary>(P-cores, E-cores). (0, 0) si le processeur n'est pas hybride ou si la lecture échoue.</summary>
    public static (int Performance, int Efficient) ReadCoreTypes()
    {
        var length = 0;
        GetLogicalProcessorInformationEx(RelationProcessorCore, IntPtr.Zero, ref length);
        if (length <= 0) return (0, 0);

        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            if (!GetLogicalProcessorInformationEx(RelationProcessorCore, buffer, ref length)) return (0, 0);
            var bytes = new byte[length];
            Marshal.Copy(buffer, bytes, 0, length);
            return CountCoreTypes(bytes);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// Parcourt les blocs SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX (un par cœur) :
    /// Relationship (4 octets), Size (4), puis PROCESSOR_RELATIONSHIP : Flags (1), EfficiencyClass (1)…
    /// La classe la plus haute correspond aux P-cores.
    /// </summary>
    internal static (int Performance, int Efficient) CountCoreTypes(byte[] bytes)
    {
        var classes = new List<byte>();
        for (var offset = 0; offset + 10 <= bytes.Length;)
        {
            var size = BitConverter.ToInt32(bytes, offset + 4);
            if (size <= 0) break;
            if (BitConverter.ToInt32(bytes, offset) == RelationProcessorCore) classes.Add(bytes[offset + 9]);
            offset += size;
        }

        if (classes.Count == 0 || classes.Distinct().Count() < 2) return (0, 0);
        var top = classes.Max();
        return (classes.Count(c => c == top), classes.Count(c => c != top));
    }

    /// <summary>Révision du microcode chargé par le BIOS ou Windows (ex. 0x12F).</summary>
    public static uint? ReadMicrocode()
    {
        var raw = Registry.GetValue(@"HKEY_LOCAL_MACHINE\HARDWARE\DESCRIPTION\System\CentralProcessor\0", "Update Revision", null);
        return ParseMicrocode(raw as byte[]);
    }

    /// <summary>Valeur binaire de 8 octets : la révision est dans les 4 octets de poids fort (parfois les 4 premiers).</summary>
    internal static uint? ParseMicrocode(byte[]? raw)
    {
        if (raw is not { Length: >= 8 }) return null;
        var high = BitConverter.ToUInt32(raw, 4);
        var low = BitConverter.ToUInt32(raw, 0);
        var revision = high != 0 ? high : low;
        return revision == 0 ? null : revision;
    }
}
