// DDGI probe BLEND pass (compute, SM6.6) — GI plan P2.1. Integrates the per-ray (radiance, distance) data
// (from DdgiTrace.hlsl) into each probe's octahedral IRRADIANCE tile and DEPTH-moments tile, blended over
// time with a hysteresis EMA (so the field is temporally stable). Two entry points share the math:
//   CSIrradiance — one thread per irradiance texel: cosine-weighted sum of rays whose direction is in the
//                  texel's hemisphere → irradiance; EMA-blend into the atlas tile.
//   CSDepth      — one thread per depth texel: sharpened-cosine-weighted sum of ray distances → (mean,
//                  mean-sq) moments for the Chebyshev visibility test (P2.2); EMA-blend.
// Octahedral mapping (Cigolle et al.) packs the sphere into a square tile; a 1px border is filled by the
// blend so bilinear sampling wraps correctly across edges (P2.2 gather samples with the border).
//
// Bound: CBV b0 DdgiConstants; SRV t0 RayData (the trace output); UAV u0 the target atlas (irradiance or
// depth). Dispatched once per atlas with the matching tile size in DdgiConstants.

cbuffer DdgiConstants : register(b0) {
    float4 OriginSpacingX; float4 SpacingYZ; float4 ProbeDims;
    float4 Params0;          // x irrTexels, y depthTexels, z hysteresis, w frameIndex
    float4 Params1;          // x maxRayDist, y normalBias, z feedbackEnable, w intensity
};
StructuredBuffer<float4> RayData : register(t0);   // [probe * RaysPerProbe + ray] = (radiance, dist)
RWTexture2D<float4> IrradianceAtlas : register(u0);   // CSIrradiance target
RWTexture2D<float2> DepthAtlas      : register(u1);   // CSDepth target (distinct register; one bound per pass)
RWStructuredBuffer<float4> ProbeState : register(u2); // CSClassify target: xyz = relocation offset (world), w = active(1/0)

static const float PI = 3.14159265359;
static const uint RAYS_PER_PROBE = 144u;
static const uint IRR_TEXELS = 6u;
static const uint DEPTH_TEXELS = 16u;
static const uint BORDER = 1u;

// Ternary component-select NaN/Inf scrub (NEVER mix(v,0,flag) — NaN*0==NaN, the proven AMD black-hole bug).
float3 SanitizeIrr(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}
float2 SanitizeDepth(float2 v) {
    return float2(isnan(v.x) || isinf(v.x) ? 0.0 : v.x, isnan(v.y) || isinf(v.y) ? 0.0 : v.y);
}

// --- Octahedral mapping: [0,1]^2 tile UV (no border) → unit direction. ---
float3 OctDecode(float2 f) {
    f = f * 2.0 - 1.0;
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.xy += n.xy >= 0.0 ? -t : t;
    return normalize(n);
}

// Same ray-direction generator as DdgiTrace (must match exactly).
float3 SphericalFibonacci(uint i, uint n, float jitter) {
    float phi = 2.39996323 * (float(i) + jitter);
    float cosT = 1.0 - (2.0 * float(i) + 1.0) / float(n);
    float sinT = sqrt(saturate(1.0 - cosT * cosT));
    return float3(cos(phi) * sinT, sin(phi) * sinT, cosT);
}
float Hash1(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}

// Map a global atlas texel → (probe index, local interior texel, isBorder). Tiles laid out
// (ProbesX*ProbesZ) cols x ProbesY rows; tile = texels + 2*border.
struct TexelInfo { uint probe; float2 localUv; bool valid; };
TexelInfo Locate(uint2 px, uint texels) {
    uint tile = texels + 2u * BORDER;
    uint2 t = px / tile;                       // tile coords
    uint2 inTile = px % tile;                   // 0..tile-1
    TexelInfo o; o.valid = false; o.probe = 0; o.localUv = 0.0.xx;
    // Skip border texels (the blend writes interior; border is copied separately — for P2.1 we fill the
    // interior and the gather uses clamp; full border-wrap is a P2.2 refinement).
    if (inTile.x < BORDER || inTile.y < BORDER || inTile.x >= tile - BORDER || inTile.y >= tile - BORDER)
        return o;
    uint2 interior = inTile - BORDER;            // 0..texels-1
    uint col = t.x, row = t.y;                   // col = pz*ProbesX+px, row = py
    uint probesXZ = (uint)ProbeDims.x * (uint)ProbeDims.z;
    if (col >= probesXZ || row >= (uint)ProbeDims.y) return o;
    uint pz = col / (uint)ProbeDims.x, pxi = col % (uint)ProbeDims.x, py = row;
    o.probe = (pz * (uint)ProbeDims.y + py) * (uint)ProbeDims.x + pxi;
    // Match ProbeWorldPos flattening in DdgiTrace: probe = (pz*Y + py)*X + px.
    o.localUv = (float2(interior) + 0.5) / float(texels);
    o.valid = true;
    return o;
}

