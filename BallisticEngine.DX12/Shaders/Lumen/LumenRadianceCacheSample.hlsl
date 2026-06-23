// Lumen FAZ 7 — WORLD-SPACE RADIANCE CACHE sampling helper (header-style include, NO entrypoint).
//
// The cache is a single camera-centered clipmap of octahedral world-space radiance probes (the FAR-FIELD GI noise
// reducer). The screen-probe gather (FAZ 6) traces SHORT rays; on a miss within the cell's space-diagonal trace-stop
// it (a) MARKS the covering cell for next frame's allocate+trace, and (b) samples this helper for the distant
// radiance. The cache's own trace pass (CSTrace in LumenRadianceCache.hlsl) could later reuse this for multi-bounce.
//
// The includer must, before #include "Lumen/LumenRadianceCacheSample.hlsl":
//   - paste RC_PARAMS into its b0 cbuffer (the helper reads these fields by name).
//   - declare an OctDecode(float2)->float3 / OctEncode(float3)->float2 octahedral map (the screen probe already has
//     them; the cache trace declares its own). A SamplerState LinearClamp : s0.
// All resources resolve from ResourceDescriptorHeap[] via the bindless indices in RC_PARAMS (HeapDirectlyIndexed
// root sig — the SAME pattern GlobalSdf/LumenTrace use). NaN-safe: ternary select, never lerp(v,0,flag).

#ifndef LUMEN_RADIANCE_CACHE_SAMPLE_INCLUDED
#define LUMEN_RADIANCE_CACHE_SAMPLE_INCLUDED

// The radiance-cache parameter block. Paste RC_PARAMS into the includer's b0 cbuffer (extra fields after it are fine).
//   RcOrigin       : clipmap min-corner world position (voxel-snapped).
//   RcProbeSpacing : world distance between adjacent probe grid points (= 2*extent/GridRes).
//   RcGridRes      : probes-grid resolution per axis (the indirection volume is GridRes^3).
//   RcAtlasInProbes: probes per atlas row/col (atlas = FinalProbeRes*AtlasInProbes square).
//   RcProbeRes     : octahedral probe resolution (e.g. 16).
//   RcFinalProbeRes: RcProbeRes + 2 (1-texel border each side).
//   RcTraceStop    : the screen-probe short-trace clamp = RcProbeSpacing*sqrt(3) (the cell space-diagonal).
//   RcEnabled      : 1 = sample/mark the cache, 0 = disabled (screen probe traces full distance — FAZ 6 fallback).
//   RcIndirIdx/RcRadIdx/RcHitIdx/RcMarkIdx : bindless ResourceDescriptorHeap[] indices.
#define RC_PARAMS                                                         \
    float3 RcOrigin;        float RcProbeSpacing;                         \
    uint   RcGridRes;       uint  RcAtlasInProbes; uint RcProbeRes; uint RcFinalProbeRes; \
    float  RcTraceStop;     float RcEnabled;       uint RcIndirIdx; uint RcRadIdx;        \
    uint   RcHitIdx;        uint  RcMarkIdx;       float RcSampleBias;  float RcPad0

static const uint RC_UNALLOC = 0xFFFFFFFFu;

float3 RcSanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

// World position → clipmap grid coordinate (continuous, in probe-grid units). Probe i sits at RcOrigin + i*spacing.
float3 RcGridCoordF(float3 worldP) {
    return (worldP - RcOrigin) / max(RcProbeSpacing, 1e-4);
}

// Flatten an integer grid cell (clamped) to the linear mark-buffer / index-volume index.
uint RcCellFlat(int3 c) {
    c = clamp(c, int3(0, 0, 0), int3((int)RcGridRes - 1, (int)RcGridRes - 1, (int)RcGridRes - 1));
    return (uint)c.z * RcGridRes * RcGridRes + (uint)c.y * RcGridRes + (uint)c.x;
}

// MARK a cell as "used this frame" (the screen probe calls this on a short-trace miss). NEXT frame's allocate fills it.
void RcMarkCell(float3 worldP) {
    if (RcEnabled < 0.5) return;
    float3 g = RcGridCoordF(worldP);
    if (any(g < 0.0) || any(g >= (float)RcGridRes)) return;   // outside the clipmap → no far cache here
    int3 c = (int3)floor(g);
    RWStructuredBuffer<uint> mark = ResourceDescriptorHeap[RcMarkIdx];
    uint prev;
    InterlockedOr(mark[RcCellFlat(c)], 1u, prev);
}

