namespace Aria.Core.Nvidia;

/// <summary>Valeur d'un réglage du pilote dans le profil global.</summary>
/// <param name="Value">Valeur lue, ou null si le réglage n'existe pas dans le profil (valeur d'usine du pilote).</param>
/// <param name="IsUserSet">Vrai si la valeur a été choisie par l'utilisateur (sinon : valeur prédéfinie par NVIDIA).</param>
public readonly record struct DriverValue(uint? Value, bool IsUserSet);

/// <summary>Lecture et écriture des réglages du pilote NVIDIA (profil global). Remplaçable pour les tests.</summary>
public interface IDriverSettings
{
    DriverValue Read(uint settingId);
    void Write(uint settingId, uint value);

    /// <summary>Remet la valeur d'usine (le réglage disparaît du profil global).</summary>
    void RestoreDefault(uint settingId);
}

/// <summary>
/// Identifiants et valeurs des réglages, tels que publiés par NVIDIA (SDK NVAPI, NvApiDriverSettings.h).
/// </summary>
public static class DrsIds
{
    public const uint LowLatency = 0x007BA09E;          // PRERENDERLIMIT_ID — « Maximum pre-rendered frames »
    public const uint VSync = 0x00A879CF;               // VSYNCMODE_ID
    public const uint FrameRateLimit = 0x10835002;      // FRL_FPS_ID
    public const uint PreferredRefresh = 0x0064B541;    // REFRESH_RATE_OVERRIDE_ID
    public const uint ShaderCacheSize = 0x00AC8497;     // PS_SHADERDISKCACHE_MAX_SIZE_ID (en Mo)
    public const uint PowerMode = 0x1057EB71;           // PREFERRED_PSTATE_ID
    public const uint OptimusCompat = 0x10F9DC80;       // SHIM_MCCOMPAT_ID
    public const uint OptimusRendering = 0x10F9DC81;    // SHIM_RENDERING_MODE_ID

    public const uint LowLatencyAppControlled = 0;      // PRERENDERLIMIT_APP_CONTROLLED
    public const uint VSyncPassive = 0x60925292;        // VSYNCMODE_PASSIVE (choix de l'application)
    public const uint VSyncForceOff = 0x08416747;
    public const uint VSyncForceOn = 0x47814940;
    public const uint VSyncHalf = 0x32610244;           // FLIPINTERVAL2
    public const uint VSyncThird = 0x71271021;          // FLIPINTERVAL3
    public const uint VSyncQuarter = 0x13245256;        // FLIPINTERVAL4
    public const uint VSyncFast = 0x18888888;           // VSYNCMODE_VIRTUAL (« Rapide »)
    public const uint FrameRateLimitOff = 0;
    public const uint RefreshAppControlled = 0;
    public const uint RefreshHighest = 1;
    public const uint ShaderCacheDefault = 0x4000;      // 16 Go, valeur d'usine du SDK R615
    public const uint ShaderCacheUnlimited = 0xFFFFFFFF;
    public const uint PowerAdaptive = 0;
    public const uint PowerPreferMax = 1;
    public const uint PowerDriverControlled = 2;
    public const uint PowerConsistent = 3;
    public const uint PowerPreferMin = 4;
    public const uint PowerOptimal = 5;                 // « Normal », valeur d'usine
    public const uint OptimusIntegrated = 0x00;
    public const uint OptimusNvidia = 0x01;
    public const uint OptimusAutoSelect = 0x10;         // valeur d'usine
}

/// <summary>
/// Implémentation réelle via NVAPI (DRS : Driver Settings). Chaque opération ouvre sa propre session :
/// chargement de la base du pilote, profil global courant (celui des « Paramètres globaux »), opération,
/// enregistrement si besoin, fermeture. Aucune session ne reste ouverte.
/// </summary>
public sealed class NvApiDriverSettings : IDriverSettings
{
    // NVDRS_SETTING_V1 : 12 320 octets (#pragma pack(4)), vérifiés au compilateur C à partir de nvapi.h
    private const int SettingSize = 12320;
    private const int OffsetSettingId = 4100;
    private const int OffsetSettingType = 4104;
    private const int OffsetIsCurrentPredefined = 4112;
    private const int OffsetCurrentValue = 8220;
    private static readonly uint SettingVersion = NvApi.MakeVersion(SettingSize, 1); // 0x13020
    private const uint DwordType = 0; // NVDRS_DWORD_TYPE

