using System.Runtime.InteropServices;

namespace Aria.Bench.Native;

/// <summary>
/// Fenêtre Win32 minimale pour le rendu : plein écran sans bordure (écran principal) ou fenêtre fixe de 1280 × 720.
/// Échap ou la croix arrêtent le banc d'essai. Pas de redimensionnement : la taille ne change pas pendant la mesure.
/// </summary>
internal sealed unsafe class Win32Window : IDisposable
{
    private const string ClassName = "AriaBenchWindow";
    private static volatile bool s_closeRequested;

    public IntPtr Handle { get; }
    public int ClientWidth { get; }
    public int ClientHeight { get; }
    public bool Fullscreen { get; }

    public Win32Window(string title, bool fullscreen, int windowedWidth = 1280, int windowedHeight = 720)
    {
        Fullscreen = fullscreen;
        s_closeRequested = false;
        var instance = GetModuleHandleW(null);

        fixed (char* className = ClassName)
        fixed (char* windowTitle = title)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                style = CS_HREDRAW | CS_VREDRAW,
                lpfnWndProc = &WndProc,
                hInstance = instance,
                hCursor = LoadCursorW(IntPtr.Zero, IDC_ARROW),
                lpszClassName = className
            };
            if (RegisterClassExW(&wc) == 0 && Marshal.GetLastPInvokeError() != ERROR_CLASS_ALREADY_EXISTS)
                throw new BenchException($"Création de la fenêtre impossible (classe, erreur {Marshal.GetLastPInvokeError()}).");

            int x, y, width, height;
            uint style, exStyle;
            if (fullscreen)
            {
                style = WS_POPUP;
                exStyle = WS_EX_APPWINDOW;
                (x, y) = (0, 0);
                width = GetSystemMetrics(SM_CXSCREEN);
                height = GetSystemMetrics(SM_CYSCREEN);
            }
            else
            {
                style = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
                exStyle = WS_EX_APPWINDOW;
                var rect = new RECT { right = windowedWidth, bottom = windowedHeight };
                AdjustWindowRectEx(&rect, style, 0, exStyle);
                (x, y) = (CW_USEDEFAULT, CW_USEDEFAULT);
                width = rect.right - rect.left;
                height = rect.bottom - rect.top;
            }

            Handle = CreateWindowExW(exStyle, className, windowTitle, style, x, y, width, height,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (Handle == IntPtr.Zero)
                throw new BenchException($"Création de la fenêtre impossible (erreur {Marshal.GetLastPInvokeError()}).");
        }

        ShowWindow(Handle, SW_SHOW);
        SetForegroundWindow(Handle);
        if (fullscreen) ShowCursor(0);

        RECT client;
        GetClientRect(Handle, &client);
        ClientWidth = Math.Max(1, client.right - client.left);
        ClientHeight = Math.Max(1, client.bottom - client.top);
    }

    /// <summary>Traite les messages en attente. Renvoie faux si l'utilisateur a demandé l'arrêt.</summary>
    public bool PumpMessages()
    {
        MSG msg;
        while (PeekMessageW(&msg, IntPtr.Zero, 0, 0, PM_REMOVE) != 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        return !s_closeRequested;
    }

    /// <summary>Rend le processus conscient de la mise à l'échelle par écran : le plein écran couvre alors les vrais pixels.</summary>
    public static void EnablePerMonitorDpi()
    {
        try { SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); }
        catch (EntryPointNotFoundException) { /* Windows trop ancien : sans effet */ }
    }

    public void Dispose()
    {
        if (Fullscreen) ShowCursor(1);
        DestroyWindow(Handle);
    }

    [UnmanagedCallersOnly]
    private static IntPtr WndProc(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case WM_KEYDOWN when wParam == VK_ESCAPE:
            case WM_CLOSE:
                s_closeRequested = true;
                return IntPtr.Zero;
            case WM_SYSKEYDOWN when wParam == VK_RETURN:
                return IntPtr.Zero; // pas d'Alt+Entrée
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    // ---- Win32 ----

    private const uint CS_VREDRAW = 0x1, CS_HREDRAW = 0x2;
    private const uint WS_OVERLAPPED = 0x0, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000, WS_MINIMIZEBOX = 0x00020000, WS_POPUP = 0x80000000;
    private const uint WS_EX_APPWINDOW = 0x00040000;
    private const int CW_USEDEFAULT = unchecked((int)0x80000000);
    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, SW_SHOW = 5;
    private const uint PM_REMOVE = 0x1;
    private const uint WM_CLOSE = 0x0010, WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private static readonly IntPtr VK_ESCAPE = 0x1B, VK_RETURN = 0x0D, IDC_ARROW = 32512;
    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;
    private const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    [StructLayout(LayoutKind.Sequential)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public delegate* unmanaged<IntPtr, uint, IntPtr, IntPtr, IntPtr> lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public char* lpszMenuName;
        public char* lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left, top, right, bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
        public uint lPrivate;
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)] private static extern ushort RegisterClassExW(WNDCLASSEXW* wc);
    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, char* className, char* windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int PeekMessageW(MSG* msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int TranslateMessage(MSG* msg);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr DispatchMessageW(MSG* msg);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int AdjustWindowRectEx(RECT* rect, uint style, int menu, uint exStyle);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int GetClientRect(IntPtr hwnd, RECT* rect);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr name);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int ShowCursor(int show);
    [DllImport("user32.dll", ExactSpelling = true)] private static extern int SetProcessDpiAwarenessContext(IntPtr context);
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern IntPtr GetModuleHandleW(char* moduleName);
}
