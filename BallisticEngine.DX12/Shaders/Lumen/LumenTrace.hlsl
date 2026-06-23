// Lumen FAZ 5 — the LUMEN TRACE abstraction (header-style include, NO entrypoint).
//
// This is the KEYSTONE that turns the global SDF (FAZ 2) + the lit surface cache (FAZ 3) into real GI: given a ray,
// find a hit (HW TLAS RayQuery OR software global-SDF sphere-march) and return the RADIANCE at that hit by sampling
// the surface cache's FinalLighting. The same include is consumed by the FAZ 5 debug view and (later) the FAZ 6
// screen-probe integrator.
//
// The includer MUST, before `#include "Lumen/LumenTrace.hlsl"`:
//   - declare a cbuffer at b0 whose LAYOUT BEGINS WITH the `LumenTraceParams` fields below (the include reads them by
//     name — extra fields after them are fine), or simply paste `LUMEN_TRACE_PARAMS;` inside its own b0 cbuffer.
//   - bind: RaytracingAccelerationStructure `Scene` : t0 ; StructuredBuffer<GpuLumenCard> `Cards` : t1 ;
//           StructuredBuffer<GpuLumenPage> `Pages` : t2 ; StructuredBuffer<InstanceCardRange> `InstanceRanges` : t3.
//   - bind a HeapDirectlyIndexed root sig so the clipmap Texture3D + FinalLighting Texture2D resolve from
//     ResourceDescriptorHeap[ <index from the CB> ] (the SAME reserved-tail-bindless approach GlobalSdf/LumenCardLight
//     use). A LinearClamp sampler at s0 (for the clipmap trilinear sample).
//
// Driver rules obeyed (see CLAUDE.md / FAZ 3 lessons): NaN scrub is a ternary SELECT (never lerp(v,0,flag) — NaN*0=NaN);
// every divide guards its denominator; saturate before pow/sqrt; no unclamped colour sums fed back into a loop.

#ifndef LUMEN_TRACE_INCLUDED
#define LUMEN_TRACE_INCLUDED

// ---- GPU structs (mirror Dx12LumenCardScene — IDENTICAL layout to LumenCardLight.hlsl) ----
// Guarded so the includer can declare them FIRST (it must, to type its StructuredBuffer<LtCard> binds before this
// include is pasted). If the includer already defined them (LT_STRUCTS_DEFINED), skip the re-declaration here.
#ifndef LT_STRUCTS_DEFINED
#define LT_STRUCTS_DEFINED
struct LtCard {   // GpuLumenCard, 64 B world-space
    float3 Origin; uint  PageId;
    float3 AxisX;  float ExtentX;
    float3 AxisY;  float ExtentY;
    float3 AxisZ;  float ExtentZ;
};
struct LtPage {   // GpuLumenPage, 32 B
    uint AtlasOffsetX, AtlasOffsetY;
    uint SizeX, SizeY;
    uint CardId, ResLevel, Pad0, Pad1;
};
struct LtInstanceRange { uint Offset; uint Count; };   // InstanceCardRange
#endif

// The parameter block the includer's b0 cbuffer must begin with. Paste LUMEN_TRACE_PARAMS into the cbuffer, OR mirror
// these fields verbatim at its head. (A macro keeps the includer + this file in lockstep — one source of truth.)
#define LUMEN_TRACE_PARAMS                                              \
    float3 LtClipOrigin;   float LtVoxelSize;                          \
    float3 LtCamPosUnused; float LtClipHalfExtent;                     \
    uint   LtClipResX, LtClipResY, LtClipResZ; float LtMaxTraceDist;   \
    uint   LtAtlasSize, LtCardCount, LtInstanceCount, LtFinalReadIdx;  \
    uint   LtClipmapIdx, LtFinalValid, LtHasTlas, LtSkyIdx;            \
    float  LtSkyIntensity, LtUseSky, LtSurfBias, LtPad0;               \
    /* FAZ 11 — spatial card grid (world-pos lookup accel). LtCgEnabled=0 → linear scan (old). 48B, 16-aligned. */ \
    float3 LtCgOrigin;     float LtCgEnabled;                          \
    float3 LtCgCellSize;   uint  LtCgDim;                              \
    uint   LtCgCellIdx, LtCgIndexIdx, LtCgPad0, LtCgPad1

struct LumenTraceResult { float3 Radiance; float HitT; bool Hit; };

// ----------------------------------------------------------------------------------------------------
// NaN-safe helpers (ternary select, never lerp).
// ----------------------------------------------------------------------------------------------------
float3 LtSanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

