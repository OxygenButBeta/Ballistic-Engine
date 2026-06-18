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
TextureCube Irradiance : register(t3);              // sky/IBL irradiance (the sky term feeding open-sky probes)
Texture2D<float4> PrevIrradiance : register(t4);    // P2.3: LAST frame's DDGI irradiance atlas (multi-bounce)
Texture2D<float2> PrevDepth : register(t11);        // LAST frame's DDGI depth-moments atlas — Chebyshev leak gate
RWStructuredBuffer<float4> RayData : register(u0);   // [probe * RaysPerProbe + ray] = (radiance.rgb, dist)

cbuffer DdgiConstants : register(b0) {
    float4 OriginSpacingX;   // xyz grid origin (world), w spacing.x
    float4 SpacingYZ;        // x spacing.y, y spacing.z
    float4 ProbeDims;        // xyz (ProbesX,ProbesY,ProbesZ), w ProbeCount
    float4 Params0;          // x irrTexels, y depthTexels, z hysteresis, w frameIndex
    float4 Params1;          // x maxRayDist, y normalBias, z feedbackEnable w intensity
    float4 Params2;          // P2.5 round-robin: x updateFraction(N), y phase, z fullUpdate(1/0), w pad
    float4 Params3;          // CHUNK1 bake: xyz camera world pos, w band width (m)
    float4 Params4;          // CHUNK1 bake: x bakeEnable, y bakeWave (open band), z convergeTarget, w pad
};

// CHUNK 1 GPU progressive bake: per-probe converged-frame counter. Trace increments it (once per probe, the
// ray-0 thread); a probe whose counter >= convergeTarget is FROZEN (skipped). Blend/classify read the same
// counter so they don't re-integrate a frozen probe. This is the whole bake QUEUE living on the GPU.
RWStructuredBuffer<uint> ProbeBakeState : register(u1);

// Distance band of a probe from the bake camera (0 = nearest). Used to ripple the bake outward from the camera.
uint ProbeBakeBand(float3 probePos) {
    float d = length(probePos - Params3.xyz);
    return (uint)floor(d / max(Params3.w, 0.5));
}

// P2.5 ROUND-ROBIN (live path) OR CHUNK1 PROGRESSIVE BAKE (bakeEnable>0.5): in bake mode a probe is eligible
// this frame iff its distance band has been opened (band <= bakeWave) AND it hasn't converged yet
// (counter < convergeTarget). So the bake ripples out from the camera and each probe stops once converged →
// the field freezes. Outside bake mode the original round-robin governs (byte-identical). Same test mirrored in
// DdgiBlend (CSIrradiance/CSDepth) + classify so a skipped probe's atlas tile is never touched.
bool ProbeActiveThisFrame(uint probe) {
    if (Params4.x > 0.5) {                                 // CHUNK1 progressive bake
        if (ProbeBakeState[probe] >= (uint)Params4.z) return false;   // already converged → frozen
        // band test uses the BASE probe pos (relocation offset is small; band granularity is coarse)
        uint px = probe % (uint)ProbeDims.x;
        uint py = (probe / (uint)ProbeDims.x) % (uint)ProbeDims.y;
        uint pz = probe / ((uint)ProbeDims.x * (uint)ProbeDims.y);
        float3 basePos = OriginSpacingX.xyz + float3(px * OriginSpacingX.w, py * SpacingYZ.x, pz * SpacingYZ.y);
        return ProbeBakeBand(basePos) <= (uint)Params4.y;
    }
    if (Params2.z > 0.5) return true;                     // fullUpdate
    uint n = max((uint)Params2.x, 1u);
    return (probe % n) == (uint)Params2.y;
}
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
StructuredBuffer<float4>      ProbeState   : register(t8);  // P2.4: per-probe (relocation offset.xyz, active)

static const float PI = 3.14159265359;

float3 Sanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

// CHUNK2: active ray count rides Params4.w (144 live / 256 baked). Clamp to [16, 256] (RayData is sized for 256).
uint RaysPerProbe() { return clamp((uint)Params4.w, 16u, 256u); }

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

