using System.Runtime.InteropServices;

namespace Aria.Core.Windows;

/// <summary>Paramètres système (souris, animations).</summary>
internal static class NativeSpi
{
    public const uint GetMouse = 0x0003;
    public const uint SetMouse = 0x0004;
    public const uint GetClientAreaAnimation = 0x1042;
    public const uint SetClientAreaAnimation = 0x1043;

    /// <summary>Enregistre le changement et prévient les applications ouvertes.</summary>
    public const uint SaveAndNotify = 0x01 | 0x02;

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint param, [In, Out] int[] values, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint param, ref int value, uint winIni);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);

    public static void ThrowLastError(string what) =>
        throw new InvalidOperationException($"{what} refusé par Windows (code {Marshal.GetLastWin32Error()}).");
}

/// <summary>Plans et modes d'alimentation.</summary>
internal static class NativePower
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    // Modes d'alimentation de Windows 11 (curseur "Mode d'alimentation", plan Équilibré uniquement)
    public static readonly Guid ModeBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    public static readonly Guid ModeBalanced = Guid.Empty;
    public static readonly Guid ModeBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");
    public static readonly Guid ModeBatterySaver = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f");

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr rootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlayGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlayGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, out uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerWriteACValueIndex(IntPtr rootPowerKey, ref Guid schemeGuid, ref Guid subGroupGuid, ref Guid settingGuid, uint value);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    // Sous-groupe "Gestion de l'alimentation du processeur" et réglage "État minimal du processeur"
    private static readonly Guid ProcessorSubGroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcessorMinState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public static Guid GetActivePlan()
    {
        Check(PowerGetActiveScheme(IntPtr.Zero, out var ptr), "Lecture du plan d'alimentation");
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { LocalFree(ptr); }
    }

    public static void SetActivePlan(Guid plan)
    {
        var copy = plan;
        Check(PowerSetActiveScheme(IntPtr.Zero, ref copy), "Changement du plan d'alimentation");
    }

    /// <summary>Le plan existe-t-il sur ce PC ? (Performances élevées est absent des PC en veille moderne)</summary>
    public static bool PlanExists(Guid plan)
    {
        var scheme = plan;
        var sub = ProcessorSubGroup;
        var setting = ProcessorMinState;
        return PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out _) == 0;
    }

    /// <summary>État minimal du processeur (%), sur secteur, pour un plan donné.</summary>
    public static int GetMinProcessorState(Guid plan)
    {
        var scheme = plan;
        var sub = ProcessorSubGroup;
        var setting = ProcessorMinState;
        Check(PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out var value), "Lecture de l'état minimal du processeur");
        return (int)value;
    }

    public static void SetMinProcessorState(Guid plan, int percent)
    {
        var scheme = plan;
        var sub = ProcessorSubGroup;
        var setting = ProcessorMinState;
        Check(PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, (uint)percent), "Réglage de l'état minimal du processeur");

        // Un plan actif ne relit ses valeurs qu'en étant réactivé
        if (GetActivePlan() == plan) SetActivePlan(plan);
    }

    public static Guid GetMode()
    {
        Check(PowerGetEffectiveOverlayScheme(out var mode), "Lecture du mode d'alimentation");
        return mode;
    }

    public static void SetMode(Guid mode) =>
        Check(PowerSetActiveOverlayScheme(mode), "Changement du mode d'alimentation");

    /// <summary>Vrai si l'ordinateur est sur secteur (ou si l'état est inconnu, cas d'un PC fixe).</summary>
    public static bool IsOnAcPower() =>
        !GetSystemPowerStatus(out var status) || status.ACLineStatus != 0;

    public static string PlanName(Guid plan) =>
        plan == Balanced ? "Équilibré"
        : plan == HighPerformance ? "Performances élevées"
        : plan == PowerSaver ? "Économie d'énergie"
        : plan == Ultimate ? "Performances optimales (plan)"
        : "Personnalisé";

    public static string ModeName(Guid mode) =>
        mode == ModeBestPerformance ? "Performances optimales"
        : mode == ModeBalanced ? "Équilibré"
        : mode == ModeBestEfficiency ? "Efficacité énergétique optimale"
        : mode == ModeBatterySaver ? "Économiseur de batterie"
        : "Autre";

    private static void Check(uint result, string what)
    {
        if (result != 0) throw new InvalidOperationException($"{what} refusé par Windows (code {result}).");
    }
}
