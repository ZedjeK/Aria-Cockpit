using System.Management;

namespace Aria.Core.Inventory;

/// <summary>Petites aides pour interroger WMI sans planter sur une propriété absente.</summary>
internal static class Wmi
{
    public const string Cimv2 = @"root\cimv2";
    public const string Storage = @"root\Microsoft\Windows\Storage";
    public const string RootWmi = @"root\wmi";

    public static List<ManagementBaseObject> Query(string wql, string scope = Cimv2)
    {
        using var searcher = new ManagementObjectSearcher(scope, wql);
        return searcher.Get().Cast<ManagementBaseObject>().ToList();
    }

    public static object? Raw(ManagementBaseObject obj, string property)
    {
        try { return obj[property]; }
        catch (ManagementException) { return null; } // propriété absente sur cette version de Windows
    }

    public static string Str(ManagementBaseObject obj, string property) =>
        Raw(obj, property)?.ToString()?.Trim() ?? "";

    public static long Long(ManagementBaseObject obj, string property)
    {
        var value = Raw(obj, property);
        if (value is null) return 0;
        try { return Convert.ToInt64(value); }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return 0; }
    }

    public static int Int(ManagementBaseObject obj, string property) => (int)Math.Clamp(Long(obj, property), int.MinValue, int.MaxValue);

    /// <summary>Date WMI (format "yyyyMMddHHmmss.ffffff+zzz") → date seule.</summary>
    public static DateTime? Date(ManagementBaseObject obj, string property)
    {
        var raw = Str(obj, property);
        return raw.Length >= 8 && DateTime.TryParseExact(raw[..8], "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    public static ulong ULong(ManagementBaseObject obj, string property)
    {
        var value = Raw(obj, property);
        if (value is null) return 0;
        try { return Convert.ToUInt64(value); }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return 0; }
    }
}
