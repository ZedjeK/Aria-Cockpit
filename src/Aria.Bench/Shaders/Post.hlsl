// Passes plein écran : occlusion ambiante (SSAO), halo lumineux (bloom) et image finale (tonemapping + barre de progression).

#define PostRS "CBV(b1), DescriptorTable(SRV(t0, numDescriptors=4)), RootConstants(num32BitConstants=8, b0), " \
    "StaticSampler(s0, filter=FILTER_MIN_MAG_MIP_LINEAR, addressU=TEXTURE_ADDRESS_CLAMP, addressV=TEXTURE_ADDRESS_CLAMP, addressW=TEXTURE_ADDRESS_CLAMP), " \
    "StaticSampler(s1, filter=FILTER_MIN_MAG_MIP_POINT, addressU=TEXTURE_ADDRESS_CLAMP, addressV=TEXTURE_ADDRESS_CLAMP, addressW=TEXTURE_ADDRESS_CLAMP)"

cbuffer Params : register(b0)
{
    float2 TexelSize;     // taille d'un texel de la source
    float Progress;
    uint Samples;
    float BloomStrength;
    float AoStrength;
    float Exposure;
    uint Mode;            // bloom : 1 = première passe (seuil) ; image finale : 1 = SSAO, 2 = bloom
};

// Début du tampon de la scène (même disposition que Scene.hlsl)
cbuffer Frame : register(b1)
{
    row_major float4x4 ViewProj;
    row_major float4x4 InvViewProj;
    row_major float4x4 LightViewProj;
    float3 Eye;
    float Time;
};

Texture2D<float4> Source0 : register(t0);
Texture2D<float4> Source1 : register(t1);
Texture2D<float4> Source2 : register(t2);
SamplerState LinearClamp : register(s0);
SamplerState PointClamp : register(s1);

struct FullOut
{
    float4 Position : SV_Position;
    float2 UV : TEXCOORD;
};

