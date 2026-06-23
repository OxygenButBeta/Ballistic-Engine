// Lumen FAZ 6 — SCREEN-PROBE GATHER (the first VISIBLE integrated Lumen GI).
//
// This is AuroraScreenProbe.hlsl's spine (Place → Trace → Filter → SH → Integrate) with EXACTLY ONE change: in
// CSProbeTrace, each octahedral hemisphere ray is resolved by the shared LumenTrace abstraction (LumenTrace.hlsl:
// HW TLAS RayQuery OR software global-SDF sphere-march → sample the LIT surface cache FinalLighting) instead of
// Aurora's screen-trace→TLAS→card-radiance hierarchy. The probe math (placement, octahedral atlas, probe-space
// bilateral filter, SH projection, per-pixel cosine integrate, temporal EMA) is RADIANCE-AGNOSTIC and copied
// verbatim — the only difference is WHERE a ray's incoming radiance comes from.
//
// THREE+ passes (compute):
//   CSPlace      — 1 thread / probe. Pick a representative pixel in the probe's screen tile (valid geometry,
//                  nearest tile center), store world pos + normal + depth into the probe header.
//   CSProbeTrace — 1 thread / (probe, oct cell). Trace one octahedral hemisphere ray via LumenTrace; EMA over the
//                  previous frame's accumulated atlas (cache-space temporal accumulation).
//   CSProbeFilter— probe-space joint-bilateral blend of each atlas cell with neighbouring probes (blob fix).
//   CSProbeSH    — project each probe's filtered oct tile into 9 RGB cosine-convolved irradiance SH coeffs.
//   CSIntegrate  — 1 thread / full-res pixel. Gather the nearest probes (bilateral) → evaluate the SH in the pixel
//                  normal → write cosine-weighted irradiance E into `indirect`.
//
// The b0 cbuffer BEGINS with LUMEN_TRACE_PARAMS (the include reads them by name) + the probe params after them.
// The trace's resources are bound: TLAS t0, Cards t1 / Pages t2 / InstanceRanges t3 (StructuredBuffers), and the
// clipmap Texture3D + FinalLighting Texture2D (+ sky cube) resolve from ResourceDescriptorHeap[] via the bindless
// indices in the CB (HeapDirectlyIndexed root sig — the SAME pattern as the FAZ 5 trace debug view).
//
// Driver rules obeyed: NaN scrub = ternary component-select (never lerp(v,0,flag)); every divide guards its denom;
// saturate before pow/sqrt. The temporal EMA reuses Aurora's (already driver-safe) form.

RaytracingAccelerationStructure Scene : register(t0);

// Struct types the LumenTrace include needs — declared FIRST (guarded with LT_STRUCTS_DEFINED so the include
// skips its own re-declaration). IDENTICAL layout to LumenTrace.hlsl / Dx12LumenCardScene.
#define LT_STRUCTS_DEFINED
struct LtCard {
    float3 Origin; uint  PageId;
    float3 AxisX;  float ExtentX;
    float3 AxisY;  float ExtentY;
    float3 AxisZ;  float ExtentZ;
};
struct LtPage {
    uint AtlasOffsetX, AtlasOffsetY;
    uint SizeX, SizeY;
    uint CardId, ResLevel, Pad0, Pad1;
};
struct LtInstanceRange { uint Offset; uint Count; };