[numthreads(8, 8, 1)]
void CSIrradiance(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    TexelInfo info = Locate(px, IRR_TEXELS);
    if (!info.valid) return;

    float3 texelDir = OctDecode(info.localUv);
    float jitter = Hash1(info.probe * 31u + (uint)Params0.w * 2654435761u);

    float3 sum = 0.0.xxx; float wsum = 0.0;
    [loop] for (uint r = 0; r < RAYS_PER_PROBE; r++) {
        float3 rayDir = SphericalFibonacci(r, RAYS_PER_PROBE, jitter);
        float w = max(dot(texelDir, rayDir), 0.0);       // cosine: gather the hemisphere about texelDir
        if (w <= 0.0) continue;
        sum += RayData[info.probe * RAYS_PER_PROBE + r].rgb * w;
        wsum += w;
    }
    float3 result = SanitizeIrr(sum / max(wsum, 1e-4));

    float hyst = Params0.z;
    float3 prev = SanitizeIrr(IrradianceAtlas[px].rgb);
    // First frame (frameIndex 0) hard-sets; else EMA. (Atlas starts at 0 → hyst would crawl up; the warm-up
    // gate in P2.5 fixes the deterministic path. For now low hysteresis converges fast.) Both result+prev are
    // scrubbed (ternary, NOT mix*0) — once P2.3 feedback loops the atlas back through the trace, a single NaN
    // would otherwise stick in the EMA forever (the [[ssgi-nan-mix-scrub]] black-hole class).
    float3 blended = (Params0.w < 0.5) ? result : lerp(result, prev, hyst);
    IrradianceAtlas[px] = float4(blended, 1.0);
}

[numthreads(8, 8, 1)]
void CSDepth(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    TexelInfo info = Locate(px, DEPTH_TEXELS);
    if (!info.valid) return;

    float3 texelDir = OctDecode(info.localUv);
    float jitter = Hash1(info.probe * 31u + (uint)Params0.w * 2654435761u);

    float2 sum = 0.0.xx; float wsum = 0.0;
    [loop] for (uint r = 0; r < RAYS_PER_PROBE; r++) {
        float3 rayDir = SphericalFibonacci(r, RAYS_PER_PROBE, jitter);
        float w = pow(max(dot(texelDir, rayDir), 0.0), 50.0);   // sharpened: depth wants the near-axis rays
        if (w <= 0.0) continue;
        // abs(): P2.4 encodes backface hits as a NEGATIVE distance for classification; the depth MOMENTS need
        // the geometric (unsigned) distance.
        float dist = min(abs(RayData[info.probe * RAYS_PER_PROBE + r].a), Params1.x);
        sum += float2(dist, dist * dist) * w;
        wsum += w;
    }
    float2 result = SanitizeDepth(sum / max(wsum, 1e-4));

    float hyst = Params0.z;
    float2 prev = SanitizeDepth(DepthAtlas[px]);
    float2 blended = (Params0.w < 0.5) ? result : lerp(result, prev, hyst);
    DepthAtlas[px] = blended;
}

// --- P2.2 octahedral BORDER-WRAP. The gather (DdgiGather.hlsl) samples each tile with a LINEAR sampler over
// the 1px border, so the border must replicate the octahedral wrap of the opposite interior edge. For a tile
// of side TILE = texels + 2*BORDER, the standard DDGI/RTXGI border copy (Majercik 2019, RTXGI ProbeBorder):
//   - the 4 corners copy the diagonally-opposite INTERIOR corner texel;
//   - each edge texel copies the opposite-edge interior texel in REVERSED order (the octahedral seam mirrors).
// One thread per border texel (interior texels early-out). Run AFTER CSIrradiance/CSDepth, per atlas; the
// texel size (6 irr / 16 depth) comes from DdgiConstants so one shader serves both atlases via two PSOs.
void BorderCopy(uint2 px, uint texels, bool isDepth) {
    uint tile = texels + 2u * BORDER;
    uint2 inTile = px % tile;
    uint2 tileOrigin = (px / tile) * tile;
    // Interior texels are handled by CSIrradiance/CSDepth — skip.
    bool border = inTile.x < BORDER || inTile.y < BORDER || inTile.x >= tile - BORDER || inTile.y >= tile - BORDER;
    if (!border) return;
    // Validate this tile maps to a real probe (the atlas has padding tiles past ProbesX*ProbesZ x ProbesY).
    uint2 t = px / tile;
    uint probesXZ = (uint)ProbeDims.x * (uint)ProbeDims.z;
    if (t.x >= probesXZ || t.y >= (uint)ProbeDims.y) return;

    uint last = tile - 1u;                 // index of the far border row/col
    uint hi = texels;                       // index of the last interior texel (BORDER + texels - 1 = texels)
    bool cx = inTile.x == 0u || inTile.x == last;   // on a vertical (left/right) border
    bool cy = inTile.y == 0u || inTile.y == last;   // on a horizontal (top/bottom) border
    uint2 src;
    if (cx && cy) {
        // Corner → diagonally-opposite interior corner.
        src = uint2(inTile.x == 0u ? hi : BORDER, inTile.y == 0u ? hi : BORDER);
    } else if (cy) {
        // Top/bottom edge → opposite edge, reversed along X (octahedral seam mirror).
        uint mirroredX = last - inTile.x;       // reverse within [0,last]; interior of the SAME row stays
        src = uint2(mirroredX, inTile.y == 0u ? BORDER : hi);
    } else {
        // Left/right edge → opposite edge, reversed along Y.
        uint mirroredY = last - inTile.y;
        src = uint2(inTile.x == 0u ? BORDER : hi, mirroredY);
    }
    uint2 dstG = tileOrigin + inTile;
    uint2 srcG = tileOrigin + src;
    if (isDepth) DepthAtlas[dstG] = DepthAtlas[srcG];
    else         IrradianceAtlas[dstG] = IrradianceAtlas[srcG];
}