[RootSignature(PostRS)]
FullOut VSFull(uint id : SV_VertexID)
{
    FullOut o;
    o.UV = float2((id << 1) & 2, id & 2);
    o.Position = float4(o.UV * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
    return o;
}

float Hash12(float2 p)
{
    float3 p3 = frac(float3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return frac((p3.x + p3.y) * p3.z);
}

// ---------------------------------------------------------------- SSAO

float3 WorldFromUV(float2 uv, float distance)
{
    float2 ndc = uv * float2(2.0, -2.0) + float2(-1.0, 1.0);
    float4 far = mul(float4(ndc, 1.0, 1.0), InvViewProj);
    return Eye + normalize(far.xyz / far.w - Eye) * distance;
}

float PSSsao(FullOut input) : SV_Target
{
    float4 nd = Source0.SampleLevel(PointClamp, input.UV, 0);
    if (nd.w <= 0.0) return 1.0;
    float3 n = normalize(nd.xyz);
    float3 world = WorldFromUV(input.UV, nd.w);
    float radius = 2.5;
    float rotation = Hash12(input.Position.xy) * 6.2831853;
    float occlusion = 0.0;

    for (uint i = 0; i < Samples; i++)
    {
        // Directions réparties sur une sphère (spirale de Fibonacci), basculées dans l'hémisphère de la normale
        float t = (i + 0.5) / Samples;
        float phi = i * 2.3999632 + rotation;
        float z = 1.0 - 2.0 * t;
        float s = sqrt(1.0 - z * z);
        float3 dir = float3(s * cos(phi), z, s * sin(phi));
        dir = normalize(n + dir * 0.95);
        float3 p = world + dir * radius * lerp(0.2, 1.0, t * t);

        float4 clip = mul(float4(p, 1.0), ViewProj);
        float2 uv = clip.xy / clip.w * float2(0.5, -0.5) + 0.5;
        float scene = Source0.SampleLevel(PointClamp, uv, 0).w;
        float depth = length(p - Eye);
        if (scene > 0.0 && scene < depth - 0.05)
            occlusion += saturate(radius / abs(depth - scene));
    }
    return saturate(1.0 - occlusion / max(Samples, 1u) * AoStrength);
}

// ---------------------------------------------------------------- Bloom

// Réduction à 13 points (évite le scintillement), avec seuil à la première passe
float4 PSBloomDown(FullOut input) : SV_Target
{
    float2 t = TexelSize;
    float2 uv = input.UV;
    float3 a = Source0.SampleLevel(LinearClamp, uv + t * float2(-2, -2), 0).rgb;
    float3 b = Source0.SampleLevel(LinearClamp, uv + t * float2( 0, -2), 0).rgb;
    float3 c = Source0.SampleLevel(LinearClamp, uv + t * float2( 2, -2), 0).rgb;
    float3 d = Source0.SampleLevel(LinearClamp, uv + t * float2(-2,  0), 0).rgb;
    float3 e = Source0.SampleLevel(LinearClamp, uv, 0).rgb;
    float3 f = Source0.SampleLevel(LinearClamp, uv + t * float2( 2,  0), 0).rgb;
    float3 g = Source0.SampleLevel(LinearClamp, uv + t * float2(-2,  2), 0).rgb;
    float3 h = Source0.SampleLevel(LinearClamp, uv + t * float2( 0,  2), 0).rgb;
    float3 i = Source0.SampleLevel(LinearClamp, uv + t * float2( 2,  2), 0).rgb;
    float3 j = Source0.SampleLevel(LinearClamp, uv + t * float2(-1, -1), 0).rgb;
    float3 k = Source0.SampleLevel(LinearClamp, uv + t * float2( 1, -1), 0).rgb;
    float3 l = Source0.SampleLevel(LinearClamp, uv + t * float2(-1,  1), 0).rgb;
    float3 m = Source0.SampleLevel(LinearClamp, uv + t * float2( 1,  1), 0).rgb;
    float3 color = e * 0.125 + (a + c + g + i) * 0.03125 + (b + d + f + h) * 0.0625 + (j + k + l + m) * 0.125;
    if (Mode == 1)
    {
        float brightness = max(color.r, max(color.g, color.b));
        color *= max(brightness - 1.0, 0.0) / max(brightness, 1e-4);
    }
    return float4(min(color, 64.0), 1.0);
}

// Agrandissement (filtre en tente 3 × 3), ajouté au niveau supérieur
float4 PSBloomUp(FullOut input) : SV_Target
{
    float2 t = TexelSize;
    float2 uv = input.UV;
    float3 color = Source0.SampleLevel(LinearClamp, uv, 0).rgb * 4.0;
    color += (Source0.SampleLevel(LinearClamp, uv + float2(-t.x, 0), 0).rgb + Source0.SampleLevel(LinearClamp, uv + float2(t.x, 0), 0).rgb
            + Source0.SampleLevel(LinearClamp, uv + float2(0, -t.y), 0).rgb + Source0.SampleLevel(LinearClamp, uv + float2(0, t.y), 0).rgb) * 2.0;
    color += Source0.SampleLevel(LinearClamp, uv + float2(-t.x, -t.y), 0).rgb + Source0.SampleLevel(LinearClamp, uv + float2(t.x, -t.y), 0).rgb
           + Source0.SampleLevel(LinearClamp, uv + float2(-t.x, t.y), 0).rgb + Source0.SampleLevel(LinearClamp, uv + float2(t.x, t.y), 0).rgb;
    return float4(color / 16.0, 1.0);
}

// ---------------------------------------------------------------- Image finale

float3 Aces(float3 x)
{
    // Approximation de Narkowicz de la courbe ACES
    return saturate((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14));
}

float4 PSComposite(FullOut input) : SV_Target
{
    float3 color = Source0.SampleLevel(LinearClamp, input.UV, 0).rgb;
    if (Mode & 1) color *= Source1.SampleLevel(LinearClamp, input.UV, 0).r;
    if (Mode & 2) color += Source2.SampleLevel(LinearClamp, input.UV, 0).rgb * BloomStrength;
    color = Aces(color * Exposure);
    if (input.UV.y > 0.994)
        color = input.UV.x < Progress ? float3(1.0, 0.45, 0.10) : float3(0.08, 0.08, 0.10);
    return float4(color, 1.0);
}