// ----------------------------------------------------------------------------------------------------
// SURFACE CACHE SAMPLING — map a world hit → a card → its atlas texel → FinalLighting.
// ----------------------------------------------------------------------------------------------------

// Project a world point onto a card and (if it plausibly lies on the card) sample FinalLighting. Returns true + the
// sampled radiance via `outRad`. Shared by the instance + world-pos variants so the projection/UV/page math lives once.
bool LtSampleCard(LtCard c, float3 hitPos, float3 hitNormal, Texture2D<float4> finalRead, out float3 outRad) {
    outRad = 0.0.xxx;
    if (c.PageId == 0xFFFFFFFFu) return false;
    float3 rel = hitPos - c.Origin;
    float du = dot(rel, c.AxisX) / max(c.ExtentX, 1e-4);   // [-1,1] in the card plane
    float dv = dot(rel, c.AxisY) / max(c.ExtentY, 1e-4);
    float dd = dot(rel, c.AxisZ) / max(c.ExtentZ, 1e-4);   // [-1,1] across the depth slab
    if (abs(du) > 1.2 || abs(dv) > 1.2 || abs(dd) > 1.5) return false;   // outside the OBB (capture-jitter slack)

    LtPage pg = Pages[c.PageId];
    float2 luv = saturate(float2(du * 0.5 + 0.5, dv * 0.5 + 0.5));   // card-space [-1,1] → [0,1]
    uint sx = pg.SizeX > 0u ? pg.SizeX : 1u;
    uint sy = pg.SizeY > 0u ? pg.SizeY : 1u;
    uint px = pg.AtlasOffsetX + (uint)(luv.x * (float)(sx - 1u) + 0.5);
    uint py = pg.AtlasOffsetY + (uint)((1.0 - luv.y) * (float)(sy - 1u) + 0.5);   // invert v (capture top-row = uv.y=1)
    outRad = LtSanitize(finalRead.Load(int3((int)px, (int)py, 0)).rgb);
    return true;
}

// HW path: the RayQuery gives the instance id, so we scan only THAT instance's card range (cheap). Factored from
// LumenCardLight.SampleFinalAtHit — same OBB-contain + normal-align scoring. Samples LtFinalReadIdx (FinalLighting READ).
float3 SampleSurfaceCache_Instance(uint instance, float3 hitPos, float3 hitNormal) {
    if (instance >= LtInstanceCount) return 0.0.xxx;
    Texture2D<float4> finalRead = ResourceDescriptorHeap[LtFinalReadIdx];
    LtInstanceRange range = InstanceRanges[instance];
    float bestScore = -1e9; float3 bestRad = 0.0.xxx; bool found = false;
    [loop] for (uint k = 0; k < range.Count; k++) {
        uint ci = range.Offset + k;
        if (ci >= LtCardCount) break;
        LtCard c = Cards[ci];
        float3 rad;
        if (!LtSampleCard(c, hitPos, hitNormal, finalRead, rad)) continue;
        // Score = outward-card-normal alignment with the hit normal, minus an in-plane distance penalty.
        float3 rel = hitPos - c.Origin;
        float du = dot(rel, c.AxisX) / max(c.ExtentX, 1e-4);
        float dv = dot(rel, c.AxisY) / max(c.ExtentY, 1e-4);
        float score = dot(hitNormal, c.AxisZ) - 0.25 * (abs(du) + abs(dv));
        if (score > bestScore) { bestScore = score; bestRad = rad; found = true; }
    }
    return (found && bestScore > -0.5) ? bestRad : 0.0.xxx;
}

// SW path: an SDF hit gives only a world point (no instance) — scan ALL cards. v1 SIMPLIFICATION: a linear O(CardCount)
// scan. Fine for the GI test scenes (~12 cards). For Bistro-scale card counts this is slow; a spatial accel (a card
// grid / per-voxel card list) is a LATER refinement (see the FAZ 5 brief). Picks the card whose OBB contains the point
// AND whose outward normal best aligns with the SDF-gradient hit normal.
// Score + sample one card against the world hit; updates best* if it wins. Factored so the grid + linear paths share it.
void LtScoreCard(uint ci, float3 hitPos, float3 hitNormal, Texture2D<float4> finalRead,
                 inout float bestScore, inout float3 bestRad, inout bool found) {
    if (ci >= LtCardCount) return;
    LtCard c = Cards[ci];
    float3 rad;
    if (!LtSampleCard(c, hitPos, hitNormal, finalRead, rad)) return;
    float3 rel = hitPos - c.Origin;
    float du = dot(rel, c.AxisX) / max(c.ExtentX, 1e-4);
    float dv = dot(rel, c.AxisY) / max(c.ExtentY, 1e-4);
    float dd = dot(rel, c.AxisZ) / max(c.ExtentZ, 1e-4);
    float score = dot(hitNormal, c.AxisZ) - 0.25 * (abs(du) + abs(dv)) - 0.5 * abs(dd);
    if (score > bestScore) { bestScore = score; bestRad = rad; found = true; }
}