cbuffer ProbeConstants : register(b0) {
    // --- the LumenTrace parameter block (MUST be first; the include reads these by name) ---
    float3 LtClipOrigin;   float LtVoxelSize;
    float3 LtCamPosUnused; float LtClipHalfExtent;
    uint   LtClipResX, LtClipResY, LtClipResZ; float LtMaxTraceDist;
    uint   LtAtlasSize, LtCardCount, LtInstanceCount, LtFinalReadIdx;
    uint   LtClipmapIdx, LtFinalValid, LtHasTlas, LtSkyIdx;
    float  LtSkyIntensity, LtUseSky, LtSurfBias, LtPad0;
    // FAZ 11 — spatial card grid (matches LUMEN_TRACE_PARAMS tail; LtCgEnabled=0 = linear scan)
    float3 LtCgOrigin;     float LtCgEnabled;
    float3 LtCgCellSize;   uint  LtCgDim;
    uint   LtCgCellIdx, LtCgIndexIdx, LtCgPad0, LtCgPad1;
    // --- probe params (after the trace block) ---
    float4x4 InvViewProj;
    float4x4 ViewProj;
    float3 CameraPos;   float Intensity;
    float2 FullTexel;   float RayCount;   float FrameIndex;
    float NormalBias;   float MaxRayDist; float PreferSW;       float ProbeStride;
    uint  ProbesX;      uint ProbesY;     uint FullW;           uint FullH;
    float HistoryValid; float ProbeEma;   float OctSize;        float UseSH;
    float ProbeFilterRadius; float SpPad0; float SpPad1;        float SpPad2;
    // --- FAZ 7 radiance-cache params (mirror RC_PARAMS in LumenRadianceCacheSample.hlsl — the include reads them by name) ---
    float3 RcOrigin;        float RcProbeSpacing;
    uint   RcGridRes;       uint  RcAtlasInProbes; uint RcProbeRes; uint RcFinalProbeRes;
    float  RcTraceStop;     float RcEnabled;       uint RcIndirIdx; uint RcRadIdx;
    uint   RcHitIdx;        uint  RcMarkIdx;       float RcSampleBias;  float RcPad0;
};

StructuredBuffer<LtCard>          Cards          : register(t1);
StructuredBuffer<LtPage>          Pages          : register(t2);
StructuredBuffer<LtInstanceRange> InstanceRanges : register(t3);
Texture2D<float>  Depth     : register(t4);   // G-buffer depth
Texture2D<float4> Normal    : register(t5);   // G-buffer world normal, packed N*0.5+0.5

SamplerState LinearClamp : register(s0);
SamplerState LinearWrap  : register(s1);

// Probe header: world pos (xyz) + valid flag (w), normal (xyz) + linear depth (w). One per probe.
struct ProbeHeader { float4 PosValid; float4 NormalDepth; };
RWStructuredBuffer<ProbeHeader> ProbeHeaders : register(u0);   // CSPlace writes, CSProbeTrace/CSIntegrate read
RWTexture2D<float4> ProbeAtlas : register(u1);                 // octahedral radiance atlas (ProbesX*OctSize wide)
RWTexture2D<float4> Indirect   : register(u2);                 // OUT (CSIntegrate): incoming irradiance E
RWTexture2D<float4> ProbeAtlasFiltered : register(u3);        // probe-space spatial-filtered atlas (the integrate reads this)
RWStructuredBuffer<float4> ProbeSH : register(u4);            // 7 float4 / probe (9 RGB cosine-convolved irradiance SH)
Texture2D<float4>   ProbeAtlasHistory : register(t13);       // previous frame's accumulated atlas (EMA source)
StructuredBuffer<ProbeHeader> ProbeHeadersPrev : register(t16);// previous frame's probe headers (reproject reject)

#include "Lumen/LumenTrace.hlsl"

static const float PI = 3.14159265359;

