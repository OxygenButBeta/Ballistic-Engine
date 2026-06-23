// Lumen FAZ 7 — WORLD-SPACE RADIANCE CACHE build passes (CSAllocate / CSTrace / CSFixup).
//
// The cache is a single camera-centered clipmap of octahedral world-space radiance probes — the FAR-FIELD GI noise
// reducer + its own temporal denoiser. The screen probes (FAZ 6) trace SHORT rays and, on a miss within the cell's
// space-diagonal trace-stop, MARK the covering cell (NEXT frame) + SAMPLE this cache for the distant radiance. The
// build runs at the START of the Lumen GI, BEFORE the screen-probe gather, on the cells marked LAST frame (UE's
// 1-frame-deferred scheme). Persisted across frames.
//
// Three compute passes (this file), in order:
//   CSAllocate — over the GridRes^3 indirection volume. For each LAST-frame-marked cell with no live probe: pull a
//                free-list slot (atomic), write its atlas index into indirection, record ProbeLastUsedFrame. Refresh
//                live cells; evict cells unrequested > EvictFrames (slot returns to the free list). Builds the compact
//                trace list (atlas slot + clipmap coord) for the trace pass + a budget cap. Then clears the mark
//                buffer (consumed). Single 8x8x... ? no — dispatched 1 thread / cell (4x4x4 groups).
//   CSTrace    — one 8x8 thread group per 8x8 trace-tile of each probe-to-trace; each thread = one octahedral
//                direction. probe world center = clipmap coord; origin = center; dir = octDecode(texel). LumenTrace
//                FAR → write Radiance into RadianceAtlas, HitT into HitDistAtlas. Sky on miss.
//   CSFixup    — fill the 1-texel octahedral border of each traced probe (v1: clamp-to-edge wrap; the proper
//                antipodal octahedral wrap is a TODO — documented) so bilinear sampling at probe edges is correct.
//
// Driver rules obeyed: NaN scrub = ternary select (never lerp(v,0,flag)); guarded divides; saturate before pow/sqrt.

// ---- struct types LumenTrace needs (declared FIRST, guarded) — IDENTICAL to LumenTrace.hlsl / Dx12LumenCardScene. ----
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

cbuffer RcConstants : register(b0) {
    // --- LumenTrace parameter block (MUST be first; the include reads these by name) ---
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
    // --- radiance-cache params ---
    float3 RcOrigin;        float RcProbeSpacing;
    uint   RcGridRes;       uint  RcAtlasInProbes; uint RcProbeRes; uint RcFinalProbeRes;
    float  RcFarMaxDist;    float RcPreferSW;      uint RcFrameIndex; uint RcTraceBudget;
    uint   RcEvictFrames;   uint  RcIndirIdx;      uint RcRadIdx;     uint RcHitIdx;
    uint   RcMarkIdx;       uint  RcFreeListIdx;   float RcSkyIntensity2; float RcUseSky2;
    uint   RcFreeCount;     uint  RcAtlasCapacity; float RcRcPad0; float RcRcPad1;
};

RaytracingAccelerationStructure Scene : register(t0);
StructuredBuffer<LtCard>          Cards          : register(t1);
StructuredBuffer<LtPage>          Pages          : register(t2);
StructuredBuffer<LtInstanceRange> InstanceRanges : register(t3);

SamplerState LinearClamp : register(s0);

#include "Lumen/LumenTrace.hlsl"

static const uint RC_UNALLOC2 = 0xFFFFFFFFu;

// --- Octahedral map (full sphere) — same convention as LumenScreenProbe.hlsl / the sample include. ---
float2 RcOctEncode(float3 n) {
    n /= (abs(n.x) + abs(n.y) + abs(n.z));
    float2 e = n.xy;
    if (n.z < 0.0) e = (1.0 - abs(e.yx)) * float2(e.x >= 0.0 ? 1.0 : -1.0, e.y >= 0.0 ? 1.0 : -1.0);
    return e * 0.5 + 0.5;
}
float3 RcOctDecode(float2 f) {
    f = f * 2.0 - 1.0;
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.xy += float2(n.x >= 0.0 ? -t : t, n.y >= 0.0 ? -t : t);
    return normalize(n);
}

uint RcCellFlat3(int3 c) {
    return (uint)c.z * RcGridRes * RcGridRes + (uint)c.y * RcGridRes + (uint)c.x;
}

// ====================================================================================================================
// CSInit — ONE-TIME: clear the indirection volume to RC_UNALLOC (no live probes) + zero the mark buffer. The free
// list is CPU-initialized (all slots free). Dispatched once at resource creation (GridRes^3, 4x4x4 groups).
// ====================================================================================================================
[numthreads(4, 4, 4)]
void CSInit(uint3 dtid : SV_DispatchThreadID) {
    if (dtid.x >= RcGridRes || dtid.y >= RcGridRes || dtid.z >= RcGridRes) return;
    RWTexture3D<uint> indir = ResourceDescriptorHeap[RcIndirIdx];
    RWStructuredBuffer<uint> mark = ResourceDescriptorHeap[RcMarkIdx];
    indir[dtid] = RC_UNALLOC2;
    mark[RcCellFlat3((int3)dtid)] = 0u;
}

