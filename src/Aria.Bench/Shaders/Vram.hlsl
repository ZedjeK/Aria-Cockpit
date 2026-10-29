// Contrôles de stabilité d'Aria-Bench : test de la mémoire vidéo (Fill, Verify) et calcul vérifié (Burn).
// Repris du « Test VRAM discriminant » écrit par la session Aria Pilote (26/10/2026), validé sur la tour :
// 0 écart aux réglages d'origine, 17 130 écarts avec un undervolt trop poussé.
// Compilé en cs_5_1 par outils/compiler-shaders-calcul.py (compilateur fourni avec Windows), intégré à l'exe.

#define RS "UAV(u0), UAV(u1), RootConstants(num32BitConstants=8, b0)"

RWByteAddressBuffer Data   : register(u0);  // bloc de mémoire testé, ou résultats du calcul
RWByteAddressBuffer Report : register(u1);  // compteurs d'erreurs et premières adresses fautives

cbuffer Constants : register(b0)
{
    uint BaseChunk;   // numéro global du premier groupe de 16 octets du bloc
    uint Chunks;      // groupes de 16 octets dans le bloc (puissance de 2)
    uint Seed;        // graine de la passe : les valeurs changent à chaque passe
    uint Mode;        // bit 0 : ordre mélangé ; calcul : 0 = référence, 1 = comparaison
    uint Stride;      // nombre total de fils lancés (boucle « grid-stride »)
    uint Mul;         // multiplicateur impair : permutation de l'ordre d'accès
    uint Iterations;  // calcul : longueur des chaînes
    uint Pad;
};

// Valeur attendue d'un mot : elle dépend de son adresse ET de la passe.
// Une donnée écrite ou lue au mauvais endroit ne peut donc pas passer inaperçue.
uint Hash(uint a, uint s)
{
    uint h = a * 0x9E3779B1u ^ s;
    h ^= h >> 16; h *= 0x85EBCA6Bu;
    h ^= h >> 13; h *= 0xC2B2AE35u;
    h ^= h >> 16;
    return h;
}

uint4 Expected(uint chunk)
{
    uint w = (BaseChunk + chunk) * 4u;
    return uint4(Hash(w, Seed), Hash(w + 1u, Seed), Hash(w + 2u, Seed), Hash(w + 3u, Seed));
}

// Ordre linéaire (bande passante maximale) ou mélangé (sauts de ligne mémoire, comme l'inférence)
uint Index(uint k) { return (Mode & 1u) != 0u ? (k * Mul) & (Chunks - 1u) : k; }

[RootSignature(RS)]
[numthreads(256, 1, 1)]
void Fill(uint3 id : SV_DispatchThreadID)
{
    for (uint k = id.x; k < Chunks; k += Stride)
    {
        uint c = Index(k);
        Data.Store4(c * 16u, Expected(c));
    }
}

[RootSignature(RS)]
[numthreads(256, 1, 1)]
void Verify(uint3 id : SV_DispatchThreadID)
{
    for (uint k = id.x; k < Chunks; k += Stride)
    {
        uint c = Index(k);
        uint4 got = Data.Load4(c * 16u);
        uint4 want = Expected(c);
        uint4 diff = got ^ want;
        if (any(diff != 0u))
        {
            uint wrong = (diff.x != 0u ? 1u : 0u) + (diff.y != 0u ? 1u : 0u) + (diff.z != 0u ? 1u : 0u) + (diff.w != 0u ? 1u : 0u);
            uint bits = countbits(diff.x) + countbits(diff.y) + countbits(diff.z) + countbits(diff.w);
            uint slot, unused;
            Report.InterlockedAdd(0, wrong, unused);
            Report.InterlockedAdd(4, bits, unused);
            Report.InterlockedAdd(8, 1u, slot);  // une case par groupe de 16 octets fautif
            if (slot < 8u)
            {
                uint lane = diff.x != 0u ? 0u : (diff.y != 0u ? 1u : (diff.z != 0u ? 2u : 3u));
                Report.Store3(16u + slot * 12u, uint3((BaseChunk + c) * 4u + lane, want[lane], got[lane]));
            }
        }
    }
}

// Charge de calcul déterministe : chaînes de multiplications-additions en virgule flottante, entiers,
// échanges en mémoire partagée. Mêmes entrées, même carte : même résultat au bit près, sinon le cœur a fauté.
groupshared float4 Tile[256];

[RootSignature(RS)]
[numthreads(256, 1, 1)]
void Burn(uint3 id : SV_DispatchThreadID, uint3 lt : SV_GroupThreadID)
{
    float4 x = float4(1.0f, 1.25f, 1.5f, 1.75f) + (float)(id.x & 1023u) * 1e-4f;
    float4 y = x * 0.5f + 0.125f;
    float4 z = y * 0.75f;
    float4 w = x * 0.25f + 0.5f;
    uint u = id.x * 747796405u + 2891336453u;
    for (uint it = 0u; it < Iterations; it++)
    {
        x = mad(x, 0.99991f, y * 9e-5f);
        y = mad(y, 0.99987f, z * 1.3e-4f);
        z = mad(z, 0.99983f, w * 1.7e-4f);
        w = mad(w, 0.99979f, x * 2.1e-4f);
        u = u * 1664525u + 1013904223u;
        u ^= u >> 15;
        if ((it & 127u) == 0u)
        {
            Tile[lt.x] = x + y;
            GroupMemoryBarrierWithGroupSync();
            x = mad(Tile[(lt.x + 1u) & 255u], 0.5f, x * 0.5f);
            GroupMemoryBarrierWithGroupSync();
        }
    }
    uint r = asuint(x.x) ^ asuint(y.y) ^ asuint(z.z) ^ asuint(w.w) ^ asuint(x.w + y.z) ^ u;
    if (Mode == 0u)
    {
        Data.Store(id.x * 4u, r);
    }
    else
    {
        uint want = Data.Load(id.x * 4u);
        if (want != r)
        {
            uint n;
            Report.InterlockedAdd(0, 1u, n);
            if (n < 8u) Report.Store3(16u + n * 12u, uint3(id.x, want, r));  // quels fils se trompent
        }
    }
}
