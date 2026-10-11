using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Aria.Core.Updates;

/// <summary>Mises à jour des applications via winget, le gestionnaire de paquets officiel de Windows.</summary>
public sealed class WingetService
{
    private const uint NoPackagesFound = 0x8A150014;
    private const uint UpdateNotApplicable = 0x8A15002B;

    /// <summary>Paquets qu'Aria ne propose pas (avec la raison).</summary>
    private static readonly Dictionary<string, string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Microsoft.AppInstaller"] = "Contient winget lui-même : il se met à jour par le Microsoft Store."
    };

    public ProviderResult Search()
    {
        var run = WingetRunner.Run("upgrade --include-unknown --accept-source-agreements --disable-interactivity", TimeSpan.FromMinutes(3));
        if (run.NotFound)
            return ProviderResult.Failed("winget est introuvable : installez « Programme d'installation d'application » (App Installer) depuis le Microsoft Store.");
        if (run.TimedOut) return ProviderResult.Failed("winget n'a pas répondu dans les temps.");

        var table = WingetParser.ParseUpgrades(run.Output);
        if (table is null)
        {
            var code = unchecked((uint)run.ExitCode);
            return code is 0 or NoPackagesFound or UpdateNotApplicable
                ? new ProviderResult([], [], null)
                : ProviderResult.Failed($"winget a échoué (code 0x{code:X8}) : {WingetParser.LastMessage(run.Output)}");
        }

        var items = new List<UpdateItem>();
        var skipped = table.Unreadable.Select(name => new SkippedUpdate(name, "Ligne illisible dans la réponse de winget.")).ToList();

        foreach (var row in table.Rows)
        {
            if (Excluded.TryGetValue(row.Id, out var why))
            {
                skipped.Add(new SkippedUpdate(row.Name, why));
                continue;
            }
            if (row.Id.EndsWith('…'))
            {
                skipped.Add(new SkippedUpdate(row.Name, "Identifiant tronqué par winget : mettez-la à jour depuis l'application elle-même."));
                continue;
            }

            var unknown = !row.Version.Any(char.IsDigit);
            items.Add(new UpdateItem
            {
                Key = $"winget:{row.Id}",
                Source = UpdateSource.Application,
                Title = row.Name,
                CurrentVersion = unknown ? null : row.Version,
                NewVersion = row.Available,
                Reason = unknown
                    ? "winget ne connaît pas la version installée : la mise à jour peut réinstaller la même version."
                    : $"Nouvelle version proposée par winget ({row.Id}).",
                Flags = unknown ? UpdateFlags.UnknownVersion : UpdateFlags.None,
                SelectedByDefault = !unknown,
                PackageId = row.Id,
                PackageSource = row.Source
            });
        }
        return new ProviderResult(items, skipped, null);
    }

    /// <param name="onProgress">Appelé pendant l'installation (téléchargement, puis installation).</param>
    public InstallResult Install(UpdateItem item, Action<WingetProgress>? onProgress = null)
    {
        if (item.PackageId is not { } id) return InstallResult.Fail(item, "Identifiant de paquet manquant.");

        var arguments = $"upgrade --id \"{id}\" --exact --silent --include-unknown " +
                        "--accept-package-agreements --accept-source-agreements --disable-interactivity" +
                        (string.IsNullOrEmpty(item.PackageSource) ? "" : $" --source \"{item.PackageSource}\"");

        // Mises à jour limitées à quelques par seconde pour ne pas saturer l'affichage
        var parser = new WingetProgressParser();
        var lastText = "";
        var lastReport = DateTime.MinValue;
        void OnSegment(string segment)
        {
            if (onProgress is null || parser.Feed(segment) is not { } progress || progress.Text == lastText) return;
            var now = DateTime.UtcNow;
            if (progress.Fraction is > 0 and < 1 && now - lastReport < TimeSpan.FromMilliseconds(150)) return;
            lastText = progress.Text;
            lastReport = now;
            onProgress(progress);
        }

        var run = WingetRunner.Run(arguments, TimeSpan.FromMinutes(20), OnSegment);
        if (run.NotFound) return InstallResult.Fail(item, "winget est introuvable.");
        if (run.TimedOut) return InstallResult.Fail(item, "Installation trop longue (plus de 20 minutes) : arrêtée.");

        return WingetParser.InterpretExitCode(run.ExitCode, run.Output) switch
        {
            (InstallStatus.Installed, var reboot, _) => InstallResult.Ok(item, reboot),
            (InstallStatus.UpToDate, _, var message) => new InstallResult(item, InstallStatus.UpToDate, message, false),
            (_, _, var message) => InstallResult.Fail(item, message ?? "Échec de l'installation.")
        };
    }
}

internal sealed record WingetRun(int ExitCode, string Output, bool NotFound, bool TimedOut);

internal static class WingetRunner
{
    /// <param name="onSegment">Reçoit chaque morceau écrit par winget, découpé sur « \r » et « \n » (barres de progression).</param>
    public static WingetRun Run(string arguments, TimeSpan timeout, Action<string>? onSegment = null)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("winget", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true
            });
            if (process is null) return new WingetRun(-1, "", NotFound: true, TimedOut: false);

            var output = new StringBuilder();
            var reading = Task.Run(async () =>
            {
                var buffer = new char[4096];
                var segment = new StringBuilder();
                int count;
                while ((count = await process.StandardOutput.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    output.Append(buffer, 0, count);
                    if (onSegment is null) continue;
                    for (var i = 0; i < count; i++)
                    {
                        if (buffer[i] is '\r' or '\n')
                        {
                            if (segment.Length > 0) SafeNotify(onSegment, segment.ToString());
                            segment.Clear();
                        }
                        else segment.Append(buffer[i]);
                    }
                }
                if (onSegment is not null && segment.Length > 0) SafeNotify(onSegment, segment.ToString());
            });
            var error = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeout))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return new WingetRun(-1, "", NotFound: false, TimedOut: true);
            }
            process.WaitForExit();
            reading.Wait(TimeSpan.FromSeconds(10));
            return new WingetRun(process.ExitCode, output + "\n" + error.Result, NotFound: false, TimedOut: false);
        }
        catch (Win32Exception)
        {
            return new WingetRun(-1, "", NotFound: true, TimedOut: false);
        }
    }

    /// <summary>Un souci d'affichage ne doit jamais interrompre une installation.</summary>
    private static void SafeNotify(Action<string> onSegment, string segment)
    {
        try { onSegment(segment); }
        catch (Exception) { }
    }
}