[numthreads(8, 8, 1)]
void CSBorderIrr(uint3 dtid : SV_DispatchThreadID) { BorderCopy(dtid.xy, IRR_TEXELS, false); }

[numthreads(8, 8, 1)]
void CSBorderDepth(uint3 dtid : SV_DispatchThreadID) { BorderCopy(dtid.xy, DEPTH_TEXELS, true); }

// --- P2.4 CLASSIFICATION + RELOCATION (1 thread per probe). Reduce over the probe's rays:
//   * BACKFACE ratio (rays that hit the solid side, encoded as negative distance by the trace): a probe with
//     >30% backfaces is buried in geometry → mark INACTIVE (the gather skips it; the field around it stays
//     valid because the other 7 cell probes still contribute). This kills the dead-probe darkening + leak.
//   * RELOCATION offset: push the probe AWAY from near front-face hits (toward open space) + away from the
//     mean backface direction, clamped to +-40% of the cell so the probe never crosses into a neighbour cell
//     (RTXGI ProbeRelocation). The offset is RELATIVE to the base grid position; trace/gather add it.
// Writes ProbeState[probe] = (offset.xyz, active). The offset is SMOOTHED toward the previous frame's so it
// doesn't jitter (temporal stability) — except the active flag, which is set fresh each frame.
[numthreads(64, 1, 1)]
void CSClassify(uint3 dtid : SV_DispatchThreadID) {
    uint probe = dtid.x;
    if (probe >= (uint)ProbeDims.w) return;

    float jitter = Hash1(probe * 31u + (uint)Params0.w * 2654435761u);
    float3 spacing = float3(OriginSpacingX.w, SpacingYZ.x, SpacingYZ.y);
    float cell = min(spacing.x, min(spacing.y, spacing.z));
    float nearThresh = cell * 0.5;          // a front face this close = the probe is cramped, push off it

    uint backfaces = 0, hits = 0;
    float3 push = 0.0.xxx;
    [loop] for (uint r = 0; r < RAYS_PER_PROBE; r++) {
        float3 dir = SphericalFibonacci(r, RAYS_PER_PROBE, jitter);
        float d = RayData[probe * RAYS_PER_PROBE + r].a;
        if (abs(d) >= Params1.x) continue;   // sky / far miss = open, no contribution to classification
        hits++;
        if (d < 0.0) {                        // backface hit → push strongly toward where the geometry ISN'T
            backfaces++;
            push -= dir * 1.0;                // away from the buried side
        } else if (d < nearThresh) {          // near front face → gentle push away
            push -= dir * (1.0 - d / nearThresh) * 0.5;
        }
    }

    float backRatio = hits > 0u ? float(backfaces) / float(hits) : 0.0;
    float active = backRatio > 0.30 ? 0.0 : 1.0;

    // Scale the push into world units, clamp to +-40% of the cell. Zero it for inactive probes (no point
    // relocating a buried probe — it's skipped anyway, and a fresh trace next frame may re-classify it active).
    float3 offset = 0.0.xxx;
    if (active > 0.5 && hits > 0u) {
        offset = (push / float(hits)) * cell;            // mean push, scaled by cell size
        float maxOff = 0.40 * cell;
        float len = length(offset);
        if (len > maxOff) offset *= maxOff / max(len, 1e-5);
    }
    offset = SanitizeIrr(offset);

    // Temporal smoothing of the offset (not the active flag) so it converges instead of jittering frame to
    // frame. Frame 0 hard-sets.
    float4 prev = ProbeState[probe];
    float3 prevOff = SanitizeIrr(prev.xyz);
    float3 smoothed = (Params0.w < 0.5) ? offset : lerp(offset, prevOff, 0.9);
    ProbeState[probe] = float4(smoothed, active);
}