float3 Sanitize(float3 v) {   // ternary component-select — NEVER lerp(v,0,flag) (NaN*0==NaN; proven AMD bug)
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

// FAZ 7 — the world-space radiance-cache sampling helper (SampleRadianceCacheInterpolated + RcMarkCell). Needs
// OctEncode/OctDecode (above) + LinearClamp (s0) + the Rc* CB fields (above). Source-prepended like LumenTrace.
#include "Lumen/LumenRadianceCacheSample.hlsl"

// --- Order-2 (9-coefficient) real spherical harmonics, evaluated for a direction. Standard basis. ---
void ShBasis(float3 d, out float sh[9]) {
    sh[0] = 0.282095;
    sh[1] = 0.488603 * d.y;
    sh[2] = 0.488603 * d.z;
    sh[3] = 0.488603 * d.x;
    sh[4] = 1.092548 * d.x * d.y;
    sh[5] = 1.092548 * d.y * d.z;
    sh[6] = 0.315392 * (3.0 * d.z * d.z - 1.0);
    sh[7] = 1.092548 * d.x * d.z;
    sh[8] = 0.546274 * (d.x * d.x - d.y * d.y);
}
static const float ShCosA0 = 3.141593;
static const float ShCosA1 = 2.094395;
static const float ShCosA2 = 0.785398;

void StoreProbeSH(uint p, float3 c[9]) {
    uint b = p * 7u;
    ProbeSH[b + 0] = float4(c[0], c[1].x);
    ProbeSH[b + 1] = float4(c[1].yz, c[2].xy);
    ProbeSH[b + 2] = float4(c[2].z, c[3]);
    ProbeSH[b + 3] = float4(c[4], c[5].x);
    ProbeSH[b + 4] = float4(c[5].yz, c[6].xy);
    ProbeSH[b + 5] = float4(c[6].z, c[7]);
    ProbeSH[b + 6] = float4(c[8], 0.0);
}
void LoadProbeSH(uint p, out float3 c[9]) {
    uint b = p * 7u;
    float4 a0 = ProbeSH[b + 0], a1 = ProbeSH[b + 1], a2 = ProbeSH[b + 2], a3 = ProbeSH[b + 3];
    float4 a4 = ProbeSH[b + 4], a5 = ProbeSH[b + 5], a6 = ProbeSH[b + 6];
    c[0] = a0.xyz; c[1] = float3(a0.w, a1.xy); c[2] = float3(a1.zw, a2.x);
    c[3] = a2.yzw; c[4] = a3.xyz; c[5] = float3(a3.w, a4.xy);
    c[6] = float3(a4.zw, a5.x); c[7] = a5.yzw; c[8] = a6.xyz;
}
float3 EvalProbeSH(float3 c[9], float3 N) {
    float sh[9]; ShBasis(N, sh);
    float3 E = c[0] * (ShCosA0 * sh[0]);
    E += (c[1] * sh[1] + c[2] * sh[2] + c[3] * sh[3]) * ShCosA1;
    E += (c[4] * sh[4] + c[5] * sh[5] + c[6] * sh[6] + c[7] * sh[7] + c[8] * sh[8]) * ShCosA2;
    return max(E / PI, 0.0.xxx);
}

// ===== CSPlace: choose a representative pixel per probe tile =====
[numthreads(8, 8, 1)]
void CSPlace(uint3 dtid : SV_DispatchThreadID) {
    uint2 probe = dtid.xy;
    if (probe.x >= ProbesX || probe.y >= ProbesY) return;
    uint pidx = probe.y * ProbesX + probe.x;

    uint stride = (uint)ProbeStride;
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

// ===== CSProbeTrace: one LUMEN ray per (probe, octahedral cell) =====
[numthreads(8, 8, 1)]
void CSProbeTrace(uint3 dtid : SV_DispatchThreadID) {
    uint oct = (uint)OctSize;
    uint2 atlasPx = dtid.xy;
    uint2 probe = atlasPx / oct;
    uint2 lcell = atlasPx % oct;
    if (probe.x >= ProbesX || probe.y >= ProbesY) return;
    uint pidx = probe.y * ProbesX + probe.x;

    ProbeHeader h = ProbeHeaders[pidx];
    if (h.PosValid.w < 0.5) { ProbeAtlas[atlasPx] = float4(0, 0, 0, 0); return; }
    float3 P = h.PosValid.xyz;
    float3 N = h.NormalDepth.xyz;

    // Octahedral cell center → a full-sphere direction (jittered per-frame for AA). FULL-sphere tile: the integrate
    // picks the pixel's own hemisphere by cosine-weighting against the PIXEL normal, so a silhouette/curved pixel
    // inside the tile still reads valid directions (the published Aurora screen-probe trick).
    float jitter = Hash(pidx * 2654435761u ^ (lcell.x * 73856093u) ^ (lcell.y * 19349663u) ^ (uint)FrameIndex);
    float2 octUv = (float2(lcell) + float2(frac(jitter * 1.61803), frac(jitter * 2.41421))) / float(oct);
    float3 dir = OctDecode(octUv);

    // === THE ONLY CHANGE vs AuroraScreenProbe: resolve incoming radiance through LumenTrace (HW TLAS or SW SDF →
    // surface-cache FinalLighting) instead of Aurora's screen-trace/card hierarchy. ===
    float3 origin = P + N * max(LtSurfBias, NormalBias);
    bool preferSW = PreferSW > 0.5;
    float maxDist = MaxRayDist > 0.0 ? MaxRayDist : (LtMaxTraceDist > 0.0 ? LtMaxTraceDist : 1e4);

    // FAZ 7 NEAR/FAR SPLIT: when the radiance cache is on, the screen probe traces only SHORT — clamp maxDist to the
    // cache's trace-stop (= probeSpacing*sqrt(3), the clipmap cell space-diagonal). On a MISS within that short
    // distance, the FAR radiance comes from the cache instead (sampled below). This is the noise reducer: short rays
    // are cheap + low-variance; the far field is the cache's smooth, temporally-accumulated job.
    bool rcOn = RcEnabled > 0.5;
    float traceMax = rcOn ? min(maxDist, RcTraceStop) : maxDist;
    LumenTraceResult tr = LumenTrace(origin, dir, traceMax, preferSW);
    float3 rad = Sanitize(tr.Radiance);

    // MISS within the short distance → mark the covering cell (NEXT frame's allocate) + ADD the cache's far radiance.
    [branch] if (rcOn && !tr.Hit) {
        RcMarkCell(P);
        rad += SampleRadianceCacheInterpolated(P, dir);
        rad = Sanitize(rad);
    }

    // TEMPORAL ACCUMULATION (cache-space EMA, reproject-rejected). Probe atlas cells are screen-tile-anchored; on a
    // static/slow camera the same probe maps to the same cell across frames → a straight per-cell EMA is correct.
    // On a fast camera the cell may now cover DIFFERENT geometry — reject (take fresh) when the previous probe at
    // this cell sat far from this surface (disocclusion). v1: no variance-guided adaptive ray (keeps the path
    // deterministic + simple; the probe-space filter already removes the bulk of the spatial variance).
    [branch] if (HistoryValid > 0.5) {
        ProbeHeader hp = ProbeHeadersPrev[pidx];
        float posDiff = distance(hp.PosValid.xyz, P);
        bool sameSurface = hp.PosValid.w > 0.5 && posDiff < max(0.5, length(P - CameraPos) * 0.03);
        if (sameSurface) {
            float3 prev = Sanitize(ProbeAtlasHistory[atlasPx].rgb);
            rad = lerp(prev, rad, saturate(ProbeEma));   // low alpha → strong accumulation
        }
    }
    ProbeAtlas[atlasPx] = float4(rad, 1.0);
}

// ===== CSProbeFilter: SPATIAL filter of the probe atlas in PROBE space (the proper blob fix) =====
[numthreads(8, 8, 1)]
void CSProbeFilter(uint3 dtid : SV_DispatchThreadID) {
    uint oct = (uint)OctSize;
    uint2 atlasPx = dtid.xy;
    uint2 probe = atlasPx / oct;
    uint2 lcell = atlasPx % oct;
    if (probe.x >= ProbesX || probe.y >= ProbesY) return;
    uint pidx = probe.y * ProbesX + probe.x;
    ProbeHeader hc = ProbeHeaders[pidx];
    if (hc.PosValid.w < 0.5) { ProbeAtlasFiltered[atlasPx] = ProbeAtlas[atlasPx]; return; }
    float3 Pc = hc.PosValid.xyz; float3 Nc = hc.NormalDepth.xyz;

    int r = (int)clamp(ProbeFilterRadius, 1.0, 3.0);
    float3 acc = 0.0.xxx; float wsum = 0.0;
    [loop] for (int dy = -r; dy <= r; dy++)
    [loop] for (int dx = -r; dx <= r; dx++) {
        int2 np = int2(probe) + int2(dx, dy);
        if (np.x < 0 || np.y < 0 || np.x >= (int)ProbesX || np.y >= (int)ProbesY) continue;
        uint nidx = np.y * ProbesX + np.x;
        ProbeHeader hn = ProbeHeaders[nidx];
        if (hn.PosValid.w < 0.5) continue;
        float wS = exp(-float(dx*dx + dy*dy) * 0.4);
        float posD = distance(hn.PosValid.xyz, Pc);
        float wP = exp(-posD * posD * 0.5);
        float wN = pow(saturate(dot(hn.NormalDepth.xyz, Nc)), 8.0);
        float w = wS * wP * wN + 1e-5;
        acc += ProbeAtlas[uint2(np.x * oct + lcell.x, np.y * oct + lcell.y)].rgb * w;
        wsum += w;
    }
    ProbeAtlasFiltered[atlasPx] = float4(wsum > 1e-4 ? acc / wsum : ProbeAtlas[atlasPx].rgb, 1.0);
}

// ===== CSProbeSH: project each probe's FILTERED octahedral tile into 9 RGB SH coefficients (1 thread / probe) =====
[numthreads(64, 1, 1)]
void CSProbeSH(uint3 dtid : SV_DispatchThreadID) {
    uint pidx = dtid.x;
    if (pidx >= ProbesX * ProbesY) return;
    uint2 probe = uint2(pidx % ProbesX, pidx / ProbesX);
    uint oct = (uint)OctSize;

    float3 c[9];
    [unroll] for (uint k = 0; k < 9; k++) c[k] = 0.0.xxx;
    ProbeHeader h = ProbeHeaders[pidx];
    if (h.PosValid.w < 0.5) { StoreProbeSH(pidx, c); return; }

    float wsum = 0.0;
    [loop] for (uint cy = 0; cy < oct; cy++)
    [loop] for (uint cx = 0; cx < oct; cx++) {
        float2 octUv = (float2(cx, cy) + 0.5) / float(oct);
        float3 dir = OctDecode(octUv);
        float3 rad = ProbeAtlasFiltered[uint2(probe.x * oct + cx, probe.y * oct + cy)].rgb;
        float3 un = float3(octUv.x * 2.0 - 1.0, octUv.y * 2.0 - 1.0, 0.0);
        float l1 = abs(un.x) + abs(un.y); un.z = 1.0 - l1;
        float dw = 1.0 / pow(max(abs(un.x) + abs(un.y) + abs(un.z), 1e-3), 3.0);
        float sh[9]; ShBasis(dir, sh);
        [unroll] for (uint k = 0; k < 9; k++) c[k] += rad * (sh[k] * dw);
        wsum += dw;
    }
    float norm = wsum > 1e-4 ? (4.0 * PI) / wsum : 0.0;
    [unroll] for (uint k = 0; k < 9; k++) c[k] = Sanitize(c[k] * norm);
    StoreProbeSH(pidx, c);
}

// ===== CSIntegrate: per full-res pixel, gather nearest probes' radiance, cosine-weighted =====
float3 SampleProbeTile(uint2 probe, float3 Npix) {
    uint oct = (uint)OctSize;
    float3 acc = 0.0.xxx; float wsum = 0.0;
    [loop] for (uint cy = 0; cy < oct; cy++)
    [loop] for (uint cx = 0; cx < oct; cx++) {
        float2 octUv = (float2(cx, cy) + 0.5) / float(oct);
        float3 dir = OctDecode(octUv);
        float w = max(dot(dir, Npix), 0.0);
        if (w <= 0.0) continue;
        float4 t = ProbeAtlasFiltered[uint2(probe.x * oct + cx, probe.y * oct + cy)];
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

    float stride = ProbeStride;
    float2 probeF = (float2(px) - stride * 0.5) / stride;
    float2 base = floor(probeF);
    int2 ibase = int2(base);

    float3 acc = 0.0.xxx; float wsum = 0.0;
    float3 bestRad = 0.0.xxx; float bestW = -1.0; bool anyValid = false;
    [unroll] for (int dy = -1; dy <= 2; dy++)
    [unroll] for (int dx = -1; dx <= 2; dx++) {
        int2 pc = ibase + int2(dx, dy);
        if (pc.x < 0 || pc.y < 0 || pc.x >= (int)ProbesX || pc.y >= (int)ProbesY) continue;
        uint pidx = pc.y * ProbesX + pc.x;
        ProbeHeader h = ProbeHeaders[pidx];
        if (h.PosValid.w < 0.5) continue;
        anyValid = true;
        float2 d2 = (float2(pc) - probeF);
        float wSpatial = exp(-dot(d2, d2) * 0.5);
        float wDepth = 1.0 / (1.0 + abs(h.NormalDepth.w - depth) * 1500.0);
        float wNormal = pow(saturate(dot(h.NormalDepth.xyz, Npix) * 0.5 + 0.5), 2.0);
        float w = wSpatial * wDepth * wNormal + 1e-5;
        float3 rad;
        if (UseSH > 0.5) { float3 prc[9]; LoadProbeSH(pidx, prc); rad = EvalProbeSH(prc, Npix); }
        else             { rad = SampleProbeTile((uint2)pc, Npix); }
        acc += rad * w; wsum += w;
        float fw = wSpatial * wDepth * (saturate(dot(h.NormalDepth.xyz, Npix)) + 0.1);
        if (fw > bestW) { bestW = fw; bestRad = rad; }
    }
    float3 E;
    if (wsum > 1e-3) E = acc / wsum;
    else if (anyValid) E = bestRad;
    else E = 0.0.xxx;
    Indirect[px] = float4(Sanitize(E * Intensity), depth);
}
