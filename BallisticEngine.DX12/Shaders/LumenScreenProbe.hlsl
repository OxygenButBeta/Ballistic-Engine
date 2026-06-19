// Lumen V2 — Sıra 1: SCREEN-SPACE RADIANCE PROBES (the published Lumen final-gather front end).
//
// WHY: the baseline CSTrace (LumenGi.hlsl) shoots a few cosine rays from EVERY full-res G-buffer pixel — ~2M
// trace points at 1080p, the dominant Lumen cost (~3.2ms measured on Bistro interior). Lumen instead places a
// SPARSE grid of radiance probes (one per 16x16 screen tile, ~8K probes), traces MANY rays per probe into an
// octahedral radiance atlas, then INTERPOLATES that atlas at full-res per pixel (bilateral, depth+normal aware).
// Far fewer trace points + more rays each = lower variance AND lower cost. This file is that probe front end;
// it writes the SAME `indirect` irradiance E buffer the per-pixel trace used to, so the downstream denoise +
// combine + probe-temporal chain is untouched (byte-identical contract).
//
// THREE passes (compute):
//   CSPlace    — 1 thread / probe. Pick a representative pixel in the probe's screen tile (the one closest to the
//                tile center with valid geometry), store its world pos + normal + depth into the probe header.
//   CSTrace    — 1 thread / (probe, ray). Trace one octahedral-distributed hemisphere ray from the probe using
//                the EXACT LumenGi hierarchy (screen-trace → HW RT → card sample → sky → distance falloff), write
//                the incoming radiance into the probe's octahedral radiance tile.
//   CSIntegrate— 1 thread / full-res pixel. Reconstruct world pos+normal, gather the 4 nearest probes (bilateral:
//                screen-bilinear × depth × normal × validity), sample each probe's octahedral tile along the
//                pixel normal hemisphere (cosine-weighted), write cosine-weighted irradiance E into `indirect`.
//
// Bindings mirror LumenGi.CSTrace so the ray hierarchy is shared code. Octahedral mapping: standard
// equal-area-ish octahedron unwrap of the unit sphere; we only store the upper hemisphere implicitly by
// sampling cosine-weighted directions around the probe normal, but the tile stores a FULL-sphere octahedron so
// a pixel whose normal differs from the probe normal can still read a plausible direction (Lumen stores the full
// sphere per screen probe for exactly this reason).

RaytracingAccelerationStructure Scene : register(t0);
Texture2D<float>  Depth     : register(t1);
Texture2D<float4> Normal    : register(t2);
Texture2D<float4> Material  : register(t3);
Texture2D<float4> SceneColor: register(t4);
TextureCube SkyIrradiance   : register(t5);
TextureCube SkyPrefilter    : register(t6);

cbuffer ProbeConstants : register(b0) {
    float4x4 InvViewProj;
    float4x4 ViewProj;
    float3 CameraPos;   float Intensity;
    float2 FullTexel;   float RayCount;   float FrameIndex;     // FullTexel = 1/full-res
    float NormalBias;   float MaxRayDist; float UseCards;       float ScreenSteps;
    float SkyIntensity; float UseSky;     float UseScreenTrace; float ScreenRange;
    float FalloffDist;  float ProbeTile;  float ProbeStride;    float OctSize;       // ProbeStride=16; OctSize=8 (8x8 tile)
    uint  ProbesX;      uint ProbesY;     uint FullW;           uint FullH;
};
cbuffer ProbeSun : register(b1) {
    float3 SunDir;   float SunBias;
    float3 SunColor; float LightCount;
};

// Probe header: world pos (xyz) + valid flag (w), normal (xyz) + linear depth (w). One per probe.
struct ProbeHeader { float4 PosValid; float4 NormalDepth; };
RWStructuredBuffer<ProbeHeader> ProbeHeaders : register(u0);   // CSPlace writes, CSTrace/CSIntegrate read
RWTexture2D<float4> ProbeAtlas : register(u1);                 // octahedral radiance atlas (ProbesX*OctSize wide)
RWTexture2D<float4> Indirect   : register(u2);                 // OUT (CSIntegrate): incoming irradiance E

