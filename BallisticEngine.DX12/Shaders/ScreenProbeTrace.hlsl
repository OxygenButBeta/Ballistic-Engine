// SCREEN-SPACE RADIANCE PROBE — TRACE pass (compute, SM6.6). GI plan Phase 4 (P4.0).
//
// One thread per (screen probe, ray). A screen probe sits ON a surface (placed by ScreenProbePlace), so it
// gathers the HEMISPHERE about the probe's world normal (cosine-distributed). Each ray is a SHORT DXR
// RayQuery (TMax ~ a few metres — the near/mid field). The hit is shaded with the SAME world-radiance path as
// the DDGI trace (DdgiTrace.hlsl ShadeHit) — bindless geo+material, sun + punctual + DDGI-field ambient — so
// the screen-probe radiance is consistent with the world cache. The KEY Phase-4 wiring: on a MISS or a far
// hit, the ray CONTINUES into the DDGI world cache (SampleIrradianceField at the ray's far end) instead of a
// flat sky — this is Lumen's "screen traces hand off to the world radiance cache" hierarchy. Writes raw HDR
// radiance per ray to RayData; the blend pass integrates it into the probe's octahedral radiance tile.
//
// Bound: CBV b0 ScreenProbeConstants, CBV b1 RtGiSun (sun + light count); table {t0 TLAS, t3 irr cube, t4
// DDGI irradiance atlas} in the bindless tail; root SRV t5 GpuMaterials, t6 RtInstance[], t7 Lights, t8 DDGI
// ProbeState, t9 ScreenProbePos, t10 ScreenProbeNormal; root UAV u0 RayData; bindless heap + samplers s0/s1.

RaytracingAccelerationStructure Scene : register(t0);
TextureCube Irradiance : register(t3);              // sky/IBL cube (bound for parity w/ the DDGI trace table;
                                                    // P4.0 takes the far field purely from the DDGI cache —
                                                    // a grid-boundary sky term using this is a P4.1 option)
Texture2D<float4> DdgiIrradiance : register(t4);    // the DDGI world-cache irradiance atlas (far-field handoff)
RWStructuredBuffer<float4> RayData : register(u0);   // [probe * RaysPerProbe + ray] = (radiance.rgb, dist)

cbuffer ScreenProbeConstants : register(b0) {
    float4x4 InvViewProj;
    float4 SpParams0;   // x probesX y probesY z downsample w frameIndex
    float4 SpParams1;   // x screenW y screenH z maxRayDist w preExposure
    float4 SpParams2;   // x irrTexels y normalBias z intensity w emissiveEnable
};
cbuffer RtGiSun : register(b1) {
    float3 SunDir;   float NormalBias;
    float3 SunColor; float LightCount;
};

// The DDGI grid description, needed to sample the world cache on miss/far. Mirrors DdgiConstants' grid fields;
// supplied by the renderer in this CBV (b2) so the screen-probe trace knows the world-probe layout.
cbuffer DdgiGridConstants : register(b2) {
    float4 DdgiOriginSpacingX;   // xyz origin, w spacing.x
    float4 DdgiSpacingYZ;        // x spacing.y, y spacing.z
    float4 DdgiProbeDims;        // xyz (ProbesX,ProbesY,ProbesZ), w ProbeCount
    float4 DdgiParams;           // x irrTexels, y normalBias, z/w pad
};

struct RtInstance { uint NormalIdx, UvIdx, IndexIdx, TriMatIdx; };
struct GpuMaterial {
    uint DiffuseIdx, NormalIdx, MetallicIdx, RoughnessIdx;
    uint AoIdx, EmissiveIdx, Pad0, Pad1;
    float4 BaseColorFactor; float4 EmissiveFactor;
    float Metallic, Roughness, SpecularReflectance, NormalStrength;
    float NormalFlipY, HasMetallicMap, HasRoughnessMap, PackedOrm;
    float Cutout, HasEmissive, Pad2, Pad3;
};
struct GpuLight { float4 PosRange; float4 Color; float4 DirCosOuter; float4 Extra; };
StructuredBuffer<GpuMaterial> GpuMaterials  : register(t5);
StructuredBuffer<RtInstance>  RtInstances   : register(t6);
StructuredBuffer<GpuLight>    Lights        : register(t7);
StructuredBuffer<float4>      DdgiProbeState : register(t8);   // DDGI per-probe (offset.xyz, active)
StructuredBuffer<float4>      ProbePosBuf    : register(t9);   // screen probe (worldPos.xyz, valid)
StructuredBuffer<float4>      ProbeNormalBuf : register(t10);  // screen probe (worldNormal.xyz, 0)
SamplerState LinearClamp : register(s0);
SamplerState LinearWrap  : register(s1);

