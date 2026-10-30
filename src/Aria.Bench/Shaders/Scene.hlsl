// Scène du banc d'essai : terrain procédural, ville de tours aux fenêtres éclairées, ciel de fin de journée,
// soleil avec ombres portées, lumières néon. Tout est généré par les shaders (rien à télécharger).
// La géométrie ne dépend pas du préréglage, sauf sa densité : le même paysage en Faible et en Ultra.

#define SceneRS "RootFlags(ALLOW_INPUT_ASSEMBLER_INPUT_LAYOUT), CBV(b0), RootConstants(num32BitConstants=4, b1), " \
    "DescriptorTable(SRV(t0)), SRV(t1), " \
    "StaticSampler(s0, filter=FILTER_COMPARISON_MIN_MAG_LINEAR_MIP_POINT, comparisonFunc=COMPARISON_LESS_EQUAL, " \
    "addressU=TEXTURE_ADDRESS_BORDER, addressV=TEXTURE_ADDRESS_BORDER, addressW=TEXTURE_ADDRESS_BORDER, " \
    "borderColor=STATIC_BORDER_COLOR_OPAQUE_WHITE)"

#define MAX_LIGHTS 128
#define PI 3.14159265

cbuffer Frame : register(b0)
{
    row_major float4x4 ViewProj;
    row_major float4x4 InvViewProj;
    row_major float4x4 LightViewProj;
    float3 Eye;           float Time;
    float3 SunDir;        float TerrainSize;     // SunDir : direction vers le soleil
    float3 SunColor;      float TerrainHeight;
    float3 LightsCenter;  float FogDensity;
    uint LightCount;      uint Octaves;    uint PcfRadius;   uint TerrainRes;
    uint ObjectGrid;      float ShadowTexel;  uint AoRays;      uint Reflections;   // ray tracing (Ultra RT)
    float4 Lights[MAX_LIGHTS * 2];               // position + rayon, couleur + intensité
};

cbuffer Pass : register(b1)
{
    uint ShadowPass;
    uint3 PassPad;
};

Texture2D<float> ShadowMap : register(t0);
SamplerComparisonState ShadowSampler : register(s0);

// ---------------------------------------------------------------- Bruit

// Hachage entier (PCG) : aucune répétition visible, contrairement aux hachages à base de frac()
uint Pcg(uint v)
{
    uint state = v * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return (word >> 22u) ^ word;
}

// Valeur aléatoire dans [0, 1] pour une case entière (et une graine)
float Hash(float2 cell, uint seed)
{
    int2 i = (int2)floor(cell);
    return Pcg(asuint(i.x) ^ Pcg(asuint(i.y) ^ Pcg(seed))) * (1.0 / 4294967295.0);
}

float ValueNoise(float2 p)
{
    float2 i = floor(p);
    float2 f = frac(p);
    float2 u = f * f * (3.0 - 2.0 * f);
    float a = Hash(i, 0u);
    float b = Hash(i + float2(1, 0), 0u);
    float c = Hash(i + float2(0, 1), 0u);
    float d = Hash(i + float2(1, 1), 0u);
    return lerp(lerp(a, b, u.x), lerp(c, d, u.x), u.y);
}

float Fbm(float2 p, uint octaves)
{
    float sum = 0.0, amplitude = 0.5;
    const float2x2 rotate = float2x2(1.6, 1.2, -1.2, 1.6);
    for (uint i = 0; i < octaves; i++)
    {
        sum += amplitude * ValueNoise(p);
        p = mul(p, rotate);
        amplitude *= 0.5;
    }
    return sum;
}

// Relief : une vallée plate au centre (la ville), des montagnes autour. Toujours 5 octaves : même relief à tous les préréglages.
float TerrainHeightAt(float2 xz)
{
    float2 p = xz / 140.0;
    float h = Fbm(p, 5);
    float ridge = 1.0 - abs(ValueNoise(p * 0.5) * 2.0 - 1.0);
    h = h * 0.7 + ridge * ridge * 0.3;
    float d = length(xz) / (TerrainSize * 0.5);
    return TerrainHeight * h * smoothstep(0.18, 0.65, d) + 3.0 * h;
}

