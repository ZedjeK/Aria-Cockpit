namespace Aria.Core.Nvidia;

/// <summary>État G-SYNC d'un écran (NV_GET_VRR_INFO, pilote 525 ou plus récent).</summary>
/// <param name="Enabled">VRR actif sur l'écran.</param>
/// <param name="Possible">L'écran et le branchement permettent G-SYNC.</param>
/// <param name="Requested">G-SYNC demandé pour cet écran (activé dans les réglages NVIDIA).</param>
/// <param name="InVrrMode">L'écran est en ce moment en mode VRR (jeu en cours, par exemple).</param>
public sealed record VrrState(bool Enabled, bool Possible, bool Requested, bool InVrrMode)
{
    /// <summary>G-SYNC activé pour cet écran.</summary>
    public bool IsOn => Requested || Enabled || InVrrMode;
}

public enum ColorPolicy { Driver, User }

public enum DynamicRange { Full, Limited, Auto }

/// <summary>Couleurs envoyées à l'écran (NV_COLOR_DATA).</summary>
/// <param name="Policy">Driver : « Utiliser les paramètres de couleur NVIDIA par défaut » ; User : réglage manuel.</param>
public sealed record ColorState(ColorPolicy Policy, DynamicRange Range, byte ColorFormat)
{
    public bool IsRgb => ColorFormat == 0; // NV_COLOR_FORMAT_RGB
}

/// <summary>Accès aux réglages d'écran du pilote NVIDIA. Remplaçable pour les tests.</summary>
public interface INvidiaDisplays
{
    /// <summary>null si le pilote ne sait pas répondre (trop ancien, écran non piloté par NVIDIA…).</summary>
    VrrState? ReadVrr(string deviceName);

    ColorState ReadColor(string deviceName);

    /// <summary>Règle la plage dynamique ; Policy = Driver rend la main au pilote (réglage d'usine).</summary>
    void SetColor(string deviceName, ColorPolicy policy, DynamicRange range);
}

/// <summary>Implémentation réelle via NVAPI, à partir du nom Windows de l'écran (\\.\DISPLAY1…).</summary>
public sealed class NvApiDisplays : INvidiaDisplays
{
    // NV_GET_VRR_INFO_V1 : 24 octets
    private const int VrrSize = 24;
    private static readonly uint VrrVersion = NvApi.MakeVersion(VrrSize, 1); // 0x10018

    // NV_COLOR_DATA_V5 : 24 octets, données à partir de l'octet 8 (vérifié au compilateur C)
    private const int ColorSize = 24;
    private static readonly uint ColorVersion = NvApi.MakeVersion(ColorSize, 5); // 0x50018
    private const int OffsetCmd = 6;
    private const int OffsetFormat = 8;
    private const int OffsetColorimetry = 9;
    private const int OffsetRange = 10;
    private const int OffsetBpc = 12;
    private const int OffsetPolicy = 16;
    private const int OffsetDepth = 20;

    private const byte CmdGet = 1, CmdSet = 2, CmdIsSupported = 3;
    private const byte RangeVesa = 0, RangeCea = 1, RangeAuto = 0xFF;
    private const byte FormatDefault = 0xFE, ColorimetryDefault = 0xFE;
    private const uint PolicyUser = 0, PolicyBestQuality = 1;

    public static INvidiaDisplays Instance { get; internal set; } = new NvApiDisplays();

    public VrrState? ReadVrr(string deviceName)
    {
        if (NvApi.EnsureReady() is not null) return null;
        try
        {
            var id = DisplayId(deviceName);
            var getVrr = NvApi.Require<NvApi.DisplayStructFn>(NvApi.IdDispGetVrrInfo, "NvAPI_Disp_GetVRRInfo");
            using var info = new NvApi.NativeBuffer(VrrSize);
            info.WriteUInt32(0, VrrVersion);
            NvApi.Check(getVrr(id, info.Pointer), "NvAPI_Disp_GetVRRInfo");

            var bits = info.ReadUInt32(4);
            return new VrrState(
                Enabled: (bits & 0x1) != 0,
                Possible: (bits & 0x2) != 0,
                Requested: (bits & 0x4) != 0,
                InVrrMode: (bits & 0x10) != 0);
        }
        catch (NvApiException)
        {
            return null;
        }
    }

