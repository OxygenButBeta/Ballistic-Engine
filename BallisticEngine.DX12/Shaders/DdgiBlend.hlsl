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
    float4 Params2;          // P2.5 round-robin: x updateFraction(N), y phase, z fullUpdate(1/0), w pad
    float4 Params3;          // CHUNK1 bake: xyz camera world pos, w band width (m)
    float4 Params4;          // CHUNK1 bake: x bakeEnable, y bakeWave (open band), z convergeTarget, w pad
};

StructuredBuffer<float4> RayData     : register(t0);   // [probe * RaysPerProbe + ray] = (radiance, dist)
StructuredBuffer<uint>   ProbeBake   : register(t1);   // CHUNK1: per-probe converged-frame counter (read-only here)

// P2.5 ROUND-ROBIN / CHUNK1 PROGRESSIVE BAKE: blend/classify only the probes traced this frame. MUST match
// DdgiTrace.ProbeActiveThisFrame exactly — blending a probe whose RayData is stale would EMA garbage into its
// tile. In bake mode the SAME band + converged test (the converged counter the trace already bumped this frame
// is one step ahead, so use > target-1, i.e. >= target means it was JUST frozen this frame — still blend that
// last result; only skip once it was frozen on a PRIOR frame). Simpler + exact: eligible iff band opened AND
// counter <= target (the trace bumped it to <=target for the frame it last traced).
bool ProbeActiveThisFrame(uint probe) {
    if (Params4.x > 0.5) {                                 // CHUNK1 progressive bake
        if (ProbeBake[probe] > (uint)Params4.z) return false;   // frozen on a prior frame → tile already final
        uint px = probe % (uint)ProbeDims.x;
        uint py = (probe / (uint)ProbeDims.x) % (uint)ProbeDims.y;
        uint pz = probe / ((uint)ProbeDims.x * (uint)ProbeDims.y);
        float3 basePos = OriginSpacingX.xyz + float3(px * OriginSpacingX.w, py * SpacingYZ.x, pz * SpacingYZ.y);
        uint band = (uint)floor(length(basePos - Params3.xyz) / max(Params3.w, 0.5));
        return band <= (uint)Params4.y;
    }
    if (Params2.z > 0.5) return true;
    uint n = max((uint)Params2.x, 1u);
    return (probe % n) == (uint)Params2.y;
}
RWTexture2D<float4> IrradianceAtlas : register(u0);   // CSIrradiance target
RWTexture2D<float2> DepthAtlas      : register(u1);   // CSDepth target (distinct register; one bound per pass)
RWStructuredBuffer<float4> ProbeState : register(u2); // CSClassify target: xyz = relocation offset (world), w = active(1/0)

static const float PI = 3.14159265359;
// CHUNK2: active ray count rides Params4.w (144 live / 256 baked) — MUST match DdgiTrace.RaysPerProbe(). The
// blend loops + RayData indexing use this so the integral matches the rays the trace actually wrote.
uint RaysPerProbe() { return clamp((uint)Params4.w, 16u, 256u); }
// CHUNK2: oct texel counts ride DdgiConstants (Params0.x irr / Params0.y depth) so the C# atlas size + the
// gather UV math + this blend stay in lockstep when fidelity is raised (no static-const drift).
uint IrrTexels()   { return (uint)Params0.x; }
uint DepthTexels() { return (uint)Params0.y; }
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
    TexelInfo info = Locate(px, IrrTexels());
    if (!info.valid) return;
    if (!ProbeActiveThisFrame(info.probe)) return;   // P2.5 round-robin: keep stale tile, don't EMA stale rays

    float3 texelDir = OctDecode(info.localUv);
    float jitter = Hash1(info.probe * 31u + (uint)Params0.w * 2654435761u);

    float3 sum = 0.0.xxx; float wsum = 0.0;
    [loop] for (uint r = 0; r < RaysPerProbe(); r++) {
        float3 rayDir = SphericalFibonacci(r, RaysPerProbe(), jitter);
        float w = max(dot(texelDir, rayDir), 0.0);       // cosine: gather the hemisphere about texelDir
        if (w <= 0.0) continue;
        sum += RayData[info.probe * RaysPerProbe() + r].rgb * w;
        wsum += w;
    }
    float3 result = SanitizeIrr(sum / max(wsum, 1e-4));

    float hyst = Params0.z;
    float4 prev4 = IrradianceAtlas[px];
    float3 prev = SanitizeIrr(prev4.rgb);
    // Hard-set on a probe's FIRST write (the atlas tile's alpha is the written-flag: init 0, the blend writes
    // 1.0), else EMA. PER-PROBE first-touch (not the global frame 0) so a round-robin probe whose first update
    // lands on a non-zero frame still snaps to its value instead of crawling up from black over ~33 frames.
    // Both result+prev are ternary-scrubbed (NOT mix*0) — once P2.3 feedback loops the atlas back through the
    // trace, one NaN would otherwise stick in the EMA forever ([[ssgi-nan-mix-scrub]] black-hole class).
    bool firstWrite = (Params0.w < 0.5) || (prev4.a < 0.5);
    float3 blended = firstWrite ? result : lerp(result, prev, hyst);
    IrradianceAtlas[px] = float4(blended, 1.0);
}