// FreeList buffer layout (uint[]):
//   [0]                = free-stack TOP (atomic counter; number of free slots remaining)
//   [1 .. AtlasCapacity]      = the free-slot stack (atlas indices that are available)
//   [1+Cap .. 1+2*Cap)        = ProbeLastUsedFrame[atlasIndex]  (eviction bookkeeping)
//   [1+2*Cap .. ]             = trace list: [count][ (cellFlat, atlasIndex) pairs ... ]
// The trace-list count lives at TRACE_COUNT_OFF; pairs follow. Budget-capped.
#define FREE_TOP_OFF      0u
#define FREE_STACK_OFF    1u
#define LASTUSED_OFF      (1u + RcAtlasCapacity)
#define TRACE_COUNT_OFF   (1u + 2u * RcAtlasCapacity)
#define TRACE_LIST_OFF    (TRACE_COUNT_OFF + 1u)

// ====================================================================================================================
// CSAllocate — 1 thread / indirection cell (GridRes^3, dispatched 4x4x4 groups). Reads LAST frame's mark buffer.
// ====================================================================================================================
[numthreads(4, 4, 4)]
void CSAllocate(uint3 dtid : SV_DispatchThreadID) {
    if (dtid.x >= RcGridRes || dtid.y >= RcGridRes || dtid.z >= RcGridRes) return;
    int3 cell = (int3)dtid;
    uint cellFlat = RcCellFlat3(cell);

    RWStructuredBuffer<uint> mark = ResourceDescriptorHeap[RcMarkIdx];
    RWStructuredBuffer<uint> freeBuf = ResourceDescriptorHeap[RcFreeListIdx];
    RWTexture3D<uint> indir = ResourceDescriptorHeap[RcIndirIdx];

    uint used = mark[cellFlat];                  // 1 if a screen probe marked this cell LAST frame
    uint cur  = indir[dtid];                     // current atlas slot or RC_UNALLOC2

    if (used != 0u) {
        if (cur != RC_UNALLOC2) {
            // already live — refresh last-used, re-trace it this frame (keeps the far field fresh)
            freeBuf[LASTUSED_OFF + cur] = RcFrameIndex;
            uint w; InterlockedAdd(freeBuf[TRACE_COUNT_OFF], 1u, w);
            if (w < RcTraceBudget) {
                freeBuf[TRACE_LIST_OFF + w * 2u + 0u] = cellFlat;
                freeBuf[TRACE_LIST_OFF + w * 2u + 1u] = cur;
            }
        } else {
            // newly requested — pull a free slot (atomic pop from the stack top).
            uint top; InterlockedAdd(freeBuf[FREE_TOP_OFF], (uint)(-1), top);   // top now = old value; we consumed slot at (top-1)
            if (top >= 1u && top <= RcAtlasCapacity) {
                uint slot = freeBuf[FREE_STACK_OFF + (top - 1u)];
                indir[dtid] = slot;
                freeBuf[LASTUSED_OFF + slot] = RcFrameIndex;
                uint w; InterlockedAdd(freeBuf[TRACE_COUNT_OFF], 1u, w);
                if (w < RcTraceBudget) {
                    freeBuf[TRACE_LIST_OFF + w * 2u + 0u] = cellFlat;
                    freeBuf[TRACE_LIST_OFF + w * 2u + 1u] = slot;
                }
            } else {
                // free list exhausted (or raced empty): undo the decrement so the counter stays consistent.
                uint dummy; InterlockedAdd(freeBuf[FREE_TOP_OFF], 1u, dummy);
                // leave unallocated; the cell will be re-marked next frame and retried (round-robin under budget).
            }
        }
    } else {
        // not requested this frame — EVICT if a live probe here has been idle > EvictFrames.
        if (cur != RC_UNALLOC2) {
            uint last = freeBuf[LASTUSED_OFF + cur];
            // unsigned-safe age (RcFrameIndex monotonic; last <= frame).
            uint age = RcFrameIndex >= last ? (RcFrameIndex - last) : 0u;
            if (age > RcEvictFrames) {
                indir[dtid] = RC_UNALLOC2;
                uint top; InterlockedAdd(freeBuf[FREE_TOP_OFF], 1u, top);   // push slot back; top = old count
                if (top < RcAtlasCapacity)
                    freeBuf[FREE_STACK_OFF + top] = cur;
            }
        }
    }

    // CONSUME the mark for this cell (cleared for next frame's marking). Each cell owned by exactly one thread → safe.
    mark[cellFlat] = 0u;
}