SamplerState LinearClamp : register(s0);
SamplerState LinearWrap  : register(s1);

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
struct LumenInstanceMeta { uint TriOffset, TriCount, ClusterOffset, ClusterCount; float4x4 World; };
StructuredBuffer<GpuMaterial>       GpuMaterials : register(t7);
StructuredBuffer<RtInstance>        RtInstances  : register(t8);
StructuredBuffer<GpuLight>          Lights       : register(t9);
StructuredBuffer<float4>            CardRadiance : register(t10);
StructuredBuffer<LumenInstanceMeta> InstanceMeta : register(t11);
StructuredBuffer<uint>              TriToCluster : register(t12);

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
float3 WorldFromUvDepth(float2 uv, float depth) {
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    return w.xyz / w.w;
}

// --- Octahedral map: unit direction (full sphere) <-> [0,1]^2 ---
float2 OctEncode(float3 n) {
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    float2 e = n.xy;
    if (n.z < 0.0) e = (1.0 - abs(e.yx)) * float2(e.x >= 0.0 ? 1.0 : -1.0, e.y >= 0.0 ? 1.0 : -1.0);
    return e * 0.5 + 0.5;
}
float3 OctDecode(float2 f) {
    f = f * 2.0 - 1.0;
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}

float Visibility(float3 origin, float3 N, float3 dir, float maxDist) {
    RayDesc ray; ray.Origin = origin + N * max(SunBias, 0.002); ray.Direction = dir; ray.TMin = 0.02; ray.TMax = maxDist;
    RayQuery<RAY_FLAG_ACCEPT_FIRST_HIT_AND_END_SEARCH | RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray); q.Proceed();
    return q.CommittedStatus() == COMMITTED_TRIANGLE_HIT ? 0.0 : 1.0;
}

float3 ShadeHit(uint instId, uint prim, float2 bary2, float3x4 o2w, float3 rayDir, float3 hitPos) {
    RtInstance inst = RtInstances[instId];
    Buffer<uint>             indices = ResourceDescriptorHeap[inst.IndexIdx];
    StructuredBuffer<float3> normals = ResourceDescriptorHeap[inst.NormalIdx];
    StructuredBuffer<float2> uvs     = ResourceDescriptorHeap[inst.UvIdx];
    StructuredBuffer<uint>   triMat  = ResourceDescriptorHeap[inst.TriMatIdx];
    uint i0 = indices[prim * 3 + 0], i1 = indices[prim * 3 + 1], i2 = indices[prim * 3 + 2];
    float3 bary = float3(1.0 - bary2.x - bary2.y, bary2.x, bary2.y);
    float3 nObj = normalize(normals[i0] * bary.x + normals[i1] * bary.y + normals[i2] * bary.z);
    float3 Ng = normalize(mul((float3x3)o2w, nObj));
    if (dot(Ng, rayDir) > 0.0) Ng = -Ng;
    float2 uv = uvs[i0] * bary.x + uvs[i1] * bary.y + uvs[i2] * bary.z;
    GpuMaterial m = GpuMaterials[triMat[prim]];
    Texture2D diffuseMap = ResourceDescriptorHeap[m.DiffuseIdx];
    float3 albedo = min(diffuseMap.SampleLevel(LinearWrap, uv, 0).rgb * m.BaseColorFactor.rgb, 0.95.xxx);
    float3 emissive = 0.0.xxx;
    if (m.HasEmissive > 0.5) {
        Texture2D emissiveMap = ResourceDescriptorHeap[m.EmissiveIdx];
        emissive = emissiveMap.SampleLevel(LinearWrap, uv, 0).rgb * m.EmissiveFactor.rgb;
    }
    float3 sunDir = normalize(SunDir);
    float ndl = saturate(dot(Ng, sunDir));
    float3 sun = (ndl > 0.0) ? SunColor * ndl * Visibility(hitPos, Ng, sunDir, MaxRayDist) : 0.0.xxx;
    float3 punctual = 0.0.xxx;
    int n = min((int)LightCount, 32);
    [loop] for (int i = 0; i < n; i++) {
        GpuLight L = Lights[i];
        float3 toL = L.PosRange.xyz - hitPos;
        float dist = length(toL);
        if (dist > L.PosRange.w || dist < 1e-4) continue;
        float3 Ld = toL / dist;
        float nl = saturate(dot(Ng, Ld));
        if (nl <= 0.0) continue;
        float t = saturate(1.0 - pow(dist / L.PosRange.w, 4.0));
        float3 rad = L.Color.rgb * (t * t / max(dist * dist, 1e-4));
        if (L.Color.w >= 0.5) {
            float cosA = dot(-Ld, normalize(L.DirCosOuter.xyz));
            float cone = saturate((cosA - L.DirCosOuter.w) / max(L.Extra.x - L.DirCosOuter.w, 1e-4));
            if (cone <= 0.0) continue;
            rad *= cone * cone;
        }
        punctual += rad * nl * Visibility(hitPos, Ng, Ld, dist);
    }
    return albedo * (sun + punctual) + emissive;
}

