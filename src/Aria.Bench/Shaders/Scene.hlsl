// Scène de contrôle du lot 1 : une grille de colonnes éclairées qui ondulent et tournent.
// Tout est généré dans le vertex shader à partir du numéro d'instance (aucune donnée par objet).

#define SceneRS "RootFlags(ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT), RootConstants(num32BitConstants=24, b0)"

cbuffer Frame : register(b0)
{
    row_major float4x4 ViewProj;
    float3 Eye;
    float Time;
    float GridSize;
    float Spacing;
    float2 Pad;
};

struct VSIn
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
};

struct VSOut
{
    float4 Position : SV_Position;
    float3 World : WORLDPOS;
    float3 Normal : NORMAL;
    float3 Color : COLOR;
};

// Hachage entier → [0, 1] (même résultat sur toutes les cartes)
float Hash(uint n)
{
    n = (n << 13u) ^ n;
    n = n * (n * n * 15731u + 789221u) + 1376312589u;
    return float(n & 0x7fffffffu) / 2147483647.0;
}

float3x3 RotationY(float a)
{
    float s = sin(a), c = cos(a);
    return float3x3(c, 0, -s,
                    0, 1, 0,
                    s, 0, c);
}

[RootSignature(SceneRS)]
VSOut VSMain(VSIn input, uint id : SV_InstanceID)
{
    uint grid = (uint)GridSize;
    float2 cell = float2(id % grid, id / grid) - (grid - 1) * 0.5;
    float h = Hash(id);

    float wave = sin(cell.x * 0.35 + Time * 1.3) * cos(cell.y * 0.30 + Time * 0.9);
    float height = max(0.6 + h * 1.6 + wave * 0.8, 0.2);
    float3 scale = float3(0.8, height, 0.8);
    float3x3 rotation = RotationY(Time * (0.3 + h) + h * 6.2831853);

    float3 local = mul(input.Position * scale, rotation);
    float3 world = local + float3(cell.x * Spacing, 0.5 * height, cell.y * Spacing);

    VSOut o;
    o.World = world;
    o.Normal = mul(input.Normal / scale, rotation);
    o.Color = lerp(float3(0.10, 0.55, 0.95), float3(0.95, 0.35, 0.60), h);
    o.Position = mul(float4(world, 1.0), ViewProj);
    return o;
}

float4 PSMain(VSOut input) : SV_Target
{
    float3 n = normalize(input.Normal);
    float3 l = normalize(float3(0.4, 0.8, 0.3));
    float3 v = normalize(Eye - input.World);
    float3 halfway = normalize(l + v);

    float diffuse = saturate(dot(n, l));
    float specular = pow(saturate(dot(n, halfway)), 32.0);
    float3 color = input.Color * (0.12 + 0.88 * diffuse) + specular * 0.5;

    float distance = length(Eye - input.World);
    color = lerp(color, float3(0.02, 0.02, 0.05), saturate((distance - 40.0) / 120.0));
    return float4(color, 1.0);
}
