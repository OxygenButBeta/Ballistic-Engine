// Lumen V2 — surface-card lighting (P3). The plan's LumenDirectOnCardsPass: light every surface "card" with
// first-bounce radiance so an RT hit can SAMPLE real surface radiance instead of re-shading direct light per
// hit (and so P4 has a place to accumulate multi-bounce). Cards are PER-TRIANGLE (the finest stable surface
// record; the engine has no lightmap UV, so a 2D atlas card would need a parameterization that doesn't exist).
//
// 1D dispatch over ALL scene triangles. Each thread:
//   - resolves its (instance, localTri) by binary-searching the per-instance triangle-offset table,
//   - fetches the triangle's 3 OBJECT-space positions/normals/uv from the bindless geo buffers and transforms
//     them to world space with the instance matrix,
//   - decodes the triangle's material (albedo + emissive) bindlessly,
//   - lights the triangle triCenter: sun (N·L, shadow-rayed) + punctual (shadow-rayed) + emissive + sky-vis
//     ambient, and writes the outgoing radiance into CardRadiance[triOffset + localTri].
// The card radiance is LIT RADIANCE LEAVING the surface (albedo*(direct) + emissive), exactly what an RT hit
// in LumenGi.hlsl wants to add as incoming radiance — no per-hit relighting.
//
// Bound: TLAS t0 (root SRV) | CardRadiance u0 (root UAV) | sky irradiance cube t1 (table) |
//        LumenInstanceMeta[] t2 / RtInstance[] t3 / GpuMaterials t4 / Lights t5 (root SRVs) |
//        LumenCardConstants b0 | bindless heap (ResourceDescriptorHeap[] for per-instance geo) | s0 clamp / s1 wrap.

RaytracingAccelerationStructure Scene : register(t0);
RWStructuredBuffer<float4> CardRadiance : register(u0);
TextureCube SkyIrradiance : register(t1);
RWStructuredBuffer<uint> LastUpdated : register(u1);   // P7 #1: per-record age (frame index of last relight)

struct LumenInstanceMeta { uint TriOffset, TriCount, ClusterOffset, ClusterCount; float4x4 World; };
struct RtInstance { uint NormalIdx, UvIdx, IndexIdx, TriMatIdx; uint PositionIdx, TriCount, Pad0, Pad1; };
struct GpuMaterial {
    uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
    uint AoIdx, EmissiveIdx, Pad0, Pad1;
    float4 BaseColorFactor; float4 EmissiveFactor;
    float Metallic, Roughness, SpecularReflectance, NormalStrength;
    float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
    float Cutout, HasEmissive, Pad2, Pad3;
};
struct GpuLight { float4 PosRange; float4 Color; float4 DirCosOuter; float4 Extra; };
StructuredBuffer<LumenInstanceMeta> Instances   : register(t2);
StructuredBuffer<RtInstance>        RtInstances : register(t3);
StructuredBuffer<GpuMaterial>       GpuMaterials: register(t4);
StructuredBuffer<GpuLight>          Lights      : register(t5);
StructuredBuffer<float4>            PrevCard    : register(t6);   // P4: PREVIOUS frame's cache (multi-bounce + EMA)
StructuredBuffer<uint>              TriToCluster : register(t7);   // #2A: global tri index → LOCAL cluster index (2nd-bounce hit→record)
StructuredBuffer<uint>              ClusterToTri : register(t8);   // #2A: record (global cluster) → global representative tri
// Sıra 5: per-record WORLD-space card plane frame. A texel (tx,ty) in [0,TexelDim)² maps to a world point on the
// card plane → lit independently → cluster-interior detail. Cache index = record*TexelsPerRecord + ty*TexelDim+tx.
struct ClusterCard { float3 Origin; float InvExtentU; float3 U; float InvExtentV; float3 V; float Pad0; float3 Normal; float Pad1; };
StructuredBuffer<ClusterCard>       ClusterCards : register(t9);   // [recordCount]

cbuffer LumenCardConstants : register(b0) {
    float3 SunDir;   float SunBias;      // TO the sun (normalized), world; shadow-ray origin offset
    float3 SunColor; float LightCount;   // sun radiance (RAW HDR); # punctual lights
    uint InstanceCount; uint TotalTris; float SkyIntensity; float UseSky;
    float SkyVisRays; float EmaAlpha; float BounceRays; float HistoryValid;   // P4: temporal blend + 2nd-bounce gather
    uint FrameIndex; uint UpdateStride; uint ForceFull; uint TexelDim;   // P7 #1 round-robin; Sıra 5 TexelDim (1=legacy)
};
SamplerState LinearClamp : register(s0);
SamplerState LinearWrap  : register(s1);

static const float PI = 3.14159265359;