bool IsCity(float2 xz)
{
    return length(xz) < TerrainSize * 0.16;
}

// ---------------------------------------------------------------- Éclairage

float3 SkyColor(float3 dir)
{
    float up = saturate(dir.y);
    float3 horizon = float3(0.40, 0.33, 0.30);
    float3 zenith = float3(0.05, 0.10, 0.26);
    float3 color = lerp(horizon, zenith, pow(up, 0.6));
    float sun = saturate(dot(dir, SunDir));
    color += SunColor * (pow(sun, 900.0) * 30.0 + pow(sun, 12.0) * 0.25);
    return color;
}

float3 Brdf(float3 n, float3 v, float3 l, float3 albedo, float roughness, float metalness)
{
    float nl = saturate(dot(n, l));
    if (nl <= 0.0) return 0.0;
    float3 h = normalize(v + l);
    float nv = saturate(dot(n, v)) + 1e-4;
    float nh = saturate(dot(n, h));
    float vh = saturate(dot(v, h));
    float a = roughness * roughness;
    float a2 = a * a;
    float dd = nh * nh * (a2 - 1.0) + 1.0;
    float D = a2 / (PI * dd * dd);
    float k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
    float G = (nv / (nv * (1.0 - k) + k)) * (nl / (nl * (1.0 - k) + k));
    float3 f0 = lerp(0.04, albedo, metalness);
    float3 F = f0 + (1.0 - f0) * pow(1.0 - vh, 5.0);
    float3 specular = D * G * F / (4.0 * nv * nl + 1e-4);
    float3 diffuse = (1.0 - F) * (1.0 - metalness) * albedo / PI;
    return (diffuse + specular) * nl;
}

float ShadowFactor(float3 world)
{
    float4 clip = mul(float4(world, 1.0), LightViewProj);
    float3 p = clip.xyz / clip.w;
    float2 uv = p.xy * float2(0.5, -0.5) + 0.5;
    float depth = p.z - 0.0002;
    int r = (int)PcfRadius;
    float sum = 0.0, count = 0.0;
    for (int y = -r; y <= r; y++)
        for (int x = -r; x <= r; x++)
        {
            sum += ShadowMap.SampleCmpLevelZero(ShadowSampler, uv + float2(x, y) * ShadowTexel, depth);
            count += 1.0;
        }
    return sum / count;
}

float3 Shade(float3 world, float3 n, float3 albedo, float roughness, float metalness, float3 emissive)
{
    float3 toEye = Eye - world;
    float distance = length(toEye);
    float3 v = toEye / distance;

    float3 color = Brdf(n, v, SunDir, albedo, roughness, metalness) * SunColor * ShadowFactor(world);
    color += albedo * lerp(float3(0.10, 0.08, 0.07), float3(0.22, 0.28, 0.40), n.y * 0.5 + 0.5) * 0.5;

    for (uint i = 0; i < LightCount; i++)
    {
        float4 positionRadius = Lights[i * 2];
        float4 colorIntensity = Lights[i * 2 + 1];
        float3 l = positionRadius.xyz - world;
        float d2 = dot(l, l);
        float r2 = positionRadius.w * positionRadius.w;
        if (d2 < r2)
        {
            float falloff = saturate(1.0 - d2 / r2);
            l *= rsqrt(d2);
            color += Brdf(n, v, l, albedo, roughness, metalness) * colorIntensity.rgb * colorIntensity.w
                     * falloff * falloff / (1.0 + d2 * 0.02);
        }
    }

    color += emissive;
    float fog = 1.0 - exp(-distance * FogDensity);
    return lerp(color, SkyColor(-v), fog);
}

struct SceneTargets
{
    float4 Color : SV_Target0;
    float4 NormalDepth : SV_Target1;   // normale (monde) + distance à la caméra ; 0 pour le ciel
};

float4 ToClip(float3 world)
{
    return mul(float4(world, 1.0), ShadowPass != 0 ? LightViewProj : ViewProj);
}

SceneTargets Targets(float3 world, float3 n, float3 color)
{
    SceneTargets o;
    o.Color = float4(color, 1.0);
    o.NormalDepth = float4(n, length(Eye - world));
    return o;
}

