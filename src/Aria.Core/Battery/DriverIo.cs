using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Aria.Core.Battery;

/// <summary>Échange avec un pilote du fabricant par DeviceIoControl (ASUS ATKACPI, Lenovo EnergyDrv).</summary>
internal static class DriverIo
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x1 | 0x2;
    private const uint OpenExisting = 3;
    private const uint NormalAttributes = 0x80;

    /// <summary>Ouvre le pilote, ou null s'il n'est pas installé (autre fabricant, pilote absent).</summary>
    public static SafeFileHandle? Open(string path)
    {
        var handle = CreateFile(path, GenericRead | GenericWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, NormalAttributes, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }

    /// <summary>Envoie une commande ; lève une exception si le pilote la refuse.</summary>
    public static void Call(SafeFileHandle handle, uint code, byte[] input, byte[] output)
    {
        if (!DeviceIoControl(handle, code, input, (uint)input.Length, output, (uint)output.Length, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, uint inputSize,
        byte[] output, uint outputSize, out uint returned, IntPtr overlapped);
}