float3 SampleSurfaceCache_WorldPos(float3 hitPos, float3 hitNormal) {
    Texture2D<float4> finalRead = ResourceDescriptorHeap[LtFinalReadIdx];
    float bestScore = -1e9; float3 bestRad = 0.0.xxx; bool found = false;

    // FAZ 11 — SPATIAL CARD GRID fast path: bucket the hit into its grid cell + the 6 face neighbours (cards straddling
    // a cell border), loop ONLY those cells' card indices (O(cards-in-cell) ≈ a handful vs O(CardCount)). The grid +
    // index buffers are bindless (reserved-tail slots in the CB). LtCgEnabled=0 → fall through to the linear scan.
    if (LtCgEnabled > 0.5) {
        StructuredBuffer<uint2> CgCells = ResourceDescriptorHeap[LtCgCellIdx];   // .x=offset, .y=count
        Buffer<uint>            CgIndex = ResourceDescriptorHeap[LtCgIndexIdx];
        int3 base = (int3)floor((hitPos - LtCgOrigin) / max(LtCgCellSize, 1e-4));
        if (all(base >= -1) && all(base <= (int)LtCgDim)) {
            // home + 6 face neighbours
            int3 offs[7] = { int3(0,0,0), int3(1,0,0), int3(-1,0,0), int3(0,1,0), int3(0,-1,0), int3(0,0,1), int3(0,0,-1) };
            [loop] for (int o = 0; o < 7; o++) {
                int3 cc = base + offs[o];
                if (any(cc < 0) || any(cc >= (int)LtCgDim)) continue;
                uint cell = ((uint)cc.z * LtCgDim + (uint)cc.y) * LtCgDim + (uint)cc.x;
                uint2 oc = CgCells[cell];   // offset, count
                [loop] for (uint k = 0; k < oc.y; k++)
                    LtScoreCard(CgIndex[oc.x + k], hitPos, hitNormal, finalRead, bestScore, bestRad, found);
            }
            return (found && bestScore > -0.5) ? bestRad : 0.0.xxx;
        }
        // hitPos outside the grid AABB → nothing cached here.
        return 0.0.xxx;
    }

    // Linear scan fallback (door off / grid not built): O(CardCount). Fine for small scenes.
    uint n = min(LtCardCount, 65536u);
    [loop] for (uint ci = 0; ci < n; ci++)
        LtScoreCard(ci, hitPos, hitNormal, finalRead, bestScore, bestRad, found);
    return (found && bestScore > -0.5) ? bestRad : 0.0.xxx;
}

// ----------------------------------------------------------------------------------------------------
// SKY (miss term) — sample the prefiltered env cube at LtSkyIdx if bound + enabled, else 0.
// ----------------------------------------------------------------------------------------------------
float3 LtSky(float3 dir) {
    if (LtUseSky < 0.5) return 0.0.xxx;
    TextureCube<float4> sky = ResourceDescriptorHeap[LtSkyIdx];
    return LtSanitize(max(sky.SampleLevel(LinearClamp, dir, 0).rgb, 0.0.xxx)) * LtSkyIntensity;
}

// ----------------------------------------------------------------------------------------------------
// HW BACKEND — inline RayQuery over the scene TLAS → SampleSurfaceCache_Instance.
// ----------------------------------------------------------------------------------------------------
LumenTraceResult LumenTraceHW(float3 origin, float3 dir, float maxDist) {
    LumenTraceResult r; r.Radiance = 0.0.xxx; r.HitT = maxDist; r.Hit = false;
    RayDesc ray; ray.Origin = origin; ray.Direction = dir; ray.TMin = 0.01; ray.TMax = max(maxDist, 0.02);
    RayQuery<RAY_FLAG_FORCE_OPAQUE> q;
    q.TraceRayInline(Scene, 0, 0xFF, ray);
    q.Proceed();
    if (q.CommittedStatus() == COMMITTED_TRIANGLE_HIT) {
        uint inst   = q.CommittedInstanceID();
        float t     = q.CommittedRayT();
        float3 hp   = origin + dir * t;
        float3 hn   = -dir;   // approx: we sampled a surface facing back toward the ray origin (good enough for the cache pick).
        r.Radiance = SampleSurfaceCache_Instance(inst, hp, hn);
        r.HitT = t; r.Hit = true;
    } else {
        r.Radiance = LtSky(dir);
    }
    r.Radiance = LtSanitize(max(r.Radiance, 0.0.xxx));
    return r;
}

