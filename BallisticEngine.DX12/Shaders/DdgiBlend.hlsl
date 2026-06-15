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
    float4 Params1;          // x maxRayDist, y normalBias, z viewBias, w intensity
};
StructuredBuffer<float4> RayData : register(t0);   // [probe * RaysPerProbe + ray] = (radiance, dist)
RWTexture2D<float4> IrradianceAtlas : register(u0);   // CSIrradiance target
RWTexture2D<float2> DepthAtlas      : register(u1);   // CSDepth target (distinct register; one bound per pass)

static const float PI = 3.14159265359;
static const uint RAYS_PER_PROBE = 144u;
static const uint IRR_TEXELS = 6u;
static const uint DEPTH_TEXELS = 16u;
static const uint BORDER = 1u;

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
    float3 result = sum / max(wsum, 1e-4);

    float hyst = Params0.z;
    float3 prev = IrradianceAtlas[px].rgb;
    // First frame (frameIndex 0) hard-sets; else EMA. (Atlas starts at 0 → hyst would crawl up; the warm-up
    // gate in P2.5 fixes the deterministic path. For now low hysteresis converges fast.)
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
        float dist = min(RayData[info.probe * RAYS_PER_PROBE + r].a, Params1.x);
        sum += float2(dist, dist * dist) * w;
        wsum += w;
    }
    float2 result = sum / max(wsum, 1e-4);

    float hyst = Params0.z;
    float2 prev = DepthAtlas[px];
    float2 blended = (Params0.w < 0.5) ? result : lerp(result, prev, hyst);
    DepthAtlas[px] = blended;
}
