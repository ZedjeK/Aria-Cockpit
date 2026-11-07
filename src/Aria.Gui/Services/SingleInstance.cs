using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Aria.Gui.Services;

/// <summary>
/// Une seule fenêtre Aria Cockpit par session Windows. Un nouveau lancement prévient l'exemplaire déjà ouvert, qui
/// revient au premier plan, au lieu d'en ouvrir un second (deux exemplaires se disputeraient la courbe de ventilation,
/// le réglage GPU et les sauvegardes). La tâche de démarrage du profil GPU, sans fenêtre, n'est pas concernée.
/// Repris de CTI-Cockpit.
/// </summary>
public static class SingleInstance
{
    private const string MutexName = @"Local\Aria-Cockpit.Instance";

    private static readonly uint ActivateMessage = RegisterWindowMessage("Aria-Cockpit.Activate");

    private static Mutex? _mutex;

    /// <summary>Devient l'exemplaire de la session. Faux si un autre l'est déjà.</summary>
    public static bool Claim()
    {
        _mutex = new Mutex(true, MutexName, out var created);
        if (created) return true;
        _mutex.Dispose();
        _mutex = null;
        return false;
    }

    /// <summary>Demande à l'exemplaire ouvert de revenir au premier plan.</summary>
    public static void SignalRunning()
    {
        AllowSetForegroundWindow(AnyProcess);   // l'exemplaire ouvert a le droit de passer devant
        PostMessage(BroadcastHandle, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Écoute les demandes des lancements suivants.</summary>
    public static void Listen(Window window, Action onRequest)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        HwndSource.FromHwnd(handle)!.AddHook((IntPtr _, int msg, IntPtr _, IntPtr _, ref bool handled) =>
        {
            if (msg != ActivateMessage) return IntPtr.Zero;
            onRequest();
            handled = true;
            return IntPtr.Zero;
        });
    }

    private static readonly IntPtr BroadcastHandle = new(0xFFFF);
    private const int AnyProcess = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int processId);
}