// ---------------------------------------------------------------- Ciel

struct SkyOut
{
    float4 Position : SV_Position;
    float2 Ndc : NDC;
};

[RootSignature(SceneRS)]
SkyOut VSSky(uint id : SV_VertexID)
{
    SkyOut o;
    float2 uv = float2((id << 1) & 2, id & 2);
    o.Ndc = uv * float2(2.0, -2.0) + float2(-1.0, 1.0);
    o.Position = float4(o.Ndc, 1.0, 1.0);
    return o;
}

SceneTargets PSSky(SkyOut input)
{
    float4 far = mul(float4(input.Ndc, 1.0, 1.0), InvViewProj);
    float3 dir = normalize(far.xyz / far.w - Eye);
    SceneTargets o;
    o.Color = float4(SkyColor(dir), 1.0);
    o.NormalDepth = 0.0;
    return o;
}

// ---------------------------------------------------------------- Terrain

struct TerrainOut
{
    float4 Position : SV_Position;
    float3 World : WORLDPOS;
    float3 Normal : NORMAL;
};

// Position au sol du sommet n° id de la grille (TerrainRes + 1)² : la même pour le rendu et pour le ray tracing
float2 TerrainGridXZ(uint id)
{
    uint row = TerrainRes + 1;
    float2 grid = float2(id % row, id / row) / (float)TerrainRes;
    return (grid - 0.5) * TerrainSize;
}

[RootSignature(SceneRS)]
TerrainOut VSTerrain(uint id : SV_VertexID)
{
    float2 xz = TerrainGridXZ(id);
    float h = TerrainHeightAt(xz);
    float e = TerrainSize / (float)TerrainRes;
    float hx = TerrainHeightAt(xz + float2(e, 0.0));
    float hz = TerrainHeightAt(xz + float2(0.0, e));

    TerrainOut o;
    o.World = float3(xz.x, h, xz.y);
    o.Normal = normalize(float3(h - hx, e, h - hz));
    o.Position = ToClip(o.World);
    return o;
}

// Matière du sol : herbe, roche, neige, bitume en ville ; le détail suit le préréglage (charge de calcul par pixel)
void TerrainMaterial(TerrainOut input, out float3 n, out float3 albedo, out float roughness)
{
    n = normalize(input.Normal);
    float2 p = input.World.xz * 0.35;

    float detail = Fbm(p, Octaves);
    float dx = Fbm(p + float2(0.05, 0.0), Octaves) - detail;
    float dz = Fbm(p + float2(0.0, 0.05), Octaves) - detail;
    n = normalize(n + float3(-dx, 0.0, -dz) * 6.0);

    float slope = 1.0 - n.y;
    float heightRatio = input.World.y / TerrainHeight;
    float3 grass = lerp(float3(0.10, 0.20, 0.06), float3(0.20, 0.26, 0.10), detail);
    float3 rock = lerp(float3(0.22, 0.20, 0.19), float3(0.36, 0.33, 0.30), detail);
    float3 snow = float3(0.85, 0.87, 0.90);
    albedo = lerp(grass, rock, smoothstep(0.25, 0.45, slope));
    albedo = lerp(albedo, snow, smoothstep(0.62, 0.75, heightRatio + detail * 0.1) * (1.0 - smoothstep(0.5, 0.7, slope)));
    if (IsCity(input.World.xz)) albedo = lerp(float3(0.05, 0.05, 0.06), float3(0.12, 0.12, 0.13), detail);
    roughness = lerp(0.75, 0.95, detail);
}

SceneTargets PSTerrain(TerrainOut input)
{
    float3 n, albedo;
    float roughness;
    TerrainMaterial(input, n, albedo, roughness);
    return Targets(input.World, n, Shade(input.World, n, albedo, roughness, 0.0, 0.0));
}

// ---------------------------------------------------------------- Tours (ville) et rochers

struct ObjectIn
{
    float3 Position : POSITION;
    float3 Normal : NORMAL;
};