float3 Sanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}
float Hash(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}
float3x3 BuildBasis(float3 n) {
    float3 up = abs(n.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 t = normalize(cross(up, n)); float3 b = cross(n, t);
    return float3x3(t, b, n);
}
float3 CosineHemisphere(uint i, uint n, float jitter) {
    float u1 = (float(i) + jitter) / float(n);
    float u2 = frac(jitter * 1.61803398875 + float(i) * 0.7548776662);
    float r = sqrt(saturate(u1)); float phi = 6.28318530718 * u2;
    return float3(r * cos(phi), r * sin(phi), sqrt(saturate(1.0 - u1)));
}

float Visibility(float3 origin, float3 N, float3 dir, float maxDist) {
    RayDesc ray; ray.Origin = origin + N * max(SunBias, 0.004); ray.Direction = dir; ray.TMin = 0.02; ray.TMax = maxDist;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray); q.Proceed();
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 0.0 : 1.0;
}

// Binary search the instance owning global triangle `tri` (instances are ordered by ascending TriOffset).
uint InstanceForTri(uint tri) {
    uint lo = 0, hi = InstanceCount - 1, result = 0;
    [loop] while (lo <= hi) {
        uint mid = (lo + hi) >> 1;
        LumenInstanceMeta m = Instances[mid];
        if (tri >= m.TriOffset && tri < m.TriOffset + m.TriCount) { result = mid; break; }
        if (tri < m.TriOffset) { if (mid == 0) break; hi = mid - 1; }
        else lo = mid + 1;
    }
    return result;
}