// ====================================================================================================================
// CSTrace — dispatched (ProbeRes/8 * TraceBudget) x (ProbeRes/8) x 1 ; group.x encodes (traceIdx, tileX). Each thread
// is one octahedral texel of one probe-to-trace. We pack the trace index into the dispatch by computing it from the
// global thread x against ProbeRes tiles.
// ====================================================================================================================
[numthreads(8, 8, 1)]
void CSTrace(uint3 dtid : SV_DispatchThreadID) {
    // Global x spans (RcTraceBudget * RcProbeRes); each ProbeRes block is one probe.
    uint probeRes = RcProbeRes;
    uint traceIdx = dtid.x / probeRes;
    uint lx = dtid.x % probeRes;
    uint ly = dtid.y;
    if (ly >= probeRes) return;

    RWStructuredBuffer<uint> freeBuf = ResourceDescriptorHeap[RcFreeListIdx];
    uint traceCount = min(freeBuf[TRACE_COUNT_OFF], RcTraceBudget);
    if (traceIdx >= traceCount) return;

    uint cellFlat   = freeBuf[TRACE_LIST_OFF + traceIdx * 2u + 0u];
    uint atlasIndex = freeBuf[TRACE_LIST_OFF + traceIdx * 2u + 1u];

    // Decode cellFlat → 3D clipmap coord → probe world center.
    uint gz = cellFlat / (RcGridRes * RcGridRes);
    uint rem = cellFlat - gz * RcGridRes * RcGridRes;
    uint gy = rem / RcGridRes;
    uint gx = rem - gy * RcGridRes;
    float3 probeCenter = RcOrigin + float3(gx, gy, gz) * RcProbeSpacing;

    // Octahedral direction for this texel (cell center).
    float2 octUv = (float2(lx, ly) + 0.5) / (float)probeRes;
    float3 dir = RcOctDecode(octUv);

    // Trace FAR — the cache's whole job is the far field. preferSW selectable.
    bool preferSW = RcPreferSW > 0.5;
    LumenTraceResult tr = LumenTrace(probeCenter, dir, RcFarMaxDist, preferSW);
    float3 rad = LtSanitize(max(tr.Radiance, 0.0.xxx));
    float hitDist = tr.Hit ? tr.HitT : RcFarMaxDist;

    // Write into the atlas at the INNER block (offset by the 1-texel border).
    uint px = atlasIndex % RcAtlasInProbes;
    uint py = atlasIndex / RcAtlasInProbes;
    uint2 atlasBase = uint2(px, py) * RcFinalProbeRes + uint2(1, 1);   // +1 border
    uint2 atlasPx = atlasBase + uint2(lx, ly);

    RWTexture2D<float4> radAtlas = ResourceDescriptorHeap[RcRadIdx];
    RWTexture2D<float>  hitAtlas = ResourceDescriptorHeap[RcHitIdx];
    radAtlas[atlasPx] = float4(rad, 1.0);
    hitAtlas[atlasPx] = hitDist;
}

// ====================================================================================================================
// CSFixup — fill the 1-texel border of each traced probe. v1: CLAMP-TO-EDGE (copy the nearest inner texel). The
// proper octahedral antipodal seam wrap is a documented TODO; clamp is acceptable for v1 and removes the worst
// bilinear edge artifacts. 1 thread / border texel of every probe-to-trace.
// ====================================================================================================================
[numthreads(8, 8, 1)]
void CSFixup(uint3 dtid : SV_DispatchThreadID) {
    uint finalRes = RcFinalProbeRes;
    uint traceIdx = dtid.x / finalRes;
    uint fx = dtid.x % finalRes;
    uint fy = dtid.y;
    if (fy >= finalRes) return;

    // Only border texels (the trace already filled the inner block).
    bool isBorder = (fx == 0u || fy == 0u || fx == finalRes - 1u || fy == finalRes - 1u);
    if (!isBorder) return;

    RWStructuredBuffer<uint> freeBuf = ResourceDescriptorHeap[RcFreeListIdx];
    uint traceCount = min(freeBuf[TRACE_COUNT_OFF], RcTraceBudget);
    if (traceIdx >= traceCount) return;
    uint atlasIndex = freeBuf[TRACE_LIST_OFF + traceIdx * 2u + 1u];

    uint px = atlasIndex % RcAtlasInProbes;
    uint py = atlasIndex / RcAtlasInProbes;
    uint2 tileBase = uint2(px, py) * finalRes;

    // Clamp the border coord to the nearest inner texel [1 .. ProbeRes].
    uint ix = clamp(fx, 1u, RcProbeRes);
    uint iy = clamp(fy, 1u, RcProbeRes);

    RWTexture2D<float4> radAtlas = ResourceDescriptorHeap[RcRadIdx];
    RWTexture2D<float>  hitAtlas = ResourceDescriptorHeap[RcHitIdx];
    float4 rad = radAtlas[tileBase + uint2(ix, iy)];
    float  hit = hitAtlas[tileBase + uint2(ix, iy)];
    radAtlas[tileBase + uint2(fx, fy)] = rad;
    hitAtlas[tileBase + uint2(fx, fy)] = hit;
}