struct ObjectOut
{
    float4 Position : SV_Position;
    float3 World : WORLDPOS;
    float3 Normal : NORMAL;
    float3 Facade : FACADE;      // x : position le long de la façade, y : hauteur (en mètres), z : 1 sur un mur, 0 sur le toit
    nointerpolation float2 Info : INFO;   // x : 1 pour une tour, 0 pour un rocher ; y : graine (identique sur tout l'objet)
};

float3x3 RotationY(float a)
{
    float s = sin(a), c = cos(a);
    return float3x3(c, 0, -s, 0, 1, 0, s, 0, c);
}

// Placement de l'objet n° id : position du cube unité = mul(sommet * Scale, Rotation) + Offset (rendu et ray tracing)
struct Placement
{
    float3 Scale;
    float3x3 Rotation;
    float3 Offset;
    bool City;
    float Seed;
};

Placement PlaceObject(uint id)
{
    float grid = (float)ObjectGrid;
    float2 cell = float2(id % ObjectGrid, id / ObjectGrid);
    float spacing = TerrainSize * 0.8 / grid;
    float2 jitter = float2(Hash(cell, 1u), Hash(cell, 2u)) - 0.5;
    float2 xz = (cell - grid * 0.5 + 0.5 + jitter * 0.5) * spacing;
    float seed = Hash(cell, 3u);
    float ground = TerrainHeightAt(xz);
    bool city = IsCity(xz);

    // Tours dans la vallée, plus hautes vers le centre ; petits rochers ailleurs, absents des pentes enneigées
    float centre = 1.0 - saturate(length(xz) / (TerrainSize * 0.16));
    float height = city ? lerp(6.0, 20.0 + 70.0 * centre * centre, seed * seed) : lerp(0.6, 2.5, seed);
    float width = city ? min(clamp(spacing * 0.6, 3.0, 9.0), spacing * 0.8) : spacing * 0.25;
    if (!city && ground > TerrainHeight * 0.6) height = 0.0;

    Placement p;
    p.Scale = float3(width, height, width);
    p.Rotation = RotationY(city ? floor(seed * 4.0) * (PI * 0.5) + 0.3 : seed * 6.2831853);
    p.Offset = float3(xz.x, ground + height * 0.5 - 0.5, xz.y);
    p.City = city;
    p.Seed = seed;
    return p;
}

[RootSignature(SceneRS)]
ObjectOut VSObjects(ObjectIn input, uint id : SV_InstanceID)
{
    Placement p = PlaceObject(id);
    float3 local = input.Position * p.Scale;

    ObjectOut o;
    o.World = mul(local, p.Rotation) + p.Offset;
    o.Normal = mul(input.Normal, p.Rotation);
    // Axe de la façade choisi avant la rotation (la normale locale dit sur quelle face on est)
    o.Facade = float3(abs(input.Normal.x) > 0.5 ? local.z : local.x, local.y + p.Scale.y * 0.5, abs(input.Normal.y) < 0.5 ? 1.0 : 0.0);
    o.Info = float2(p.City ? 1.0 : 0.0, p.Seed);
    o.Position = ToClip(o.World);
    return o;
}

// Matière des tours (façades, fenêtres allumées) et des rochers
void ObjectMaterial(ObjectOut input, out float3 n, out float3 albedo, out float roughness, out float metalness, out float3 emissive)
{
    n = normalize(input.Normal);
    emissive = 0.0;

    if (input.Info.x > 0.5)
    {
        // Façade : un étage tous les 3,2 m, une fenêtre tous les 2,4 m ; certaines sont allumées
        float2 facade = input.Facade.xy / float2(2.4, 3.2);
        float2 cell = floor(facade);
        float2 f = frac(facade);
        // Anticrénelage : bords adoucis sur un pixel, puis valeur moyenne quand une fenêtre devient plus petite qu'un pixel
        float2 w = max(fwidth(facade), 1e-4);
        float2 edge = smoothstep(float2(0.18, 0.25) - w, float2(0.18, 0.25) + w, f)
                    * (1.0 - smoothstep(float2(0.82, 0.80) - w, float2(0.82, 0.80) + w, f));
        float detail = 1.0 - saturate((max(w.x, w.y) - 0.3) / 0.4);
        float wall = input.Facade.z > 0.5 ? 1.0 : 0.0;
        float window = wall * lerp(0.35, edge.x * edge.y, detail);

        float lit = Hash(cell, (uint)(input.Info.y * 65536.0));
        float3 warm = float3(1.0, 0.72, 0.42);
        float3 neon = lit > 0.97 ? float3(0.2, 0.9, 1.0) : float3(1.0, 0.3, 0.75);
        float3 glow = lit > 0.62 ? (lit > 0.94 ? neon : warm) * (1.5 + 2.5 * frac(lit * 13.0)) : 0.0;
        float3 averageGlow = warm * 0.9;     // environ 38 % de fenêtres allumées en moyenne

        albedo = lerp(lerp(float3(0.10, 0.10, 0.11), float3(0.20, 0.19, 0.18), input.Info.y), float3(0.03, 0.04, 0.05), window);
        roughness = lerp(0.7, 0.08, window);
        metalness = lerp(0.0, 0.6, window);
        emissive = window * lerp(averageGlow, glow, detail);
    }
    else
    {
        albedo = float3(0.24, 0.22, 0.20) * (0.7 + 0.6 * input.Info.y);
        roughness = 0.9;
        metalness = 0.0;
    }
}

