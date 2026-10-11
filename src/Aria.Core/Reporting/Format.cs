namespace Aria.Core.Reporting;

public static class Format
{
    public static string Gb(ulong bytes) =>
        bytes == 0 ? "?" : $"{bytes / 1024d / 1024 / 1024:0.#} Go";

    public static string DiskSize(ulong bytes) =>
        bytes >= 1_000_000_000_000 ? $"{bytes / 1e12:0.#} To" : $"{bytes / 1e9:0} Go";

    public static string Yes(bool value) => value ? "oui" : "non";
}
