using System.Diagnostics;
using System.Text.RegularExpressions;
using Aria.Core.Models;

namespace Aria.Core.Inventory;

/// <summary>Cartes réseau physiques connectées, avec leur vitesse de liaison (et la norme et le signal en Wi-Fi).</summary>
internal static partial class NetworkCollector
{
    private const string StandardCimv2 = @"root\StandardCimv2";

    public static IReadOnlyList<NetworkAdapterInfo> Collect()
    {
        var adapters = Wmi.Query(
                "SELECT Name, InterfaceDescription, NdisPhysicalMedium, Speed, MediaConnectState, HardwareInterface " +
                "FROM MSFT_NetAdapter WHERE HardwareInterface = TRUE AND MediaConnectState = 1",
                StandardCimv2)
            .Select(a => new NetworkAdapterInfo(
                Name: Wmi.Str(a, "Name"),
                Description: Wmi.Str(a, "InterfaceDescription"),
                Kind: Wmi.Int(a, "NdisPhysicalMedium") switch
                {
                    14 => NetworkKind.Ethernet, // 802.3
                    9 => NetworkKind.WiFi,      // 802.11 natif
                    _ => NetworkKind.Other
                },
                SpeedBps: Wmi.Long(a, "Speed")))
            .Where(a => a.Kind != NetworkKind.Other)
            .ToList();

        // Norme et signal Wi-Fi : uniquement si une carte Wi-Fi est connectée
        if (adapters.Any(a => a.Kind == NetworkKind.WiFi) && RunNetsh() is { } output)
        {
            var (standard, signal) = NetshParser.Parse(output);
            adapters = adapters
                .Select(a => a.Kind == NetworkKind.WiFi ? a with { WifiStandard = standard, WifiSignalPercent = signal } : a)
                .ToList();
        }

        return adapters.OrderBy(a => a.Kind).ToList();
    }

    private static string? RunNetsh()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("netsh", "wlan show interfaces")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return null;
            }
            return output.Result;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Analyse de « netsh wlan show interfaces », quelle que soit la langue de Windows.</summary>
internal static partial class NetshParser
{
    public static (string? Standard, int? SignalPercent) Parse(string output)
    {
        string? standard = null;
        var radio = RadioType().Match(output);
        if (radio.Success)
        {
            var code = radio.Groups["std"].Value.ToLowerInvariant();
            var generation = code switch
            {
                "be" => "Wi-Fi 7",
                "ax" => "Wi-Fi 6/6E",
                "ac" => "Wi-Fi 5",
                "n" => "Wi-Fi 4",
                _ => null
            };
            standard = generation is null ? $"802.11{code}" : $"{generation} (802.11{code})";
        }

        // La ligne du signal s'appelle « Signal » en français comme en anglais
        var signal = SignalLine().Match(output);
        int? percent = signal.Success && int.TryParse(signal.Groups["p"].Value, out var p) ? p : null;
        return (standard, percent);
    }

    [GeneratedRegex(@"802\.11(?<std>be|ax|ac|n|g|a|b)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RadioType();

    [GeneratedRegex(@"^\s*Signal\s*:\s*(?<p>\d{1,3})\s*%", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SignalLine();
}