// Octahedral sample of one allocated probe's RadianceAtlas + its stored hit distance, for direction `dir`. The atlas
// probe occupies [base .. base+FinalProbeRes) with a 1-texel border; the inner RcProbeRes block maps oct [0,1].
void RcSampleProbe(uint atlasIndex, float3 dir, out float3 radiance, out float hitDist) {
    radiance = 0.0.xxx; hitDist = 1e9;
    Texture2D<float4> radTex = ResourceDescriptorHeap[RcRadIdx];
    Texture2D<float>  hitTex = ResourceDescriptorHeap[RcHitIdx];

    uint px = atlasIndex % RcAtlasInProbes;
    uint py = atlasIndex / RcAtlasInProbes;
    float2 octUv = OctEncode(dir);                                   // [0,1]
    // Map oct uv into the inner probe block (offset by the 1-texel border), in texels, then to atlas uv.
    float2 inner = octUv * (float)RcProbeRes + 1.0;                  // [1 .. ProbeRes+1] within the FinalProbeRes tile
    float2 atlasTexel = float2(px, py) * (float)RcFinalProbeRes + inner;
    float atlasDim = (float)(RcFinalProbeRes * RcAtlasInProbes);
    float2 uv = atlasTexel / max(atlasDim, 1.0);
    radiance = RcSanitize(radTex.SampleLevel(LinearClamp, uv, 0).rgb);
    hitDist  = hitTex.SampleLevel(LinearClamp, uv, 0).r;
}

// ====================================================================================================================
// TRILINEAR, DEPTH-OCCLUSION-WEIGHTED interpolation of the 8 surrounding probes.
//
// For a sample at worldPos looking toward `dir` (the screen probe's miss direction):
//   - find the 8 grid corners around worldPos,
//   - for each ALLOCATED corner probe: octahedral-sample its radiance + stored hit distance in `dir`,
//   - DEPTH-WEIGHT (the leak gotcha): the probe sits at its own world center; the geometric distance from THAT probe
//     to worldPos is `probeToSample`. If the probe's stored hit distance in `dir` is SHORTER than the distance from
//     worldPos onward (i.e. there's an occluder between the probe and the sample point that the sample point is on
//     the far side of), the probe sees DIFFERENT far geometry → down-weight it. We use a simple chebyshev-style
//     compare: weight = saturate( (probeHitDist + margin) - probeToSample along dir ). This kills light bleeding
//     through walls (a probe inside the closed box must not light the box exterior).
//   - trilinear-blend by the fractional grid position.
// NaN-safe; guarded divides; returns 0 when no allocated probe contributes (caller keeps its short-trace radiance).
// ====================================================================================================================
float3 SampleRadianceCacheInterpolated(float3 worldPos, float3 dir) {
    if (RcEnabled < 0.5) return 0.0.xxx;
    float3 g = RcGridCoordF(worldPos);                // grid coord of the surface point itself
    if (any(g < 0.0) || any(g >= (float)RcGridRes)) return 0.0.xxx;

    int3 base = (int3)floor(g - 0.5);                 // lower corner of the 8-cell neighbourhood (probe-centered grid)
    float3 frac3 = saturate(g - 0.5 - (float3)base);

    Texture3D<uint> indir = ResourceDescriptorHeap[RcIndirIdx];

    float3 accum = 0.0.xxx;
    float  wsum  = 0.0;
    [unroll] for (int i = 0; i < 8; i++) {
        int3 corner = base + int3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        if (any(corner < 0) || any(corner >= (int)RcGridRes)) continue;

        uint atlasIndex = indir.Load(int4(corner, 0));
        if (atlasIndex == RC_UNALLOC) continue;

        // Trilinear corner weight.
        float3 cw3 = float3((i & 1) ? frac3.x : 1.0 - frac3.x,
                            (i >> 1) & 1 ? frac3.y : 1.0 - frac3.y,
                            (i >> 2) & 1 ? frac3.z : 1.0 - frac3.z);
        float triW = cw3.x * cw3.y * cw3.z;
        if (triW <= 0.0) continue;

        float3 rad; float storedHit;
        RcSampleProbe(atlasIndex, dir, rad, storedHit);

        // Probe world center, and the signed distance from the probe to the sample point ALONG dir.
        float3 probeCenter = RcOrigin + ((float3)corner) * RcProbeSpacing;
        float  probeToSampleAlong = dot(worldPos - probeCenter, dir);   // how far down `dir` the sample sits past the probe

        // OCCLUSION WEIGHT: if the probe's ray in this direction hit something BEFORE reaching the sample point's depth,
        // the probe is occluded relative to the sample → its far radiance is for different geometry. Margin = one probe
        // spacing of slack (capture jitter). saturate keeps it [0,1]; never negative.
        float margin = RcProbeSpacing * 0.5;
        float occW = saturate((storedHit + margin - probeToSampleAlong) / max(RcProbeSpacing, 1e-4));
        // When the sample point is BEHIND the probe along dir (probeToSampleAlong <= 0), no occlusion penalty.
        occW = (probeToSampleAlong <= 0.0) ? 1.0 : occW;

        float w = triW * occW;
        accum += rad * w;
        wsum  += w;
    }
    return wsum > 1e-5 ? RcSanitize(accum / wsum) : 0.0.xxx;
}

#endif // LUMEN_RADIANCE_CACHE_SAMPLE_INCLUDED