SceneTargets PSObjects(ObjectOut input)
{
    float3 n, albedo, emissive;
    float roughness, metalness;
    ObjectMaterial(input, n, albedo, roughness, metalness, emissive);
    return Targets(input.World, n, Shade(input.World, n, albedo, roughness, metalness, emissive));
}

// ---------------------------------------------------------------- Ray tracing (préréglage Ultra RT)
//
// Construction (CSBuildRt, une fois par configuration) : sommets du terrain et une instance par tour ou rocher, calculés
// avec les mêmes fonctions que le rendu, puis BLAS et TLAS construites par Aria-Bench. Rendu (PSTerrainRT, PSObjectsRT,
// modèle 6.5, DXR 1.1 « en ligne ») : ombre du soleil, occlusion ambiante et reflets tracés par des RayQuery, qui font
// travailler les cœurs RT de la carte en plus de ses unités de calcul.

#define BuildRS "CBV(b0), RootConstants(num32BitConstants=4, b2), UAV(u0), UAV(u1)"

cbuffer Build : register(b2)
{
    uint2 TerrainBlas;   // adresses GPU des BLAS (64 bits en deux mots)
    uint2 CubeBlas;
};

// Description d'instance DXR (D3D12_RAYTRACING_INSTANCE_DESC, 64 octets)
struct RtInstance
{
    float4 Row0;
    float4 Row1;
    float4 Row2;
    uint IdAndMask;       // InstanceID sur 24 bits, masque sur 8
    uint HitAndFlags;     // décalage de hit group sur 24 bits, options sur 8
    uint2 Blas;
};

RWStructuredBuffer<RtInstance> Instances : register(u0);
RWByteAddressBuffer TerrainVertices : register(u1);

#define TERRAIN_INSTANCE 0xFFFFFFu
#define CITY_BIT 0x800000u
// D3D12_RAYTRACING_INSTANCE_FLAG_TRIANGLE_CULL_DISABLE | FORCE_OPAQUE
#define INSTANCE_FLAGS (5u << 24)