// Probe (px,py,pz) world position from the flat probe index, + the P2.4 relocation offset.
float3 ProbeWorldPos(uint probe) {
    uint px = probe % (uint)ProbeDims.x;
    uint py = (probe / (uint)ProbeDims.x) % (uint)ProbeDims.y;
    uint pz = probe / ((uint)ProbeDims.x * (uint)ProbeDims.y);
    float3 basePos = OriginSpacingX.xyz + float3(px * OriginSpacingX.w, py * SpacingYZ.x, pz * SpacingYZ.y);
    return basePos + ProbeState[probe].xyz;
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

// --- P2.3 multi-bounce: sample LAST frame's DDGI irradiance field at a world point along a normal. The hit's
// "ambient" becomes the recursive probe irradiance instead of the flat IBL cube → each frame's trace folds in
// the previous frame's bounce, so the field converges to an infinite geometric series (bounded by albedo<1 +
// the per-bounce clamp below). Trilinear over the 8 enclosing probes x cosine front-facing wrap (no Chebyshev
// here — the feedback term is energy-clamped; the leak gate lives in the camera-visible gather). Matches the
// gather's OctEncode + ProbeAtlasUv + the atlas tile layout exactly.
float2 TraceOctEncode(float3 dir) {
    dir /= (abs(dir.x) + abs(dir.y) + abs(dir.z));
    float2 uv = dir.xy;
    if (dir.z < 0.0)
        uv = (1.0 - abs(uv.yx)) * float2(uv.x >= 0.0 ? 1.0 : -1.0, uv.y >= 0.0 ? 1.0 : -1.0);
    return uv * 0.5 + 0.5;
}
float3 ProbePos(uint px, uint py, uint pz) {
    float3 basePos = OriginSpacingX.xyz + float3(px * OriginSpacingX.w, py * SpacingYZ.x, pz * SpacingYZ.y);
    uint probe = (pz * (uint)ProbeDims.y + py) * (uint)ProbeDims.x + px;   // matches ProbeWorldPos flatten
    return basePos + ProbeState[probe].xyz;
}
float3 SampleIrradianceField(float3 worldPos, float3 N) {
    float3 spacing = float3(OriginSpacingX.w, SpacingYZ.x, SpacingYZ.y);
    float3 biasPos = worldPos + N * Params1.y;
    float3 rel = (biasPos - OriginSpacingX.xyz) / spacing;
    int3 baseC = (int3)floor(rel);
    float3 f = rel - (float3)baseC;
    int3 dims = int3((int)ProbeDims.x, (int)ProbeDims.y, (int)ProbeDims.z);
    uint irrTexels = (uint)Params0.x;
    uint tile = irrTexels + 2u;          // +2*BORDER (BORDER=1, must match DdgiBlend)
    float2 atlasSize = float2((uint)ProbeDims.x * (uint)ProbeDims.z, (uint)ProbeDims.y) * float(tile);
    float2 octI = TraceOctEncode(N);     // sample along the surface normal (diffuse receiver)

    // Depth-moments atlas layout (16x16 tiles, +2 border) — for the Chebyshev LEAK GATE below.
    uint depTexels = (uint)Params0.y;
    uint depTile = depTexels + 2u;
    float2 depAtlasSize = float2((uint)ProbeDims.x * (uint)ProbeDims.z, (uint)ProbeDims.y) * float(depTile);

    float3 sum = 0.0.xxx; float wsum = 0.0;
    [unroll] for (int i = 0; i < 8; i++) {
        int3 off = int3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        int3 c = baseC + off;
        if (any(c < 0) || any(c >= dims)) continue;
        float3 probeP = ProbePos((uint)c.x, (uint)c.y, (uint)c.z);
        float3 toProbe = probeP - biasPos;
        float distToProbe = length(toProbe);
        float3 dirToProbe = distToProbe > 1e-5 ? toProbe / distToProbe : N;
        float3 triv = lerp(1.0 - f, f, (float3)off);
        float trilinear = triv.x * triv.y * triv.z;
        float wrap = saturate(dot(dirToProbe, N) * 0.5 + 0.5); wrap = wrap * wrap + 0.2;

        // CHEBYSHEV VARIANCE VISIBILITY — THE LEAK GATE. Was missing here (this field read fed the screen-probe
        // far-field + multi-bounce ambient), so a closed interior receiver could read a probe SITTING OUTSIDE the
        // wall (it sees the sky) as if the wall weren't there → sky/IBL light leaking into a sealed room with no
        // light. Sample the probe's depth moments toward the receiver; if the receiver is statistically FARTHER
        // than the probe can "see" in that direction, it's occluded (behind a wall) → drop the probe. Same math
        // as DdgiGather (the camera-visible gather already had this; the field read did not).
        uint dcol = (uint)c.z * (uint)ProbeDims.x + (uint)c.x, drow = (uint)c.y;
        float2 depOct = TraceOctEncode(-dirToProbe);   // from the probe toward the receiver
        float2 depUv = (float2(dcol * depTile, drow * depTile) + 1.0 + depOct * float(depTexels)) / depAtlasSize;
        float2 mom = PrevDepth.SampleLevel(LinearClamp, depUv, 0).rg;
        float vis = 1.0;
        if (distToProbe > mom.x) {
            float variance = abs(mom.x * mom.x - mom.y);
            float diff = distToProbe - mom.x;
            vis = variance / (variance + diff * diff);
            vis = max(vis * vis * vis, 0.0);   // sharpen (RTXGI) — kill faint leaks
        }

        float w = trilinear * wrap * vis;
        if (w < 1e-6) continue;
        uint col = (uint)c.z * (uint)ProbeDims.x + (uint)c.x, row = (uint)c.y;
        float2 texelXY = float2(col * tile, row * tile) + 1.0 + octI * float(irrTexels);
        float3 irr = PrevIrradiance.SampleLevel(LinearClamp, texelXY / atlasSize, 0).rgb;
        sum += Sanitize(irr) * w; wsum += w;
    }
    return wsum > 1e-5 ? sum / wsum : 0.0.xxx;
}

// Shade a committed RayQuery hit in world space (mirrors DxrGi.hlsl ClosestHit). Returns radiance (RAW HDR).
// `backface` (out) = the ray hit the SOLID/back side (the probe is on the buried side) — derived from the
// geometric normal vs the ray, NOT from DXR CommittedTriangleFrontFace (that uses the fixed spec winding,
// which is INVERTED vs this engine's RH/CCW-from-front convention — DXR has no projection winding flip). The
// same two-sided dot test used everywhere in DxrGi/DDGI, so it's convention-independent.
float3 ShadeHit(RayQuery<RAY_FLAG_FORCE_OPAQUE> q, float3 rayDir, out bool backface) {
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
    backface = dot(Ng, rayDir) > 0.0;      // ray hit the solid/back side → probe is buried on this ray
    if (backface) Ng = -Ng;                // two-sided: face the incoming ray for shading
    float2 uv = uvs[i0] * bary.x + uvs[i1] * bary.y + uvs[i2] * bary.z;

    GpuMaterial m = GpuMaterials[triMat[prim]];
    Texture2D diffuseMap = ResourceDescriptorHeap[m.DiffuseIdx];
    float3 albedo = min(diffuseMap.SampleLevel(LinearWrap, uv, 0).rgb * m.BaseColorFactor.rgb, 0.9.xxx);

    // Emissive self-emission L_e (emissive-as-GI-source): emissive surfaces are area lights in the bounce.
    // Sampled byte-identically to the raster GBufferBindless decode (emissiveMap*EmissiveFactor, gated on
    // HasEmissive); the emissive SRV is already in the bound bindless heap. ADDED OUTSIDE the albedo product
    // below (self-emission is independent of the surface's reflectance — NO /PI, NO albedo multiply). Gated by
    // Params2.w (emissiveEnable) for the byte-identical A/B door. Emissive is a CONSTANT additive source, so it
    // does NOT compound through the multi-bounce feedback (unlike the field-driven ambient term).
    float3 emissive = 0.0.xxx;
    if (Params2.w > 0.5 && m.HasEmissive > 0.5) {
        Texture2D emissiveMap = ResourceDescriptorHeap[m.EmissiveIdx];
        emissive = emissiveMap.SampleLevel(LinearWrap, uv, 0).rgb * m.EmissiveFactor.rgb;
    }

    float3 hit = q.WorldRayOrigin() + q.CommittedRayT() * rayDir;
    float ndl = saturate(dot(Ng, normalize(SunDir)));
    float3 sun = SunColor * ndl * (ndl > 0.0 ? Visibility(hit, Ng, normalize(SunDir), 1e4) : 0.0);
    float3 punctual = PunctualDiffuse(hit, Ng);

    // Ambient at the hit. P2.3: when feedback is on (Params1.z>0.5) use LAST frame's DDGI irradiance FIELD at
    // the hit (the recursive multi-bounce term) instead of the flat IBL cube — the field already includes the
    // sky (open-sky probes sample it), so this is the COMPLETE ambient, no double-count. Per-bounce energy
    // clamp: bound the indirect luma BEFORE it re-enters the feedback loop (the SSGI-EMA black-hole guard) so
    // a bright bounce can't compound into runaway across frames. Falls back to the IBL cube on frame 0 / when
    // feedback is off. NOT a NaN scrub via *0 — the clamp is a luma rescale + Sanitize is applied at the end.
    float3 ambient;
    if (Params1.z > 0.5) {
        // The field is RAW HDR irradiance (same scale as the radiance the trace writes). Convergence is
        // guaranteed by albedo<=0.9 (each bounce attenuates), so the per-bounce clamp only has to kill
        // pathological fireflies/Inf before they re-enter the loop — cap at the same 1e5 ceiling as the final
        // radiance clamp (NOT a tight cap, which would crush legitimate raw-HDR irradiance ~1e3).
        float3 field = SampleIrradianceField(hit, Ng);
        float fl = dot(field, float3(0.2126, 0.7152, 0.0722));
        if (fl > 1.0e5) field *= 1.0e5 / max(fl, 1e-4);
        ambient = min(field, 60000.0.xxx);   // per-channel cap below the fp16 ceiling (atlas is RGBA16F)
    } else {
        ambient = Irradiance.SampleLevel(LinearClamp, Ng, 0).rgb;
    }
    float3 radiance = albedo * (sun + punctual + ambient) + emissive;

    // Luma clamp (the firefly cap) THEN a per-channel cap below the fp16 atlas ceiling (~65504) — the luma
    // clamp alone can leave one channel > fp16 max → a +Inf store; the atlas read-side Sanitize heals it next
    // frame, but the per-channel min avoids even a one-frame blotch on an extreme single-channel bounce.
    float luma = dot(radiance, float3(0.2126, 0.7152, 0.0722));
    if (luma > 1.0e5) radiance *= 1.0e5 / max(luma, 1e-4);
    return Sanitize(min(radiance, 60000.0.xxx));
}

[numthreads(64, 1, 1)]
void CSMain(uint3 dtid : SV_DispatchThreadID) {
    uint rays = RaysPerProbe();
    uint total = (uint)ProbeDims.w * rays;
    uint id = dtid.x;
    if (id >= total) return;
    uint probe = id / rays;
    uint ray = id % rays;

    // P2.5 round-robin / CHUNK1 bake: skip probes not eligible this frame (they keep last frame's RayData, which
    // the blend also skips — so the atlas tile is untouched). The expensive RayQuery below never runs for them.
    if (!ProbeActiveThisFrame(probe)) return;

    // CHUNK1 progressive bake: count this probe's converged frames (once per probe — the ray-0 thread). When the
    // counter reaches convergeTarget the probe freezes (ProbeActiveThisFrame returns false above). GPU-owned, no
    // CPU readback. Only in bake mode so the live path's RayData is byte-identical.
    if (Params4.x > 0.5 && ray == 0u) ProbeBakeState[probe] = ProbeBakeState[probe] + 1u;

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
        // P2.4 classification signal: a BACKFACE hit (ray left the probe through the SOLID side of a surface)
        // means the probe is (partly) buried in geometry. ShadeHit reports it from dot(geometricNormal,rayDir)
        // — convention-independent, NOT DXR CommittedTriangleFrontFace (inverted vs this engine's winding).
        // Encode it as a NEGATIVE distance so the classify pass counts backfaces per probe (CSDepth abs()'s it
        // for the moments). Front/sky hits keep a positive distance.
        bool backface;
        radiance = ShadeHit(q, dir, backface);
        dist = backface ? -max(q.CommittedRayT(), 1e-4) : q.CommittedRayT();
    } else {
        radiance = Irradiance.SampleLevel(LinearClamp, dir, 0).rgb;   // sky
        dist = Params1.x;                                              // far (open)
    }
    RayData[id] = float4(radiance, dist);
}