static const float PI = 3.14159265359;

float3 Sanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

uint RaysPerProbe() { return 64u; }   // 8x8 octahedron worth of hemisphere rays per screen probe

// Cosine-hemisphere direction i of n about +Z (Fibonacci spiral over the hemisphere), then rotated into the
// probe's tangent frame. Cosine-distributed so the radiance integral the blend pass forms is the irradiance.
float3 HemisphereFibonacci(uint i, uint n, float jitter) {
    float phi = 2.39996323 * (float(i) + jitter);          // golden angle
    float cosT = sqrt(1.0 - (float(i) + 0.5) / float(n));  // cosine-weighted z (upper hemisphere)
    float sinT = sqrt(saturate(1.0 - cosT * cosT));
    return float3(cos(phi) * sinT, sin(phi) * sinT, cosT);
}

float Hash1(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}

// Build an orthonormal basis around N (Duff et al. 2017, branchless).
void OrthoBasis(float3 N, out float3 T, out float3 B) {
    float s = N.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (s + N.z);
    float b = N.x * N.y * a;
    T = float3(1.0 + s * N.x * N.x * a, s * b, -s * N.x);
    B = float3(b, s + N.y * N.y * a, -N.y);
}

float Visibility(float3 origin, float3 N, float3 dir, float maxDist) {
    RayDesc ray;
    ray.Origin = origin + N * max(NormalBias, 0.001);
    ray.Direction = dir; ray.TMin = 0.02; ray.TMax = maxDist;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray); q.Proceed();
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 0.0 : 1.0;
}

float3 PunctualDiffuse(float3 hit, float3 N) {
    float3 sum = 0.0.xxx;
    int n = min((int)LightCount, 64);
    [loop] for (int i = 0; i < n; i++) {
        GpuLight L = Lights[i];
        float3 toL = L.PosRange.xyz - hit;
        float dist = length(toL);
        if (dist > L.PosRange.w || dist < 1e-4) continue;
        float3 Ld = toL / dist;
        float ndl = saturate(dot(N, Ld));
        if (ndl <= 0.0) continue;
        float t = saturate(1.0 - pow(dist / L.PosRange.w, 4.0));
        float3 radiance = L.Color.rgb * (t * t / max(dist * dist, 1e-4));
        if (L.Color.w >= 0.5) {
            float cosA = dot(-Ld, normalize(L.DirCosOuter.xyz));
            float cone = saturate((cosA - L.DirCosOuter.w) / max(L.Extra.x - L.DirCosOuter.w, 1e-4));
            if (cone <= 0.0) continue;
            radiance *= cone * cone;
        }
        sum += radiance * ndl * Visibility(hit, N, Ld, dist);
    }
    return sum;
}

