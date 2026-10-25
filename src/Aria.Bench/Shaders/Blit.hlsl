// Recopie du rendu interne (1080p, 1440p ou 4K) à la taille de l'écran, plus une barre de progression en bas.

#define BlitRS "DescriptorTable(SRV(t0)), RootConstants(num32BitConstants=4, b0), " \
               "StaticSampler(s0, filter=FILTER_MIN_MAG_MIP_LINEAR, addressU=TEXTURE_ADDRESS_CLAMP, addressV=TEXTURE_ADDRESS_CLAMP)"

Texture2D<float4> SceneTexture : register(t0);
SamplerState LinearClamp : register(s0);

cbuffer Params : register(b0)
{
    float Progress;
    float3 Pad;
};

struct VSOut
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD;
};

// Un seul triangle qui couvre tout l'écran (sans tampon de sommets)
[RootSignature(BlitRS)]
VSOut VSMain(uint id : SV_VertexID)
{
    VSOut o;
    o.UV = float2((id << 1) & 2, id & 2);
    o.Position = float4(o.UV * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

float4 PSMain(VSOut input) : SV_Target
{
    float3 color = saturate(SceneTexture.Sample(LinearClamp, input.UV).rgb);
    if (input.UV.y > 0.994)
        color = input.UV.x < Progress ? float3(1.0, 0.45, 0.10) : float3(0.08, 0.08, 0.10);
    return float4(color, 1.0);
}