    public ColorState ReadColor(string deviceName)
    {
        using var data = Color(deviceName, CmdGet, prepare: null);
        return new ColorState(
            data.ReadUInt32(OffsetPolicy) == PolicyUser ? ColorPolicy.User : ColorPolicy.Driver,
            data.ReadByte(OffsetRange) switch { RangeVesa => DynamicRange.Full, RangeCea => DynamicRange.Limited, _ => DynamicRange.Auto },
            data.ReadByte(OffsetFormat));
    }

    public void SetColor(string deviceName, ColorPolicy policy, DynamicRange range)
    {
        if (policy == ColorPolicy.Driver)
        {
            // Retour au réglage d'usine : le pilote choisit tout
            Color(deviceName, CmdSet, d =>
            {
                d.WriteByte(OffsetFormat, FormatDefault);
                d.WriteByte(OffsetColorimetry, ColorimetryDefault);
                d.WriteByte(OffsetRange, RangeAuto);
                d.WriteUInt32(OffsetBpc, 0);   // NV_BPC_DEFAULT
                d.WriteUInt32(OffsetPolicy, PolicyBestQuality);
                d.WriteUInt32(OffsetDepth, 0); // NV_DESKTOP_COLOR_DEPTH_DEFAULT : garder la profondeur actuelle
            }).Dispose();
            return;
        }

        // Réglage manuel : on garde format, colorimétrie et profondeur actuels, seule la plage change
        using var current = Color(deviceName, CmdGet, prepare: null);
        var format = current.ReadByte(OffsetFormat);
        var colorimetry = current.ReadByte(OffsetColorimetry);
        var bpc = current.ReadUInt32(OffsetBpc);
        var depth = current.ReadUInt32(OffsetDepth);
        var wanted = range switch { DynamicRange.Full => RangeVesa, DynamicRange.Limited => RangeCea, _ => RangeAuto };

        void Prepare(NvApi.NativeBuffer d)
        {
            d.WriteByte(OffsetFormat, format);
            d.WriteByte(OffsetColorimetry, colorimetry);
            d.WriteByte(OffsetRange, wanted);
            d.WriteUInt32(OffsetBpc, bpc);
            d.WriteUInt32(OffsetPolicy, PolicyUser);
            d.WriteUInt32(OffsetDepth, depth);
        }

        // Le pilote confirme d'abord que la combinaison est possible : sinon, rien n'est modifié
        Color(deviceName, CmdIsSupported, Prepare).Dispose();
        Color(deviceName, CmdSet, Prepare).Dispose();
    }

    private static NvApi.NativeBuffer Color(string deviceName, byte command, Action<NvApi.NativeBuffer>? prepare)
    {
        var id = DisplayId(deviceName);
        var control = NvApi.Require<NvApi.DisplayStructFn>(NvApi.IdDispColorControl, "NvAPI_Disp_ColorControl");
        var data = new NvApi.NativeBuffer(ColorSize);
        try
        {
            data.WriteUInt32(0, ColorVersion);
            data.WriteUInt16(4, ColorSize);
            data.WriteByte(OffsetCmd, command);
            prepare?.Invoke(data);
            NvApi.Check(control(id, data.Pointer), "NvAPI_Disp_ColorControl");
            return data;
        }
        catch
        {
            data.Dispose();
            throw;
        }
    }

    private static uint DisplayId(string deviceName)
    {
        var get = NvApi.Require<NvApi.GetDisplayIdByDisplayNameFn>(NvApi.IdDispGetDisplayIdByDisplayName, "NvAPI_DISP_GetDisplayIdByDisplayName");
        NvApi.Check(get(deviceName, out var id), "NvAPI_DISP_GetDisplayIdByDisplayName");
        return id;
    }
}
