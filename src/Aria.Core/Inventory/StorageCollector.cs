using Aria.Core.Models;

namespace Aria.Core.Inventory;

internal static class StorageCollector
{
    public static IReadOnlyList<DiskInfo> Collect()
    {
        // Lettres de lecteur par numéro de disque
        var letters = new Dictionary<int, List<char>>();
        foreach (var part in Wmi.Query("SELECT DiskNumber, DriveLetter FROM MSFT_Partition", Wmi.Storage))
        {
            var raw = Wmi.Raw(part, "DriveLetter");
            if (raw is null) continue;

            var letter = Convert.ToChar(raw);
            if (!char.IsLetter(letter)) continue;

            var disk = Wmi.Int(part, "DiskNumber");
            if (!letters.TryGetValue(disk, out var list)) letters[disk] = list = [];
            list.Add(char.ToUpperInvariant(letter));
        }

        var health = ReadHealth();

        return Wmi.Query("SELECT DeviceId, FriendlyName, MediaType, BusType, Size FROM MSFT_PhysicalDisk", Wmi.Storage)
            .Select(d =>
            {
                _ = int.TryParse(Wmi.Str(d, "DeviceId"), out var number);
                health.TryGetValue(Wmi.Str(d, "DeviceId"), out var h);
                return new DiskInfo(
                    Number: number,
                    Model: Wmi.Str(d, "FriendlyName"),
                    Media: Wmi.Int(d, "MediaType") switch { 3 => StorageMedia.Hdd, 4 => StorageMedia.Ssd, _ => StorageMedia.Unknown },
                    Bus: Wmi.Int(d, "BusType") switch
                    {
                        17 => StorageBus.NVMe,
                        11 => StorageBus.Sata,
                        7 => StorageBus.Usb,
                        8 => StorageBus.Raid,
                        _ => StorageBus.Other
                    },
                    SizeBytes: Wmi.ULong(d, "Size"),
                    DriveLetters: letters.TryGetValue(number, out var l) ? l.Order().ToList() : [],
                    WearPercent: h.Wear,
                    TemperatureC: h.Temperature,
                    PowerOnHours: h.Hours,
                    TemperatureMaxC: h.TemperatureMax);
            })
            .OrderBy(d => d.Number)
            .ToList();
    }

    /// <summary>
    /// Compteurs de fiabilité (usure, température, heures de fonctionnement), par numéro de disque.
    /// Certains disques ou pilotes ne les fournissent pas : les valeurs restent alors vides.
    /// </summary>
    private static Dictionary<string, (int? Wear, int? Temperature, long? Hours, int? TemperatureMax)> ReadHealth()
    {
        var result = new Dictionary<string, (int?, int?, long?, int?)>();
        try
        {
            foreach (var c in Wmi.Query("SELECT DeviceId, Wear, Temperature, TemperatureMax, PowerOnHours FROM MSFT_StorageReliabilityCounter", Wmi.Storage))
            {
                var wear = Wmi.Raw(c, "Wear") is null ? (int?)null : Wmi.Int(c, "Wear");
                var temp = Wmi.Raw(c, "Temperature") is null ? (int?)null : Wmi.Int(c, "Temperature");
                // Température de fonctionnement maximale normale indiquée par le disque (seuil d'alerte propre au modèle)
                var tempMax = Wmi.Raw(c, "TemperatureMax") is null ? (int?)null : Wmi.Int(c, "TemperatureMax");
                var hours = Wmi.Raw(c, "PowerOnHours") is null ? (long?)null : Wmi.Long(c, "PowerOnHours");
                // 0 °C signifie « non fourni » sur de nombreux disques
                result[Wmi.Str(c, "DeviceId")] = (
                    UsableWear(wear),
                    temp is > 0 and < 120 ? temp : null,
                    hours is > 0 ? hours : null,
                    tempMax is > 0 and < 120 ? tempMax : null);
            }
        }
        catch (Exception)
        {
            // Compteurs indisponibles : la santé ne sera simplement pas affichée.
        }
        return result;
    }

    /// <summary>
    /// Usure en % de la durée de vie prévue : 100 = limite atteinte, et un NVMe continue au-delà (« Percentage Used »
    /// jusqu'à 254) ; un SSD usé au-delà de 100 % est le cas le plus grave, pas une valeur à ignorer. 255 = non fourni.
    /// </summary>
    internal static int? UsableWear(int? wear) => wear is >= 0 and < 255 ? wear : null;
}
