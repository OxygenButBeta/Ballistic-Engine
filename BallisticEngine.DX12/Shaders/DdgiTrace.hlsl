// DDGI probe TRACE pass (compute, SM6.6) — GI plan P2.1. One thread per (probe, ray): generate a
// spherical-Fibonacci direction (rotated per frame), trace it against the scene TLAS via inline RayQuery,
// and shade the hit with the SAME world-radiance path as the P1 DxrGi.hlsl ClosestHit (albedo * (sun*NdotL*
// shadowRay + punctual + IBL(Ng))). Writes (radiance.rgb, hitDistance) per ray to the RayData UAV; the blend
// pass (DdgiBlend.hlsl) integrates those into each probe's octahedral irradiance + depth tiles. A miss
// returns the sky irradiance (so open-sky probes get ambient) and a large distance.
//
// Inline RayQuery in a COMPUTE shader (not an RT PSO) — no recursion, shadow rays are also inline. Bindless
// geometry + material decode is byte-identical to DxrGi.hlsl / GBufferBindless.hlsl (no drift).
//
// Bound: CBV b0 DdgiConstants, CBV b1 RtGiSun (sun + light count); table-less root SRVs: t0 TLAS, t5
// GpuMaterials, t6 RtInstance[], t7 Lights; t3 irradiance cube (sky fallback) as a table SRV; UAV u0 RayData;
// + bindless heap (ResourceDescriptorHeap[] for index/normal/uv buffers + albedo textures) + samplers s0/s1.

RaytracingAccelerationStructure Scene : register(t0);
TextureCube Irradiance : register(t3);              // sky/IBL irradiance (probe ambient + miss)
RWStructuredBuffer<float4> RayData : register(u0);   // [probe * RaysPerProbe + ray] = (radiance.rgb, dist)

cbuffer DdgiConstants : register(b0) {
    float4 OriginSpacingX;   // xyz grid origin (world), w spacing.x
    float4 SpacingYZ;        // x spacing.y, y spacing.z
    float4 ProbeDims;        // xyz (ProbesX,ProbesY,ProbesZ), w ProbeCount
    float4 Params0;          // x irrTexels, y depthTexels, z hysteresis, w frameIndex
    float4 Params1;          // x maxRayDist, y normalBias, z viewBias, w intensity
};
cbuffer RtGiSun : register(b1) {
    float3 SunDir;   float NormalBias;
    float3 SunColor; float LightCount;
};
SamplerState LinearClamp : register(s0);
SamplerState LinearWrap  : register(s1);

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
StructuredBuffer<GpuMaterial> GpuMaterials : register(t5);
StructuredBuffer<RtInstance>  RtInstances  : register(t6);
StructuredBuffer<GpuLight>    Lights       : register(t7);

static const float PI = 3.14159265359;

float3 Sanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

uint RaysPerProbe() { return 144u; }   // spherical-Fibonacci ray count per probe (tune in P2.5)

// Spherical Fibonacci direction i of n, rotated by a per-frame random basis so the probe samples the whole
// sphere over frames (the temporal accumulation in the blend pass converges it).
float3 SphericalFibonacci(uint i, uint n, float jitter) {
    float phi = 2.39996323 * (float(i) + jitter);              // golden angle
    float cosT = 1.0 - (2.0 * float(i) + 1.0) / float(n);
    float sinT = sqrt(saturate(1.0 - cosT * cosT));
    return float3(cos(phi) * sinT, sin(phi) * sinT, cosT);
}

float Hash1(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}

// Probe (px,py,pz) world position from the flat probe index.
float3 ProbeWorldPos(uint probe) {
    uint px = probe % (uint)ProbeDims.x;
    uint py = (probe / (uint)ProbeDims.x) % (uint)ProbeDims.y;
    uint pz = probe / ((uint)ProbeDims.x * (uint)ProbeDims.y);
    return OriginSpacingX.xyz + float3(px * OriginSpacingX.w, py * SpacingYZ.x, pz * SpacingYZ.y);
}

// Inline visibility ray (shadow). 1 lit / 0 occluded.
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

// Shade a committed RayQuery hit in world space (mirrors DxrGi.hlsl ClosestHit). Returns radiance (RAW HDR).
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
    if (dot(Ng, rayDir) > 0.0) Ng = -Ng;   // two-sided
    float2 uv = uvs[i0] * bary.x + uvs[i1] * bary.y + uvs[i2] * bary.z;

    GpuMaterial m = GpuMaterials[triMat[prim]];
    Texture2D diffuseMap = ResourceDescriptorHeap[m.DiffuseIdx];
    float3 albedo = min(diffuseMap.SampleLevel(LinearWrap, uv, 0).rgb * m.BaseColorFactor.rgb, 0.9.xxx);

    float3 hit = q.WorldRayOrigin() + q.CommittedRayT() * rayDir;
    float ndl = saturate(dot(Ng, normalize(SunDir)));
    float3 sun = SunColor * ndl * (ndl > 0.0 ? Visibility(hit, Ng, normalize(SunDir), 1e4) : 0.0);
    float3 punctual = PunctualDiffuse(hit, Ng);
    float3 ambient = Irradiance.SampleLevel(LinearClamp, Ng, 0).rgb;
    float3 radiance = albedo * (sun + punctual + ambient);

    float luma = dot(radiance, float3(0.2126, 0.7152, 0.0722));
    if (luma > 1.0e5) radiance *= 1.0e5 / max(luma, 1e-4);
    return Sanitize(radiance);
}

[numthreads(64, 1, 1)]
void CSMain(uint3 dtid : SV_DispatchThreadID) {
    uint rays = RaysPerProbe();
    uint total = (uint)ProbeDims.w * rays;
    uint id = dtid.x;
    if (id >= total) return;
    uint probe = id / rays;
    uint ray = id % rays;

    float3 probePos = ProbeWorldPos(probe);
    float jitter = Hash1(probe * 31u + (uint)Params0.w * 2654435761u);
    float3 dir = SphericalFibonacci(ray, rays, jitter);

    RayDesc rd;
    rd.Origin = probePos; rd.Direction = dir; rd.TMin = 0.0; rd.TMax = max(Params1.x, 1.0);
    RayQuery<RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, rd);
    q.Proceed();

    float3 radiance; float dist;
    if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
        radiance = ShadeHit(q, dir);
        dist = q.CommittedRayT();
    } else {
        radiance = Irradiance.SampleLevel(LinearClamp, dir, 0).rgb;   // sky
        dist = Params1.x;                                              // far (open)
    }
    RayData[id] = float4(radiance, dist);
}
