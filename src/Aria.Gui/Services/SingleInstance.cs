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
    private static readonly uint HandOffMessage = RegisterWindowMessage("Aria-Cockpit.HandOff");

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

    /// <summary>
    /// Rend la place d'exemplaire de la session, juste avant de lancer une autre version qui la prendra (mise à jour,
    /// ou retour à la version gardée, qu'un lancement ordinaire refuserait parce qu'elle est plus ancienne).
    /// À appeler sur le fil qui a appelé <see cref="Claim"/>.
    /// </summary>
    public static void Release()
    {
        if (_mutex is null) return;
        try
        {
            _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Pas tenu par ce fil : le fermer suffit à le rendre
        }
        _mutex.Dispose();
        _mutex = null;
    }

    /// <summary>Demande à l'exemplaire ouvert de revenir au premier plan.</summary>
    public static void SignalRunning()
    {
        AllowSetForegroundWindow(AnyProcess);   // l'exemplaire ouvert a le droit de passer devant
        PostMessage(BroadcastHandle, ActivateMessage, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Version de cet exe (« 0.24.0 ») ; null si illisible.</summary>
    public static string? OwnVersion { get; } = VersionOf(Environment.ProcessPath);

    private static string? VersionOf(string? exe)
    {
        try
        {
            return exe is null ? null : System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).ProductVersion;
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Version d'un autre Aria Cockpit de cette session Windows ; null s'il n'y en a pas ou si elle est illisible.
    /// Sert seulement à décider quoi demander : l'exemplaire en place revérifie de son côté (<see cref="Listen"/>).
    /// </summary>
    public static string? RunningVersion()
    {
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        foreach (var other in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
        {
            using (other)
            {
                try
                {
                    if (other.Id == me.Id || other.SessionId != me.SessionId) continue;
                    if (VersionOf(other.MainModule?.FileName) is { Length: > 0 } version) return version;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Processus terminé entre-temps, ou illisible : on regarde le suivant
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Demande à l'exemplaire en place, plus ancien, de s'arrêter, et attend qu'il rende la session. Vrai si ce
    /// lancement est devenu l'exemplaire de la session ; faux si l'autre n'a pas cédé à temps.
    /// </summary>
    public static bool TakeOver(Version mine, TimeSpan timeout)
    {
        PostMessage(BroadcastHandle, HandOffMessage, new IntPtr(Pack(mine)), IntPtr.Zero);
        var mutex = new Mutex(false, MutexName);
        try
        {
            if (!mutex.WaitOne(timeout))
            {
                mutex.Dispose();
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
            // L'autre exemplaire s'est arrêté sans libérer le verrou : il est à nous
        }
        _mutex = mutex;
        return true;
    }

    /// <summary>Version tenant dans un paramètre de message : 10 bits par nombre (0 à 1023).</summary>
    internal static int Pack(Version v) =>
        (Math.Clamp(v.Major, 0, 1023) << 20) | (Math.Clamp(v.Minor, 0, 1023) << 10) | Math.Clamp(v.Build, 0, 1023);

    internal static Version Unpack(int packed) => new((packed >> 20) & 1023, (packed >> 10) & 1023, packed & 1023);

    /// <summary>Écoute les demandes des lancements suivants.</summary>
    /// <param name="onRequest">Un lancement de la même version : revenir au premier plan.</param>
    /// <param name="onHandOff">Un lancement de la version donnée demande la place : à l'exemplaire en place de céder, ou non.</param>
    public static void Listen(Window window, Action onRequest, Action<Version> onHandOff)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        HwndSource.FromHwnd(handle)!.AddHook((IntPtr _, int msg, IntPtr wParam, IntPtr _, ref bool handled) =>
        {
            if (msg == ActivateMessage) onRequest();
            else if (msg == HandOffMessage) onHandOff(Unpack(wParam.ToInt32()));
            else return IntPtr.Zero;
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