[RootSignature(BuildRS)]
[numthreads(64, 1, 1)]
void CSBuildRt(uint3 thread : SV_DispatchThreadID)
{
    uint id = thread.x;
    uint objects = ObjectGrid * ObjectGrid;
    if (id < objects)
    {
        Placement p = PlaceObject(id);
        RtInstance inst;
        // Transformation 3 × 4 (colonnes : axes du cube mis à l'échelle et tournés, puis position)
        inst.Row0 = float4(p.Scale.x * p.Rotation[0][0], p.Scale.y * p.Rotation[1][0], p.Scale.z * p.Rotation[2][0], p.Offset.x);
        inst.Row1 = float4(p.Scale.x * p.Rotation[0][1], p.Scale.y * p.Rotation[1][1], p.Scale.z * p.Rotation[2][1], p.Offset.y);
        inst.Row2 = float4(p.Scale.x * p.Rotation[0][2], p.Scale.y * p.Rotation[1][2], p.Scale.z * p.Rotation[2][2], p.Offset.z);
        // Rocher absent (pente enneigée, hauteur nulle) : masque 0, aucun rayon ne le touche
        inst.IdAndMask = (id | (p.City ? CITY_BIT : 0u)) | ((p.Scale.y > 0.0 ? 0xFFu : 0u) << 24);
        inst.HitAndFlags = INSTANCE_FLAGS;
        inst.Blas = CubeBlas;
        Instances[id] = inst;
    }
    else if (id == objects)
    {
        RtInstance terrain;
        terrain.Row0 = float4(1, 0, 0, 0);
        terrain.Row1 = float4(0, 1, 0, 0);
        terrain.Row2 = float4(0, 0, 1, 0);
        terrain.IdAndMask = TERRAIN_INSTANCE | (0xFFu << 24);
        terrain.HitAndFlags = INSTANCE_FLAGS;
        terrain.Blas = TerrainBlas;
        Instances[id] = terrain;
    }

    uint row = TerrainRes + 1;
    if (id < row * row)
    {
        float2 xz = TerrainGridXZ(id);
        TerrainVertices.Store3(id * 12, asuint(float3(xz.x, TerrainHeightAt(xz), xz.y)));
    }
}

#if defined(__SHADER_TARGET_MAJOR) && (__SHADER_TARGET_MAJOR > 6 || (__SHADER_TARGET_MAJOR == 6 && __SHADER_TARGET_MINOR >= 5))

RaytracingAccelerationStructure SceneBvh : register(t1);

// Un obstacle entre origin et origin + dir * tMax ? (le premier touché suffit)
bool Occluded(float3 origin, float3 dir, float tMax)
{
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE> q;
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = dir;
    ray.TMin = 0.0;
    ray.TMax = tMax;
    q.TraceRayInline(SceneBvh, RAY_FLAG_NONE, 0xFF, ray);
    q.Proceed();
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT;
}

// Direction dans l'hémisphère de la normale (répartition en cosinus), fixée par le pixel et le n° du rayon :
// la même à chaque image, pour que l'image de contrôle reste comparable
float3 HemisphereDir(float3 n, float2 pixel, uint i)
{
    uint seed = Pcg((uint)pixel.x ^ Pcg((uint)pixel.y ^ Pcg(i + 17u)));
    float u1 = (seed & 0xFFFFu) / 65535.0;
    float u2 = (seed >> 16) / 65535.0;
    float r = sqrt(u1);
    float phi = 6.2831853 * u2;
    float3 t = normalize(abs(n.y) < 0.99 ? cross(n, float3(0, 1, 0)) : cross(n, float3(1, 0, 0)));
    float3 b = cross(n, t);
    return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(saturate(1.0 - u1)));
}

// Ce qu'on voit dans un reflet : point touché éclairé simplement (soleil sans ombre, ciel, fenêtres en moyenne)
float3 ReflectedColor(float3 origin, float3 dir)
{
    RayQuery<RAY_FLAG_FORCE_OPAQUE> q;
    RayDesc ray;
    ray.Origin = origin;
    ray.Direction = dir;
    ray.TMin = 0.0;
    ray.TMax = 1500.0;
    q.TraceRayInline(SceneBvh, RAY_FLAG_NONE, 0xFF, ray);
    while (q.Proceed()) { }
    if (q.CommittedStatus() != COMMITTED_TRIANGLE_HIT) return SkyColor(dir);

    float t = q.CommittedRayT();
    float3 p = origin + dir * t;
    float3 n, albedo, emissive = 0.0;
    uint id = q.CommittedInstanceID();
    if (id == TERRAIN_INSTANCE)
    {
        float h = TerrainHeightAt(p.xz);
        n = normalize(float3(h - TerrainHeightAt(p.xz + float2(1.0, 0.0)), 1.0, h - TerrainHeightAt(p.xz + float2(0.0, 1.0))));
        albedo = IsCity(p.xz) ? float3(0.08, 0.08, 0.09) : float3(0.15, 0.22, 0.08);
    }
    else
    {
        // Normale de la face touchée : 2 triangles par face, dans l'ordre de CubeMesh (+X, -X, +Y, -Y, +Z, -Z)
        static const float3 Faces[6] = { float3(1, 0, 0), float3(-1, 0, 0), float3(0, 1, 0), float3(0, -1, 0), float3(0, 0, 1), float3(0, 0, -1) };
        float3 local = Faces[min(q.CommittedPrimitiveIndex() / 2, 5u)];
        n = normalize(mul((float3x3)q.CommittedObjectToWorld3x4(), local));
        bool tower = (id & CITY_BIT) != 0;
        bool wall = abs(local.y) < 0.5;
        albedo = tower ? float3(0.12, 0.12, 0.13) : float3(0.24, 0.22, 0.20);
        if (tower && wall) emissive = float3(1.0, 0.72, 0.42) * 0.9 * 0.35;
    }
    float3 ambient = lerp(float3(0.10, 0.08, 0.07), float3(0.22, 0.28, 0.40), n.y * 0.5 + 0.5) * 0.5;
    float3 color = albedo * (SunColor * saturate(dot(n, SunDir)) / PI + ambient) + emissive;
    float fog = 1.0 - exp(-t * FogDensity);
    return lerp(color, SkyColor(dir), fog);
}