bool ScreenTrace(float3 origin, float3 dir, out float3 radiance) {
    radiance = 0.0.xxx;
    float range = min(ScreenRange, MaxRayDist);
    int steps = max((int)ScreenSteps, 1);
    float stepLen = range / (float)steps;
    float3 p = origin + dir * stepLen;
    [loop] for (int i = 0; i < steps; i++, p += dir * stepLen) {
        float4 clip = mul(float4(p, 1.0), ViewProj);
        if (clip.w <= 0.0) return false;
        float3 ndc = clip.xyz / clip.w;
        float2 uv = ndc.xy * float2(0.5, -0.5) + 0.5;
        if (any(uv < 0.0) || any(uv > 1.0)) return false;
        float sceneDepth = Depth.SampleLevel(LinearClamp, uv, 0).r;
        if (sceneDepth >= 1.0) continue;
        float3 rayWorld = WorldFromUvDepth(uv, ndc.z);
        float3 sceneWorld = WorldFromUvDepth(uv, sceneDepth);
        float rayZ = length(rayWorld - CameraPos);
        float sceneZ = length(sceneWorld - CameraPos);
        float diff = rayZ - sceneZ;
        if (diff > 0.01 * rayZ && diff < stepLen * 2.0) {
            if (length(sceneWorld - origin) > range) return false;
            radiance = SceneColor.SampleLevel(LinearClamp, uv, 0).rgb;
            return true;
        }
        if (diff >= stepLen * 2.0) return false;
    }
    return false;
}

// Resolve one ray's incoming radiance with the shared LumenGi hierarchy. `falloffApply` lets the integrate path
// skip falloff (it's baked at trace time here).
float3 TraceRay(float3 origin, float3 dir) {
    float3 rad;
    if (UseScreenTrace > 0.5 && ScreenTrace(origin, dir, rad)) return rad;
    RayDesc ray; ray.Origin = origin; ray.Direction = dir; ray.TMin = 0.02; ray.TMax = MaxRayDist;
    RayQuery<RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray); q.Proceed();
    if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
        float hitT = q.CommittedRayT();
        float falloff = (FalloffDist > 0.01) ? exp2(-hitT / FalloffDist) : 1.0;
        if (UseCards > 0.5) {
            uint inst = q.CommittedInstanceID();
            LumenInstanceMeta meta = InstanceMeta[inst];
            uint record = meta.ClusterOffset + TriToCluster[meta.TriOffset + q.CommittedPrimitiveIndex()];
            return CardRadiance[record].rgb * falloff;
        } else {
            float3 hitPos = origin + dir * hitT;
            return ShadeHit(q.CommittedInstanceID(), q.CommittedPrimitiveIndex(),
                            q.CommittedTriangleBarycentrics(), q.CommittedObjectToWorld3x4(), dir, hitPos) * falloff;
        }
    } else if (UseSky > 0.5) {
        return SkyIrradiance.SampleLevel(LinearClamp, dir, 0).rgb * SkyIntensity;
    }
    return 0.0.xxx;
}

