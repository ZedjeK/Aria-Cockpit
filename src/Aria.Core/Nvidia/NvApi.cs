using System.Runtime.InteropServices;
using System.Text;

namespace Aria.Core.Nvidia;

/// <summary>Erreur renvoyée par le pilote NVIDIA, avec un message lisible.</summary>
public sealed class NvApiException(string function, int status, string message)
    : Exception($"{message} ({function}, code {status})")
{
    public string Function { get; } = function;
    public int Status { get; } = status;
}

/// <summary>
/// Accès au pilote NVIDIA via nvapi64.dll (installé avec tout pilote NVIDIA).
/// Chaque fonction est obtenue par son identifiant officiel, publié par NVIDIA dans le SDK NVAPI
/// (github.com/NVIDIA/nvapi, licence MIT, fichier nvapi_interface.h). Aucune valeur non documentée.
/// </summary>
internal static class NvApi
{
    public const int Ok = 0;
    public const int SettingNotFound = -160;

    // ---- Identifiants officiels (nvapi_interface.h) ----
    public const uint IdInitialize = 0x0150E828;
    public const uint IdGetErrorMessage = 0x6C2D048C;
    public const uint IdDispGetDisplayIdByDisplayName = 0xAE457190;
    public const uint IdDispGetVrrInfo = 0xDF8FDA57;
    public const uint IdDispColorControl = 0x92F9D80D;
    public const uint IdDrsCreateSession = 0x0694D52E;
    public const uint IdDrsDestroySession = 0xDAD9CFF8;
    public const uint IdDrsLoadSettings = 0x375DBD6B;
    public const uint IdDrsSaveSettings = 0xFCBC7E14;
    public const uint IdDrsGetCurrentGlobalProfile = 0x617BFF9F;
    public const uint IdDrsSetSetting = 0x577DD202;
    public const uint IdDrsGetSetting = 0x73BF8338;
    public const uint IdDrsRestoreProfileDefaultSetting = 0x53F0381E;

    // ---- Signatures (nvapi.h) ----
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterfaceFn(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int InitializeFn();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetErrorMessageFn(int status, [Out] byte[] description);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int GetDisplayIdByDisplayNameFn([MarshalAs(UnmanagedType.LPStr)] string displayName, out uint displayId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int DisplayStructFn(uint displayId, IntPtr data);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int CreateSessionFn(out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int SessionFn(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetProfileFn(IntPtr session, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int GetSettingFn(IntPtr session, IntPtr profile, uint settingId, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int SetSettingFn(IntPtr session, IntPtr profile, IntPtr setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ProfileSettingFn(IntPtr session, IntPtr profile, uint settingId);

    private static readonly object Gate = new();
    private static bool _attempted;
    private static string? _error;
    private static QueryInterfaceFn? _query;

    /// <summary>Charge et initialise NVAPI une seule fois. null si tout va bien, sinon la raison.</summary>
    public static string? EnsureReady()
    {
        lock (Gate)
        {
            if (!_attempted)
            {
                _attempted = true;
                _error = Load();
            }
            return _error;
        }
    }

    private static string? Load()
    {
        if (!OperatingSystem.IsWindows()) return "NVAPI n'existe que sous Windows";
        if (!Environment.Is64BitProcess) return "NVAPI 64 bits requiert un programme 64 bits";
        if (!NativeLibrary.TryLoad("nvapi64.dll", out var library))
            return "nvapi64.dll introuvable : pilote NVIDIA absent ou incomplet";
        if (!NativeLibrary.TryGetExport(library, "nvapi_QueryInterface", out var query))
            return "nvapi64.dll ne fournit pas nvapi_QueryInterface";

        _query = Marshal.GetDelegateForFunctionPointer<QueryInterfaceFn>(query);
        var initialize = Get<InitializeFn>(IdInitialize);
        if (initialize is null) return "NvAPI_Initialize absent de ce pilote";

        var status = initialize();
        return status == Ok ? null : $"initialisation de NVAPI refusée : {Describe(status)}";
    }

    private static T? Get<T>(uint id) where T : Delegate
    {
        if (_query is null) return null;
        var pointer = _query(id);
        return pointer == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    /// <summary>Fonction du pilote, ou exception claire si NVAPI est indisponible ou si le pilote est trop ancien.</summary>
    public static T Require<T>(uint id, string name) where T : Delegate
    {
        if (EnsureReady() is { } error) throw new InvalidOperationException($"Pilote NVIDIA injoignable : {error}");
        return Get<T>(id) ?? throw new NvApiException(name, -136, "fonction absente de ce pilote (pilote trop ancien ?)");
    }

    public static void Check(int status, string function)
    {
        if (status != Ok) throw new NvApiException(function, status, Describe(status));
    }

    /// <summary>Codes d'erreur de nvapi_lite_common.h, en français ; sinon le message du pilote.</summary>
    public static string Describe(int status) => status switch
    {
        -1 => "erreur du pilote",
        -2 => "bibliothèque NVAPI introuvable",
        -3 => "non disponible avec ce pilote",
        -4 => "NVAPI non initialisé",
        -5 => "paramètre refusé par le pilote",
        -6 => "aucune carte NVIDIA ne pilote d'écran",
        -9 => "version de structure non prise en charge par ce pilote",
        -136 => "fonction absente de ce pilote",
        -137 => "droits administrateur nécessaires",
        -160 => "réglage introuvable",
        -163 => "profil introuvable",
        _ => DriverMessage(status) ?? "erreur inconnue"
    };

    private static string? DriverMessage(int status)
    {
        try
        {
            var getMessage = Get<GetErrorMessageFn>(IdGetErrorMessage);
            if (getMessage is null) return null;
            var buffer = new byte[64]; // NvAPI_ShortString
            if (getMessage(status, buffer) != Ok) return null;
            var text = Encoding.ASCII.GetString(buffer).TrimEnd('\0').Trim();
            return text.Length == 0 ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Bloc mémoire natif mis à zéro, libéré automatiquement.</summary>
    public sealed class NativeBuffer : IDisposable
    {
        public NativeBuffer(int size)
        {
            Size = size;
            Pointer = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, Pointer, size);
        }

        public IntPtr Pointer { get; }
        public int Size { get; }

        public uint ReadUInt32(int offset) => unchecked((uint)Marshal.ReadInt32(Pointer, offset));
        public void WriteUInt32(int offset, uint value) => Marshal.WriteInt32(Pointer, offset, unchecked((int)value));
        public byte ReadByte(int offset) => Marshal.ReadByte(Pointer, offset);
        public void WriteByte(int offset, byte value) => Marshal.WriteByte(Pointer, offset, value);
        public void WriteUInt16(int offset, ushort value) => Marshal.WriteInt16(Pointer, offset, unchecked((short)value));

        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }

    /// <summary>MAKE_NVAPI_VERSION(type, version) = taille | (version &lt;&lt; 16).</summary>
    public static uint MakeVersion(int size, int version) => (uint)size | ((uint)version << 16);
}