[numthreads(8, 8, 1)]
void CSDepth(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    TexelInfo info = Locate(px, DepthTexels());
    if (!info.valid) return;
    if (!ProbeActiveThisFrame(info.probe)) return;   // P2.5 round-robin (must match CSIrradiance)

    float3 texelDir = OctDecode(info.localUv);
    float jitter = Hash1(info.probe * 31u + (uint)Params0.w * 2654435761u);

    float2 sum = 0.0.xx; float wsum = 0.0;
    [loop] for (uint r = 0; r < RaysPerProbe(); r++) {
        float3 rayDir = SphericalFibonacci(r, RaysPerProbe(), jitter);
        float w = pow(max(dot(texelDir, rayDir), 0.0), 50.0);   // sharpened: depth wants the near-axis rays
        if (w <= 0.0) continue;
        // abs(): P2.4 encodes backface hits as a NEGATIVE distance for classification; the depth MOMENTS need
        // the geometric (unsigned) distance.
        float dist = min(abs(RayData[info.probe * RaysPerProbe() + r].a), Params1.x);
        sum += float2(dist, dist * dist) * w;
        wsum += w;
    }
    float2 result = SanitizeDepth(sum / max(wsum, 1e-4));

    float hyst = Params0.z;
    float2 prev = SanitizeDepth(DepthAtlas[px]);
    // Per-probe first-touch: a never-written depth tile reads mean=0 (any real surface or open-sky probe stores
    // mean>0 within maxRayDist), so prev.x<=0 means uninitialised → hard-set (matches CSIrradiance's alpha
    // flag), so round-robin probes don't crawl up from 0 over ~33 frames.
    bool firstWrite = (Params0.w < 0.5) || (prev.x <= 0.0);
    float2 blended = firstWrite ? result : lerp(result, prev, hyst);
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
void CSBorderIrr(uint3 dtid : SV_DispatchThreadID) { BorderCopy(dtid.xy, IrrTexels(), false); }

[numthreads(8, 8, 1)]
void CSBorderDepth(uint3 dtid : SV_DispatchThreadID) { BorderCopy(dtid.xy, DepthTexels(), true); }

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
    // P2.5 round-robin: classify only probes traced this frame (their RayData is fresh). Inactive probes keep
    // last frame's ProbeState — correct, since they weren't re-traced (same as the atlas tiles).
    if (!ProbeActiveThisFrame(probe)) return;

    float jitter = Hash1(probe * 31u + (uint)Params0.w * 2654435761u);
    float3 spacing = float3(OriginSpacingX.w, SpacingYZ.x, SpacingYZ.y);
    float cell = min(spacing.x, min(spacing.y, spacing.z));
    float nearThresh = cell * 0.5;          // a front face this close = the probe is cramped, push off it

    uint backfaces = 0, hits = 0;
    float3 push = 0.0.xxx;
    [loop] for (uint r = 0; r < RaysPerProbe(); r++) {
        float3 dir = SphericalFibonacci(r, RaysPerProbe(), jitter);
        float d = RayData[probe * RaysPerProbe() + r].a;
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