// ===== CSPlace: choose a representative pixel per probe tile =====
[numthreads(8, 8, 1)]
void CSPlace(uint3 dtid : SV_DispatchThreadID) {
    uint2 probe = dtid.xy;
    if (probe.x >= ProbesX || probe.y >= ProbesY) return;
    uint pidx = probe.y * ProbesX + probe.x;

    uint stride = (uint)ProbeStride;
    // Scan the tile for the valid pixel nearest the tile center (Lumen places probes on real geometry, not a
    // fixed grid pixel that might land on a depth discontinuity / sky).
    int2 tileBase = int2(probe) * (int)stride;
    int2 center = tileBase + (int)stride / 2;
    float bestDist = 1e9; bool found = false;
    float3 bestPos = 0; float3 bestN = 0; float bestDepth = 1.0;
    [loop] for (uint sy = 0; sy < stride; sy += 2)
    [loop] for (uint sx = 0; sx < stride; sx += 2) {
        int2 px = tileBase + int2(sx, sy);
        if (px.x >= (int)FullW || px.y >= (int)FullH) continue;
        float2 uv = (float2(px) + 0.5) * FullTexel;
        float d = Depth.SampleLevel(LinearClamp, uv, 0).r;
        if (d >= 1.0) continue;
        float3 nW = Normal.SampleLevel(LinearClamp, uv, 0).rgb * 2.0 - 1.0;
        if (dot(nW, nW) < 0.1) continue;
        float dist = dot(float2(px - center), float2(px - center));
        if (dist < bestDist) {
            bestDist = dist; found = true;
            bestPos = WorldFromUvDepth(uv, d);
            bestN = normalize(nW);
            bestDepth = d;
        }
    }
    ProbeHeaders[pidx].PosValid    = float4(bestPos, found ? 1.0 : 0.0);
    ProbeHeaders[pidx].NormalDepth = float4(bestN, bestDepth);
}

// ===== CSTrace: one ray per (probe, octahedral cell) =====
[numthreads(8, 8, 1)]
void CSProbeTrace(uint3 dtid : SV_DispatchThreadID) {
    uint oct = (uint)OctSize;
    uint2 cell = uint2(dtid.x % oct, dtid.y);            // dtid.x = probeX*oct + cellX ; dtid.y handled below
    // Layout: dispatch X = ProbesX*OctSize, dispatch Y = ProbesY*OctSize.
    uint2 atlasPx = dtid.xy;
    uint2 probe = atlasPx / oct;
    uint2 lcell = atlasPx % oct;
    if (probe.x >= ProbesX || probe.y >= ProbesY) return;
    uint pidx = probe.y * ProbesX + probe.x;

    ProbeHeader h = ProbeHeaders[pidx];
    if (h.PosValid.w < 0.5) { ProbeAtlas[atlasPx] = float4(0, 0, 0, 0); return; }
    float3 P = h.PosValid.xyz;
    float3 N = h.NormalDepth.xyz;

    // Octahedral cell center → a full-sphere direction. Jitter inside the cell for AA across frames. The tile
    // stores the FULL SPHERE of incoming radiance (Lumen screen probes are full-sphere): the integrate then picks
    // the pixel's own hemisphere by cosine-weighting against the PIXEL normal, so a pixel whose normal differs
    // from the probe normal (a silhouette / curved surface inside the tile) still reads valid directions. Tracing
    // only the probe-N hemisphere (the previous approach) left the pixel-hemisphere cells that fall in the probe's
    // BACK hemisphere empty → energy loss + the measured darkening/grain.
    float jitter = Hash(pidx * 2654435761u ^ (lcell.x * 73856093u) ^ (lcell.y * 19349663u) ^ (uint)FrameIndex);
    float2 octUv = (float2(lcell) + float2(frac(jitter * 1.61803), frac(jitter * 2.41421))) / float(oct);
    float3 dir = OctDecode(octUv);

    float3 origin = P + N * NormalBias;
    float3 rad = TraceRay(origin, dir);
    ProbeAtlas[atlasPx] = float4(Sanitize(rad), 1.0);
}

// ===== CSIntegrate: per full-res pixel, gather 4 nearest probes' octahedral radiance, cosine-weighted =====
// Sample a probe's octahedral tile for the irradiance arriving at a pixel with normal Npix: integrate the tile's
// full sphere weighted by cos(theta) over the pixel hemisphere. We approximate with the cosine-importance the
// tile was traced under (the tile already only has hemisphere-around-probe-N radiance), reading along Npix.
float3 SampleProbeTile(uint2 probe, float3 Npix) {
    uint oct = (uint)OctSize;
    // Cosine-weighted integral over the hemisphere around Npix, sampled from the octahedral tile. Few-tap: walk
    // the tile cells, weight each stored direction by max(0, dot(dir, Npix)).
    float3 acc = 0.0.xxx; float wsum = 0.0;
    [loop] for (uint cy = 0; cy < oct; cy++)
    [loop] for (uint cx = 0; cx < oct; cx++) {
        float2 octUv = (float2(cx, cy) + 0.5) / float(oct);
        float3 dir = OctDecode(octUv);
        float w = max(dot(dir, Npix), 0.0);
        if (w <= 0.0) continue;
        float4 t = ProbeAtlas[uint2(probe.x * oct + cx, probe.y * oct + cy)];
        acc += t.rgb * w; wsum += w;
    }
    return wsum > 1e-4 ? acc / wsum : 0.0.xxx;
}

