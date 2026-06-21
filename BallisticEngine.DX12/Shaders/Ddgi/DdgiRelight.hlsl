// DDGI — per-probe relight (Pass 1). One thread-GROUP per probe (RAYS threads). Each thread traces one ray
// over the full sphere (Fibonacci), shades the hit with first-bounce direct light (sun shadow-ray + punctual +
// emissive) × albedo, or samples the sky on a miss, and stores the ray's radiance+direction in groupshared.
// Then the group's threads split the probe's OctRes×OctRes octahedral texels: each texel integrates all rays
// cosine-weighted by its own direction (the published DDGI integration) and EMA-blends over the previous frame.
//
// View-independent: the radiance cache is world-space, so there is NO reprojection / motion / screen history —
// the entire ghosting/disocclusion class never arises. The hit-shading is the Lumen card-light kernel reduced
// to a per-hit form (no per-triangle cache, no clustering). Bindless geo/material reads use the RtInstance ABI.
//
// Bound: TLAS t0 (root SRV) | Irradiance u0 (root UAV) | PrevIrradiance t1 (root SRV) | RtInstance[] t2 /
//        GpuMaterials t3 / Lights t4 (root SRV) | sky irradiance cube t5 (table) | DdgiRelightConstants b0 |
//        bindless heap (ResourceDescriptorHeap[]) | s0 clamp.

#define RAYS 64           // threads per group == rays per probe (must match the C# dispatch group size)

RaytracingAccelerationStructure Scene : register(t0);
RWStructuredBuffer<float4> Irradiance  : register(u0);   // [probe*OctTexels + texel]  rgb=E, a=1
StructuredBuffer<float4>   PrevIrrad   : register(t1);   // previous frame (EMA source)

struct RtInstance { uint NormalIdx, UvIdx, IndexIdx, TriMatIdx; uint PositionIdx, TriCount, Pad0, Pad1; };
struct GpuMaterial {
    uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
    uint AoIdx, EmissiveIdx, Pad0, Pad1;
    float4 BaseColorFactor; float4 EmissiveFactor;
    float Metallic, Roughness, SpecularReflectance, NormalStrength;
    float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
    float Cutout, HasEmissive, Pad2, Pad3;
};
struct GpuLight { float4 PosRange; float4 Color; float4 DirCosOuter; float4 Extra; float4 RightAxisHalfW; };
StructuredBuffer<RtInstance>  RtInstances : register(t2);
StructuredBuffer<GpuMaterial> GpuMaterials: register(t3);
StructuredBuffer<GpuLight>    Lights      : register(t4);
TextureCube SkyIrradiance : register(t5);

cbuffer DdgiRelightConstants : register(b0) {
    float3 GridOrigin;   float RayCount;
    float3 ProbeSpacing; float SkyIntensity;
    uint   CountX, CountY, CountZ;  float UseSky;
    float3 SunDir;       float SunBias;       // TO the sun (normalized)
    float3 SunColor;     float LightCount;
    float  EmaAlpha;     float HistoryValid;  float Intensity;  float FrameJitter;  // FrameJitter<0 → fixed (deterministic)
};
SamplerState LinearClamp : register(s0);

static const int OctRes = 6;
static const int OctTexels = OctRes * OctRes;   // 36
static const float PI = 3.14159265359;

groupshared float3 gRad[RAYS];
groupshared float3 gDir[RAYS];

float3 Sanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}
float3 OctDecode(float2 f) {
    f = f * 2.0 - 1.0;
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}
float3 SphereDir(uint i, uint n, float jitter) {
    float gold = 2.39996322973;
    float z = 1.0 - (2.0 * float(i) + 1.0) / float(n);
    float r = sqrt(saturate(1.0 - z * z));
    float phi = float(i) * gold + jitter;
    return float3(r * cos(phi), r * sin(phi), z);
}
float Hash(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}
float Visibility(float3 origin, float3 N, float3 dir, float maxDist) {
    RayDesc ray; ray.Origin = origin + N * max(SunBias, 0.01); ray.Direction = dir; ray.TMin = 0.02; ray.TMax = maxDist;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray); q.Proceed();
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 0.0 : 1.0;
}