// --- DDGI world-cache sample: 8-probe trilinear over the DDGI field along N (matches DdgiTrace.SampleIrradiance
// Field exactly — same OctEncode, atlas tile layout, cosine-wrap). Used as the FAR-FIELD ambient at the hit
// AND as the radiance for a missed/far screen-probe ray (the screen->world handoff). ---
float2 DdgiOctEncode(float3 dir) {
    dir /= (abs(dir.x) + abs(dir.y) + abs(dir.z));
    float2 uv = dir.xy;
    if (dir.z < 0.0)
        uv = (1.0 - abs(uv.yx)) * float2(uv.x >= 0.0 ? 1.0 : -1.0, uv.y >= 0.0 ? 1.0 : -1.0);
    return uv * 0.5 + 0.5;
}
float3 DdgiProbePos(uint px, uint py, uint pz) {
    float3 basePos = DdgiOriginSpacingX.xyz + float3(px * DdgiOriginSpacingX.w, py * DdgiSpacingYZ.x, pz * DdgiSpacingYZ.y);
    uint probe = (pz * (uint)DdgiProbeDims.y + py) * (uint)DdgiProbeDims.x + px;
    return basePos + DdgiProbeState[probe].xyz;
}
float3 SampleDdgiField(float3 worldPos, float3 N) {
    float3 spacing = float3(DdgiOriginSpacingX.w, DdgiSpacingYZ.x, DdgiSpacingYZ.y);
    float3 biasPos = worldPos + N * DdgiParams.y;
    float3 rel = (biasPos - DdgiOriginSpacingX.xyz) / spacing;
    int3 baseC = (int3)floor(rel);
    float3 f = rel - (float3)baseC;
    int3 dims = int3((int)DdgiProbeDims.x, (int)DdgiProbeDims.y, (int)DdgiProbeDims.z);
    uint irrTexels = (uint)DdgiParams.x;
    uint tile = irrTexels + 2u;
    float2 atlasSize = float2((uint)DdgiProbeDims.x * (uint)DdgiProbeDims.z, (uint)DdgiProbeDims.y) * float(tile);
    float2 octI = DdgiOctEncode(N);

    float3 sum = 0.0.xxx; float wsum = 0.0;
    [unroll] for (int i = 0; i < 8; i++) {
        int3 off = int3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        int3 c = baseC + off;
        if (any(c < 0) || any(c >= dims)) continue;
        uint probe = ((uint)c.z * (uint)DdgiProbeDims.y + (uint)c.y) * (uint)DdgiProbeDims.x + (uint)c.x;
        if (DdgiProbeState[probe].w < 0.5) continue;   // skip inactive (buried) DDGI probes
        float3 toProbe = DdgiProbePos((uint)c.x, (uint)c.y, (uint)c.z) - biasPos;
        float3 dirToProbe = dot(toProbe, toProbe) > 1e-10 ? normalize(toProbe) : N;
        float3 triv = lerp(1.0 - f, f, (float3)off);
        float trilinear = triv.x * triv.y * triv.z;
        float wrap = saturate(dot(dirToProbe, N) * 0.5 + 0.5); wrap = wrap * wrap + 0.2;
        float wgt = trilinear * wrap;
        if (wgt < 1e-6) continue;
        uint col = (uint)c.z * (uint)DdgiProbeDims.x + (uint)c.x, row = (uint)c.y;
        float2 texelXY = float2(col * tile, row * tile) + 1.0 + octI * float(irrTexels);
        float3 irr = DdgiIrradiance.SampleLevel(LinearClamp, texelXY / atlasSize, 0).rgb;
        sum += Sanitize(irr) * wgt; wsum += wgt;
    }
    return wsum > 1e-5 ? sum / wsum : 0.0.xxx;
}

// Shade a committed hit (mirrors DdgiTrace.ShadeHit — byte-identical bindless geo/material + sun/punctual).
// The HIT AMBIENT is the DDGI world field at the hit (so screen-probe rays inherit multi-bounce from the cache).
float3 ShadeHit(RayQuery<RAY_FLAG_FORCE_OPAQUE> q, float3 rayDir) {
    uint instId = q.CommittedInstanceID();
    uint prim = q.CommittedPrimitiveIndex();
    float2 bc2 = q.CommittedTriangleBarycentrics();
    float3 bary = float3(1.0 - bc2.x - bc2.y, bc2.x, bc2.y);

    RtInstance inst = RtInstances[instId];
    Buffer<uint>             indices = ResourceDescriptorHeap[inst.IndexIdx];
    StructuredBuffer<float3> normals = ResourceDescriptorHeap[inst.NormalIdx];
    StructuredBuffer<float2> uvs     = ResourceDescriptorHeap[inst.UvIdx];
    StructuredBuffer<uint>   triMat  = ResourceDescriptorHeap[inst.TriMatIdx];

    uint i0 = indices[prim * 3 + 0], i1 = indices[prim * 3 + 1], i2 = indices[prim * 3 + 2];
    float3 nObj = normalize(normals[i0] * bary.x + normals[i1] * bary.y + normals[i2] * bary.z);
    float3 Ng = normalize(mul((float3x3)q.CommittedObjectToWorld3x4(), nObj));
    if (dot(Ng, rayDir) > 0.0) Ng = -Ng;   // two-sided: face the incoming ray
    float2 uv = uvs[i0] * bary.x + uvs[i1] * bary.y + uvs[i2] * bary.z;

    GpuMaterial m = GpuMaterials[triMat[prim]];
    Texture2D diffuseMap = ResourceDescriptorHeap[m.DiffuseIdx];
    float3 albedo = min(diffuseMap.SampleLevel(LinearWrap, uv, 0).rgb * m.BaseColorFactor.rgb, 0.9.xxx);

    // Emissive self-emission L_e (emissive-as-GI-source): emissive surfaces act as area lights in the bounce.
    // Byte-identical decode to GBufferBindless (emissiveMap*EmissiveFactor, gated on HasEmissive); added OUTSIDE
    // the albedo product (no /PI, no albedo multiply). Gated by SpParams2.w (emissiveEnable) for the A/B door.
    float3 emissive = 0.0.xxx;
    if (SpParams2.w > 0.5 && m.HasEmissive > 0.5) {
        Texture2D emissiveMap = ResourceDescriptorHeap[m.EmissiveIdx];
        emissive = emissiveMap.SampleLevel(LinearWrap, uv, 0).rgb * m.EmissiveFactor.rgb;
    }

    float3 hit = q.WorldRayOrigin() + q.CommittedRayT() * rayDir;
    float ndl = saturate(dot(Ng, normalize(SunDir)));
    float3 sun = SunColor * ndl * (ndl > 0.0 ? Visibility(hit, Ng, normalize(SunDir), 1e4) : 0.0);
    float3 punctual = PunctualDiffuse(hit, Ng);
    float3 ambient = SampleDdgiField(hit, Ng);   // far-field multi-bounce from the world cache

    float3 radiance = albedo * (sun + punctual + ambient) + emissive;
    float luma = dot(radiance, float3(0.2126, 0.7152, 0.0722));
    if (luma > 1.0e5) radiance *= 1.0e5 / max(luma, 1e-4);
    return Sanitize(min(radiance, 60000.0.xxx));
}