[numthreads(64, 1, 1)]
void CSMain(uint3 dtid : SV_DispatchThreadID) {
    // #2A: ONE thread per RECORD (cluster). The dispatch is RecordCount-wide (30-50× fewer threads than per-
    // triangle) and there is NO write race — each record is owned by exactly one thread. The record lights its
    // cluster's REPRESENTATIVE triangle (ClusterToTri[record]) and writes that radiance into the cache.
    uint record = dtid.x;
    if (record >= TotalTris) return;   // TotalTris is repurposed as RecordCount for the dispatch bound (see C#)

    uint gtri = ClusterToTri[record];
    uint inst = InstanceForTri(gtri);
    LumenInstanceMeta meta = Instances[inst];
    uint localTri = gtri - meta.TriOffset;

    // P7 #1 — UPDATE BUDGET, per-RECORD. Only a round-robin slice of records relight each frame; the rest carry
    // their previous radiance forward. The view-independent + EMA-stable cache makes a record re-lit every
    // `UpdateStride` frames look identical to per-frame for a static light. `ForceFull` (sun/light/topology dirty)
    // overrides → full relight that frame (no latency). A never-updated record (age 0 after a build) is always due.
    bool everUpdated = LastUpdated[record] != 0u;
    bool due = (ForceFull != 0u) || !everUpdated || ((record % UpdateStride) == (FrameIndex % UpdateStride));
    if (!due) {
        CardRadiance[record] = float4(PrevCard[record].rgb, 1.0);   // carry forward
        return;
    }

    RtInstance geo = RtInstances[inst];
    Buffer<uint>             indices = ResourceDescriptorHeap[geo.IndexIdx];
    StructuredBuffer<float3> positions = ResourceDescriptorHeap[geo.PositionIdx];
    StructuredBuffer<float3> normals = ResourceDescriptorHeap[geo.NormalIdx];
    StructuredBuffer<float2> uvs     = ResourceDescriptorHeap[geo.UvIdx];
    StructuredBuffer<uint>   triMat  = ResourceDescriptorHeap[geo.TriMatIdx];

    uint i0 = indices[localTri * 3 + 0], i1 = indices[localTri * 3 + 1], i2 = indices[localTri * 3 + 2];

    // Object-space triangle → world via the instance matrix.
    float3 p0 = mul(float4(positions[i0], 1.0), meta.World).xyz;
    float3 p1 = mul(float4(positions[i1], 1.0), meta.World).xyz;
    float3 p2 = mul(float4(positions[i2], 1.0), meta.World).xyz;
    float3 triCenter = (p0 + p1 + p2) / 3.0;

    // Smooth shading normal (averaged vertex normals, object→world). Fall back to the geometric normal.
    float3 nObj = normals[i0] + normals[i1] + normals[i2];
    float3 N = mul((float3x3)meta.World, nObj);
    if (dot(N, N) < 1e-8) N = cross(p1 - p0, p2 - p0);
    N = normalize(N);

    float2 uv = (uvs[i0] + uvs[i1] + uvs[i2]) / 3.0;
    GpuMaterial m = GpuMaterials[triMat[localTri]];
    Texture2D diffuseMap = ResourceDescriptorHeap[m.DiffuseIdx];
    float3 albedo = min(diffuseMap.SampleLevel(LinearWrap, uv, 0).rgb * m.BaseColorFactor.rgb, 0.95.xxx);

    float3 emissive = 0.0.xxx;
    if (m.HasEmissive > 0.5) {
        Texture2D emissiveMap = ResourceDescriptorHeap[m.EmissiveIdx];
        emissive = emissiveMap.SampleLevel(LinearWrap, uv, 0).rgb * m.EmissiveFactor.rgb;
    }

    // === Sıra 5: per-TEXEL lighting. The albedo/emissive/material are cluster-wide (one submesh → one material),
    // computed once above. The DIRECT + sky-vis + 2nd-bounce terms are recomputed at EACH texel's own world point on
    // the card plane → cluster-interior detail (a sun shadow edge or punctual falloff inside the cluster). TexelDim
    // 1 (mesh-cards off) → ONE texel at the representative triangle center → byte-identical to the pre-Sıra-5 path. ===
    uint texelDim = max(TexelDim, 1u);
    uint tpr = texelDim * texelDim;
    ClusterCard card = ClusterCards[record];
    float alpha = HistoryValid > 0.5 ? saturate(EmaAlpha) : 1.0;

    [loop] for (uint texel = 0; texel < tpr; texel++)
    {
        // Texel center world position + shading normal. TexelDim 1 → the representative triangle center + normal
        // (exact legacy behaviour). TexelDim >1 → a point on the card plane at this texel's UV, card-plane normal.
        float3 P, Ntex;
        if (texelDim == 1u) { P = triCenter; Ntex = N; }
        else {
            float2 uvc = (float2(texel % texelDim, texel / texelDim) + 0.5) / float(texelDim);   // texel center [0,1]²
            P = card.Origin + card.U * (uvc.x / max(card.InvExtentU, 1e-8))
                            + card.V * (uvc.y / max(card.InvExtentV, 1e-8));
            Ntex = card.Normal;
        }

        // Direct sun (shadow-rayed) at this texel.
        float3 sunDir = normalize(SunDir);
        float ndl = saturate(dot(Ntex, sunDir));
        float3 sun = (ndl > 0.0) ? SunColor * ndl * Visibility(P, Ntex, sunDir, 1e4) : 0.0.xxx;

        // Punctual (shadow-rayed) at this texel.
        float3 punctual = 0.0.xxx;
        int nl = min((int)LightCount, 32);
        [loop] for (int i = 0; i < nl; i++) {
            GpuLight L = Lights[i];
            float3 toL = L.PosRange.xyz - P;
            float dist = length(toL);
            if (dist > L.PosRange.w || dist < 1e-4) continue;
            float3 Ld = toL / dist;
            float nd = saturate(dot(Ntex, Ld));
            if (nd <= 0.0) continue;
            float t = saturate(1.0 - pow(dist / L.PosRange.w, 4.0));
            float3 rad = L.Color.rgb * (t * t / max(dist * dist, 1e-4));
            if (L.Color.w >= 0.5) {
                float cosA = dot(-Ld, normalize(L.DirCosOuter.xyz));
                float cone = saturate((cosA - L.DirCosOuter.w) / max(L.Extra.x - L.DirCosOuter.w, 1e-4));
                if (cone <= 0.0) continue;
                rad *= cone * cone;
            }
            punctual += rad * nd * Visibility(P, Ntex, Ld, dist);
        }

        // Sky-visibility + 2nd-bounce hemisphere gather at this texel.
        float3 indirect = 0.0.xxx;
        uint sr = (uint)clamp(max(SkyVisRays, BounceRays), 1.0, 8.0);
        float jit = Hash((record * 2654435761u) ^ (texel * 40503u));
        float3x3 basis = BuildBasis(Ntex);
        [loop] for (uint k = 0; k < sr; k++) {
            float3 d = normalize(mul(CosineHemisphere(k, sr, jit), basis));
            RayDesc rd; rd.Origin = P + Ntex * max(SunBias, 0.004); rd.Direction = d; rd.TMin = 0.02; rd.TMax = 1e4;
            RayQuery<RAY_FLAG_FORCE_OPAQUE> q; q.TraceRayInline(Scene, 0, 0xFF, rd); q.Proceed();
            if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
                if (HistoryValid > 0.5 && BounceRays > 0.5) {
                    uint hi = q.CommittedInstanceID();
                    LumenInstanceMeta hm = Instances[hi];
                    uint hrec = hm.ClusterOffset + TriToCluster[hm.TriOffset + q.CommittedPrimitiveIndex()];
                    indirect += PrevCard[hrec * tpr].rgb;   // sample the hit record's texel 0 (coarse 2nd bounce)
                }
            } else if (UseSky > 0.5) {
                indirect += SkyIrradiance.SampleLevel(LinearClamp, d, 0).rgb * SkyIntensity;
            }
        }
        indirect /= float(sr);

        float3 lit = albedo * (sun + punctual + indirect) + emissive;
        uint cacheIdx = record * tpr + texel;
        float3 prev = PrevCard[cacheIdx].rgb;
        float3 radiance = lerp(prev, lit, alpha);
        CardRadiance[cacheIdx] = float4(Sanitize(radiance), 1.0);
    }

    // P7 #1: stamp this record as updated this frame (FrameIndex+1 so it's never 0 — 0 means "never updated").
    LastUpdated[record] = FrameIndex + 1u;
}