// First-bounce shading at an RT hit (radiance leaving the surface), given the known world hit point.
float3 ShadeHit(uint instId, uint prim, float2 bary, float3 Pw) {
    RtInstance geo = RtInstances[instId];
    Buffer<uint>             indices = ResourceDescriptorHeap[geo.IndexIdx];
    StructuredBuffer<float3> normals = ResourceDescriptorHeap[geo.NormalIdx];
    StructuredBuffer<float2> uvs     = ResourceDescriptorHeap[geo.UvIdx];
    StructuredBuffer<uint>   triMat  = ResourceDescriptorHeap[geo.TriMatIdx];

    uint i0 = indices[prim * 3 + 0], i1 = indices[prim * 3 + 1], i2 = indices[prim * 3 + 2];
    float w0 = 1.0 - bary.x - bary.y, w1 = bary.x, w2 = bary.y;
    float2 uv = uvs[i0] * w0 + uvs[i1] * w1 + uvs[i2] * w2;
    float3 Nw = normalize(normals[i0] * w0 + normals[i1] * w1 + normals[i2] * w2);

    GpuMaterial m = GpuMaterials[triMat[prim]];
    Texture2D diffuseMap = ResourceDescriptorHeap[m.DiffuseIdx];
    float3 albedo = min(diffuseMap.SampleLevel(LinearClamp, uv, 0).rgb * m.BaseColorFactor.rgb, 0.95.xxx);

    float3 emissive = 0.0.xxx;
    if (m.HasEmissive > 0.5) {
        Texture2D emissiveMap = ResourceDescriptorHeap[m.EmissiveIdx];
        emissive = emissiveMap.SampleLevel(LinearClamp, uv, 0).rgb * m.EmissiveFactor.rgb;
    }

    float3 sunDir = normalize(SunDir);
    float ndl = saturate(dot(Nw, sunDir));
    float3 sun = (ndl > 0.0) ? SunColor * ndl * Visibility(Pw, Nw, sunDir, 1e4) : 0.0.xxx;

    float3 punctual = 0.0.xxx;
    int nl = min((int)LightCount, 32);
    [loop] for (int i = 0; i < nl; i++) {
        GpuLight L = Lights[i];
        if (L.Color.w >= 1.5) continue;
        float3 toL = L.PosRange.xyz - Pw;
        float dist = length(toL);
        if (dist > L.PosRange.w || dist < 1e-4) continue;
        float3 Ld = toL / dist;
        float nd = saturate(dot(Nw, Ld));
        if (nd <= 0.0) continue;
        float t = saturate(1.0 - pow(dist / L.PosRange.w, 4.0));
        float3 rad = L.Color.rgb * (t * t / max(dist * dist, 1e-4));
        if (L.Color.w >= 0.5) {
            float cosA = dot(-Ld, normalize(L.DirCosOuter.xyz));
            float cone = saturate((cosA - L.DirCosOuter.w) / max(L.Extra.x - L.DirCosOuter.w, 1e-4));
            if (cone <= 0.0) continue;
            rad *= cone * cone;
        }
        punctual += rad * nd * Visibility(Pw, Nw, Ld, dist);
    }
    return albedo * (sun + punctual) + emissive;
}

[numthreads(RAYS, 1, 1)]
void CSMain(uint3 gid : SV_GroupID, uint gi : SV_GroupIndex) {
    uint probe = gid.x;
    uint probeCount = CountX * CountY * CountZ;
    if (probe >= probeCount) return;

    uint ix = probe % CountX;
    uint iy = (probe / CountX) % CountY;
    uint iz = probe / (CountX * CountY);
    float3 P = GridOrigin + float3(ix, iy, iz) * ProbeSpacing;

    float jitter = FrameJitter < 0.0 ? Hash(probe * 2654435761u) * 6.2831853
                                     : Hash((probe * 2654435761u) ^ (uint)FrameJitter) * 6.2831853;

    // Each thread traces ONE ray (gi in [0,RAYS)).
    float3 d = SphereDir(gi, RAYS, jitter);
    float3 rad;
    RayDesc rd; rd.Origin = P; rd.Direction = d; rd.TMin = 0.0; rd.TMax = 1e4;
    RayQuery<RAY_FLAG_FORCE_OPAQUE> q; q.TraceRayInline(Scene, 0, 0xFF, rd); q.Proceed();
    if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
        float3 hitW = P + d * q.CommittedRayT();
        rad = ShadeHit(q.CommittedInstanceID(), q.CommittedPrimitiveIndex(), q.CommittedTriangleBarycentrics(), hitW);
    } else {
        rad = (UseSky > 0.5) ? SkyIrradiance.SampleLevel(LinearClamp, d, 0).rgb * SkyIntensity : 0.0.xxx;
    }
    gRad[gi] = rad; gDir[gi] = d;
    GroupMemoryBarrierWithGroupSync();

    // The first OctTexels threads each integrate one octahedral texel over all rays.
    float alpha = (HistoryValid > 0.5) ? saturate(EmaAlpha) : 1.0;
    if (gi < (uint)OctTexels) {
        uint texel = gi;
        float2 uv = (float2(texel % OctRes, texel / OctRes) + 0.5) / float(OctRes);
        float3 texelDir = OctDecode(uv);
        float3 sum = 0.0.xxx; float wsum = 0.0;
        [unroll] for (uint r = 0; r < RAYS; r++) {
            float w = max(dot(texelDir, gDir[r]), 0.0);
            sum += gRad[r] * w; wsum += w;
        }
        float3 E = ((wsum > 1e-4) ? sum / wsum : 0.0.xxx) * Intensity;

        uint idx = probe * OctTexels + texel;
        float3 prev = PrevIrrad[idx].rgb;
        Irradiance[idx] = float4(Sanitize(lerp(prev, E, alpha)), 1.0);
    }
}