[numthreads(8, 8, 1)]
void CSIntegrate(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    if (px.x >= FullW || px.y >= FullH) return;
    float2 uv = (float2(px) + 0.5) * FullTexel;
    float depth = Depth.SampleLevel(LinearClamp, uv, 0).r;
    if (depth >= 1.0) { Indirect[px] = float4(0, 0, 0, 1); return; }
    float3 nW = Normal.SampleLevel(LinearClamp, uv, 0).rgb * 2.0 - 1.0;
    if (dot(nW, nW) < 0.1) { Indirect[px] = float4(0, 0, 0, 1); return; }
    float3 Npix = normalize(nW);
    float3 worldPos = WorldFromUvDepth(uv, depth);

    // 4 nearest probes (the 2x2 enclosing the pixel in probe space). A probe `p` represents the screen position
    // tileCenter = p*stride + stride/2, so the pixel's coordinate in probe-grid space is (px - stride/2)/stride;
    // the enclosing 2x2 is floor()..+1. Bilateral weight: screen bilinear × depth similarity × normal similarity ×
    // probe validity — rejects a probe on a different surface (silhouette) but with a guaranteed fallback so a
    // pixel whose 2x2 all reject still gets the BEST available probe instead of a black hole (the measured holes).
    float stride = ProbeStride;
    float2 probeF = (float2(px) - stride * 0.5) / stride;
    float2 base = floor(probeF);
    float2 f = frac(probeF);

    float3 acc = 0.0.xxx; float wsum = 0.0;
    // Fallback bookkeeping: track the single highest-validity probe so a fully-rejected neighbourhood still
    // resolves to the best nearby probe (no black hole — the measured failure).
    float3 bestRad = 0.0.xxx; float bestW = -1.0; bool anyValid = false;
    int2 ibase = int2(base);
    // 4x4 neighbourhood (the 2x2 enclosing + a 1-probe skirt) so a pixel near a tile whose nearest probes all
    // reject (silhouette / sparse placement) still finds a coherent probe — kills the 16px blob holes that a
    // strict 2x2 left on ceilings and large flat regions.
    [unroll] for (int dy = -1; dy <= 2; dy++)
    [unroll] for (int dx = -1; dx <= 2; dx++) {
        int2 pc = ibase + int2(dx, dy);
        if (pc.x < 0 || pc.y < 0 || pc.x >= (int)ProbesX || pc.y >= (int)ProbesY) continue;
        uint pidx = pc.y * ProbesX + pc.x;
        ProbeHeader h = ProbeHeaders[pidx];
        if (h.PosValid.w < 0.5) continue;
        anyValid = true;
        // Spatial: gaussian on the probe-space distance from the pixel (probeF). Wider than bilinear so the skirt
        // probes blend smoothly instead of a hard 2x2 cutoff.
        float2 d2 = (float2(pc) - probeF);
        float wSpatial = exp(-dot(d2, d2) * 0.7);
        float wDepth = 1.0 / (1.0 + abs(h.NormalDepth.w - depth) * 1500.0);
        float wNormal = pow(saturate(dot(h.NormalDepth.xyz, Npix) * 0.5 + 0.5), 2.0);
        float w = wSpatial * wDepth * wNormal + 1e-5;
        float3 rad = SampleProbeTile((uint2)pc, Npix);
        acc += rad * w; wsum += w;
        float fw = wSpatial * wDepth * (saturate(dot(h.NormalDepth.xyz, Npix)) + 0.1);
        if (fw > bestW) { bestW = fw; bestRad = rad; }
    }
    float3 E;
    if (wsum > 1e-3) E = acc / wsum;
    else if (anyValid) E = bestRad;          // all weights collapsed → take the best single probe (no black hole)
    else E = 0.0.xxx;                        // genuinely no valid probe nearby (rare: tile fully sky/invalid)
    Indirect[px] = float4(Sanitize(E * Intensity), depth);   // depth in .a for the downstream probe-temporal + history copy
}