[numthreads(64, 1, 1)]
void CSMain(uint3 dtid : SV_DispatchThreadID) {
    uint rays = RaysPerProbe();
    uint probesX = (uint)SpParams0.x, probesY = (uint)SpParams0.y;
    uint probeCount = probesX * probesY;
    uint id = dtid.x;
    uint total = probeCount * rays;
    if (id >= total) return;
    uint probe = id / rays;
    uint ray = id % rays;

    float4 pp = ProbePosBuf[probe];
    if (pp.w < 0.5) { RayData[id] = float4(0, 0, 0, SpParams1.z); return; }   // invalid (sky) probe → no GI
    float3 probePos = pp.xyz;
    float3 N = normalize(ProbeNormalBuf[probe].xyz);

    // Cosine-hemisphere ray in the probe's tangent frame, jittered per frame.
    float jitter = Hash1(probe * 31u + (uint)SpParams0.w * 2654435761u);
    float3 local = HemisphereFibonacci(ray, rays, jitter);
    float3 T, B; OrthoBasis(N, T, B);
    float3 dir = normalize(local.x * T + local.y * B + local.z * N);

    RayDesc rd;
    rd.Origin = probePos + N * max(SpParams2.y, 0.01);   // offset off the surface (normalBias)
    rd.Direction = dir; rd.TMin = 0.0; rd.TMax = max(SpParams1.z, 0.5);   // SHORT ray (near/mid field)
    RayQuery<RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, rd);
    q.Proceed();

    float3 radiance; float dist;
    if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
        radiance = ShadeHit(q, dir);
        dist = q.CommittedRayT();
    } else {
        // MISS → screen-trace hands off to the world cache: sample the DDGI field at the ray's far end along
        // the ray direction (the far-field radiance arriving from this direction). Lumen's screen->world
        // continuation. Plus the open sky through the cube (the DDGI field already folds sky into open probes,
        // but at the grid boundary the cube is the honest far term).
        // The world cache IS the far-field radiance (the DDGI field folds the sky into open probes). The field
        // is IRRADIANCE E (the DDGI gather forms albedo*E); but here it's the incoming RADIANCE L along ONE ray
        // direction, which the blend then cosine-integrates over 64 rays. Converting E->L for a Lambertian
        // far surface is L = E/PI (P4.1 energy fix — without it the blend's cosine re-integration double-counts,
        // which is why the P4.0 SunTemple isolate read ~2x bright). No sky term (the dead `+ sky*0.0` was the
        // NaN*0 black-hole anti-pattern [[ssgi-nan-mix-scrub]], removed in P4.0).
        float3 farPoint = probePos + dir * rd.TMax;
        radiance = Sanitize(SampleDdgiField(farPoint, -dir) / PI);
        dist = rd.TMax;
    }
    RayData[id] = float4(radiance, dist);
}
