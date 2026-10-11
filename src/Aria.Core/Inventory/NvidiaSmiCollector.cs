using System.Diagnostics;
using Aria.Core.Models;

namespace Aria.Core.Inventory;

/// <summary>
/// Lit le lien PCIe et le Resizable BAR via nvidia-smi, installé avec tout pilote NVIDIA.
/// (Le module NVAPI de l'étape 5 prendra le relais pour les réglages du pilote.)
/// </summary>
internal static class NvidiaSmiCollector
{
    public static NvidiaLinkInfo? Collect(List<string> warnings)
    {
        var exe = FindNvidiaSmi();
        if (exe is null)
        {
            warnings.Add("nvidia-smi introuvable : lien PCIe et Resizable BAR non vérifiés.");
            return null;
        }

        // "gpumax" n'existe que sur les pilotes récents : on réessaie sans si la requête échoue.
        var link = Run(exe, "--query-gpu=pcie.link.gen.current,pcie.link.gen.max,pcie.link.width.current,pcie.link.width.max,pcie.link.gen.gpumax --format=csv,noheader,nounits")
                ?? Run(exe, "--query-gpu=pcie.link.gen.current,pcie.link.gen.max,pcie.link.width.current,pcie.link.width.max --format=csv,noheader,nounits");

        var memory = Run(exe, "-q -d MEMORY");

        if (link is null)
        {
            warnings.Add("nvidia-smi n'a pas répondu : lien PCIe non vérifié.");
            return null;
        }

        var (genCur, genMax, widthCur, widthMax, genGpuMax) = NvidiaSmiParser.ParseLink(link);
        return new NvidiaLinkInfo(genCur, genMax, genGpuMax, widthCur, widthMax,
            memory is null ? 0 : NvidiaSmiParser.ParseBar1TotalMiB(memory));
    }

    private static string? FindNvidiaSmi()
    {
        string[] candidates =
        [
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? Run(string exe, string arguments)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(exe, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return null;

            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                process.Kill();
                return null;
            }
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>Analyse des sorties texte de nvidia-smi (séparée pour être testable).</summary>
internal static class NvidiaSmiParser
{
    /// <summary>Ligne CSV "4, 4, 16, 16, 4" → valeurs (0 si "[N/A]" ou absente). Seule la 1re carte est lue.</summary>
    public static (int GenCur, int GenMax, int WidthCur, int WidthMax, int GenGpuMax) ParseLink(string csv)
    {
        var line = csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";
        var v = line.Split(',').Select(p => int.TryParse(p.Trim(), out var n) ? n : 0).ToArray();
        int At(int i) => i < v.Length ? v[i] : 0;
        return (At(0), At(1), At(2), At(3), At(4));
    }

    /// <summary>Cherche "Total : 16384 MiB" dans la section "BAR1 Memory Usage".</summary>
    public static int ParseBar1TotalMiB(string text)
    {
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].Contains("BAR1", StringComparison.OrdinalIgnoreCase)) continue;

            for (var j = i + 1; j < Math.Min(i + 5, lines.Length); j++)
            {
                var parts = lines[j].Split(':', 2);
                if (parts.Length < 2 || !parts[0].Trim().Equals("Total", StringComparison.OrdinalIgnoreCase)) continue;

                var number = new string(parts[1].Trim().TakeWhile(char.IsDigit).ToArray());
                return int.TryParse(number, out var mib) ? mib : 0;
            }
        }
        return 0;
    }
}
