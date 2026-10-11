using Aria.Core.Models;

namespace Aria.Core.Inventory;

internal static class BoardCollector
{
    public static BoardInfo Collect()
    {
        var board = Wmi.Query("SELECT Manufacturer, Product FROM Win32_BaseBoard").FirstOrDefault();
        var bios = Wmi.Query("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS").FirstOrDefault();

        return new BoardInfo(
            Manufacturer: board is null ? "Inconnu" : Wmi.Str(board, "Manufacturer"),
            Product: board is null ? "Inconnu" : Wmi.Str(board, "Product"),
            BiosVersion: bios is null ? "" : Wmi.Str(bios, "SMBIOSBIOSVersion"),
            BiosDate: bios is null ? null : Wmi.Date(bios, "ReleaseDate"));
    }
}