// ----------------------------------------------------------------------------------------------------
// SW BACKEND — global-SDF sphere-march (reuses GlobalSdfDebug's IntersectClipBox + march) → SampleSurfaceCache_WorldPos.
// ----------------------------------------------------------------------------------------------------
float3 LtClipMin() { return LtClipOrigin; }
float3 LtClipMax() { return LtClipOrigin + float3(LtClipResX, LtClipResY, LtClipResZ) * LtVoxelSize; }

float LtSampleClip(float3 worldP) {
    Texture3D<float> clipmap = ResourceDescriptorHeap[LtClipmapIdx];
    float3 lo = LtClipMin(), hi = LtClipMax();
    float3 ext = max(hi - lo, float3(1e-4, 1e-4, 1e-4));
    float3 uvw = (worldP - lo) / ext;
    if (any(uvw < 0.0) || any(uvw > 1.0)) return LtClipHalfExtent;   // outside the volume = far/empty
    return clipmap.SampleLevel(LinearClamp, saturate(uvw), 0);
}
float3 LtClipNormal(float3 p) {
    float h = LtVoxelSize;
    float dx = LtSampleClip(p + float3(h, 0, 0)) - LtSampleClip(p - float3(h, 0, 0));
    float dy = LtSampleClip(p + float3(0, h, 0)) - LtSampleClip(p - float3(0, h, 0));
    float dz = LtSampleClip(p + float3(0, 0, h)) - LtSampleClip(p - float3(0, 0, h));
    float3 g = float3(dx, dy, dz);
    float len = length(g);
    return (len > 1e-6) ? g / len : float3(0, 1, 0);
}
bool LtIntersectClipBox(float3 ro, float3 rd, out float tNear, out float tFar) {
    float3 lo = LtClipMin(), hi = LtClipMax();
    float3 safeRd = float3(
        abs(rd.x) < 1e-8 ? (rd.x < 0 ? -1e-8 : 1e-8) : rd.x,
        abs(rd.y) < 1e-8 ? (rd.y < 0 ? -1e-8 : 1e-8) : rd.y,
        abs(rd.z) < 1e-8 ? (rd.z < 0 ? -1e-8 : 1e-8) : rd.z);
    float3 inv = 1.0 / safeRd;
    float3 t0 = (lo - ro) * inv, t1 = (hi - ro) * inv;
    float3 tmin = min(t0, t1), tmax = max(t0, t1);
    tNear = max(max(tmin.x, tmin.y), tmin.z);
    tFar  = min(min(tmax.x, tmax.y), tmax.z);
    return tFar >= max(tNear, 0.0);
}

LumenTraceResult LumenTraceSW(float3 origin, float3 dir, float maxDist) {
    LumenTraceResult r; r.Radiance = 0.0.xxx; r.HitT = maxDist; r.Hit = false;
    float tNear, tFar;
    if (!LtIntersectClipBox(origin, dir, tNear, tFar)) { r.Radiance = LtSky(dir); return r; }

    float t = max(tNear, 0.0) + LtVoxelSize * 0.5;
    float tEnd = min(tFar, maxDist);
    float eps = LtVoxelSize * 0.5;
    bool hit = false;
    float3 p = origin + dir * t;
    [loop] for (int s = 0; s < 256; ++s) {
        if (t > tEnd) break;
        p = origin + dir * t;
        float d = LtSampleClip(p);
        if (d < eps) { hit = true; break; }
        t += max(d, LtVoxelSize * 0.25);   // sphere-trace; floor the step so a near-zero field can't stall.
    }
    if (hit) {
        float3 hn = LtClipNormal(p);
        r.Radiance = SampleSurfaceCache_WorldPos(p, hn);
        r.HitT = t; r.Hit = true;
    } else {
        r.Radiance = LtSky(dir);
    }
    r.Radiance = LtSanitize(max(r.Radiance, 0.0.xxx));
    return r;
}

// ----------------------------------------------------------------------------------------------------
// DISPATCHER — SW when forced (preferSW) or no TLAS bound, else HW.
// ----------------------------------------------------------------------------------------------------
LumenTraceResult LumenTrace(float3 origin, float3 dir, float maxDist, bool preferSW) {
    if (preferSW || LtHasTlas == 0u)
        return LumenTraceSW(origin, dir, maxDist);
    return LumenTraceHW(origin, dir, maxDist);
}

#endif // LUMEN_TRACE_INCLUDED