// Shade, avec le ray tracing : ombre du soleil tracée (à la place de la carte d'ombre), occlusion ambiante, reflet
float3 ShadeRt(float3 world, float3 n, float3 albedo, float roughness, float metalness, float3 emissive, float2 pixel)
{
    float3 toEye = Eye - world;
    float distance = length(toEye);
    float3 v = toEye / distance;
    float3 origin = world + n * 0.05;

    float sun = dot(n, SunDir) > 0.0 && !Occluded(origin, SunDir, 2000.0) ? 1.0 : 0.0;
    float3 color = Brdf(n, v, SunDir, albedo, roughness, metalness) * SunColor * sun;

    float open = 1.0;
    if (AoRays > 0)
    {
        uint hits = 0;
        for (uint r = 0; r < AoRays; r++)
            if (Occluded(origin, HemisphereDir(n, pixel, r), 6.0)) hits++;
        open = 1.0 - 0.8 * hits / (float)AoRays;
    }
    color += albedo * lerp(float3(0.10, 0.08, 0.07), float3(0.22, 0.28, 0.40), n.y * 0.5 + 0.5) * 0.5 * open;

    for (uint i = 0; i < LightCount; i++)
    {
        float4 positionRadius = Lights[i * 2];
        float4 colorIntensity = Lights[i * 2 + 1];
        float3 l = positionRadius.xyz - world;
        float d2 = dot(l, l);
        float r2 = positionRadius.w * positionRadius.w;
        if (d2 < r2)
        {
            float falloff = saturate(1.0 - d2 / r2);
            l *= rsqrt(d2);
            color += Brdf(n, v, l, albedo, roughness, metalness) * colorIntensity.rgb * colorIntensity.w
                     * falloff * falloff / (1.0 + d2 * 0.02);
        }
    }

    // Reflet : un rayon par pixel, pondéré par Fresnel et la brillance (net sur les vitres, presque rien sur la roche)
    if (Reflections != 0)
    {
        float3 f0 = lerp(0.04, albedo, metalness);
        float3 fresnel = f0 + (1.0 - f0) * pow(1.0 - saturate(dot(n, v)), 5.0);
        color += ReflectedColor(origin, reflect(-v, n)) * fresnel * (1.0 - roughness);
    }

    color += emissive;
    float fog = 1.0 - exp(-distance * FogDensity);
    return lerp(color, SkyColor(-v), fog);
}

SceneTargets PSTerrainRT(TerrainOut input)
{
    float3 n, albedo;
    float roughness;
    TerrainMaterial(input, n, albedo, roughness);
    return Targets(input.World, n, ShadeRt(input.World, n, albedo, roughness, 0.0, 0.0, input.Position.xy));
}

SceneTargets PSObjectsRT(ObjectOut input)
{
    float3 n, albedo, emissive;
    float roughness, metalness;
    ObjectMaterial(input, n, albedo, roughness, metalness, emissive);
    return Targets(input.World, n, ShadeRt(input.World, n, albedo, roughness, metalness, emissive, input.Position.xy));
}

#endif