    public static IDriverSettings Instance { get; internal set; } = new NvApiDriverSettings();

    public DriverValue Read(uint settingId) => InSession(save: false, (session, profile) =>
    {
        var get = NvApi.Require<NvApi.GetSettingFn>(NvApi.IdDrsGetSetting, "NvAPI_DRS_GetSetting");
        using var setting = new NvApi.NativeBuffer(SettingSize);
        setting.WriteUInt32(0, SettingVersion);

        var status = get(session, profile, settingId, setting.Pointer);
        if (status == NvApi.SettingNotFound) return new DriverValue(null, false);
        NvApi.Check(status, "NvAPI_DRS_GetSetting");

        if (setting.ReadUInt32(OffsetSettingType) != DwordType)
            throw new InvalidOperationException($"Réglage 0x{settingId:X8} d'un type inattendu.");
        return new DriverValue(setting.ReadUInt32(OffsetCurrentValue), setting.ReadUInt32(OffsetIsCurrentPredefined) == 0);
    });

    public void Write(uint settingId, uint value) => InSession(save: true, (session, profile) =>
    {
        var set = NvApi.Require<NvApi.SetSettingFn>(NvApi.IdDrsSetSetting, "NvAPI_DRS_SetSetting");
        using var setting = new NvApi.NativeBuffer(SettingSize);
        setting.WriteUInt32(0, SettingVersion);
        setting.WriteUInt32(OffsetSettingId, settingId);
        setting.WriteUInt32(OffsetSettingType, DwordType);
        setting.WriteUInt32(OffsetCurrentValue, value);
        NvApi.Check(set(session, profile, setting.Pointer), "NvAPI_DRS_SetSetting");
        return true;
    });

    public void RestoreDefault(uint settingId) => InSession(save: true, (session, profile) =>
    {
        var restore = NvApi.Require<NvApi.ProfileSettingFn>(NvApi.IdDrsRestoreProfileDefaultSetting, "NvAPI_DRS_RestoreProfileDefaultSetting");
        var status = restore(session, profile, settingId);
        if (status != NvApi.SettingNotFound) NvApi.Check(status, "NvAPI_DRS_RestoreProfileDefaultSetting");
        return true;
    });

    private static T InSession<T>(bool save, Func<IntPtr, IntPtr, T> action)
    {
        var create = NvApi.Require<NvApi.CreateSessionFn>(NvApi.IdDrsCreateSession, "NvAPI_DRS_CreateSession");
        var destroy = NvApi.Require<NvApi.SessionFn>(NvApi.IdDrsDestroySession, "NvAPI_DRS_DestroySession");
        var load = NvApi.Require<NvApi.SessionFn>(NvApi.IdDrsLoadSettings, "NvAPI_DRS_LoadSettings");
        var global = NvApi.Require<NvApi.GetProfileFn>(NvApi.IdDrsGetCurrentGlobalProfile, "NvAPI_DRS_GetCurrentGlobalProfile");

        NvApi.Check(create(out var session), "NvAPI_DRS_CreateSession");
        try
        {
            NvApi.Check(load(session), "NvAPI_DRS_LoadSettings");
            NvApi.Check(global(session, out var profile), "NvAPI_DRS_GetCurrentGlobalProfile");
            if (profile == IntPtr.Zero) throw new NvApiException("NvAPI_DRS_GetCurrentGlobalProfile", -163, "profil global introuvable");

            var result = action(session, profile);
            if (save)
            {
                var saveSettings = NvApi.Require<NvApi.SessionFn>(NvApi.IdDrsSaveSettings, "NvAPI_DRS_SaveSettings");
                NvApi.Check(saveSettings(session), "NvAPI_DRS_SaveSettings");
            }
            return result;
        }
        finally
        {
            destroy(session);
        }
    }
}
