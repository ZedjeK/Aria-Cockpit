using System.Diagnostics;

namespace Aria.Gui.Services;

public static class RestartService
{
    /// <summary>Redémarre dans 10 secondes, normalement ou directement dans le BIOS (UEFI uniquement).</summary>
    public static void Restart(bool intoBios) =>
        Process.Start(new ProcessStartInfo("shutdown", intoBios ? "/r /fw /t 10" : "/r /t 10")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
}
