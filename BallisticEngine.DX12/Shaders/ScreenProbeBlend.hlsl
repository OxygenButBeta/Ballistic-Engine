// SCREEN-SPACE RADIANCE PROBE — BLEND pass (compute, SM6.6). GI plan Phase 4 (P4.0).
//
// Integrates the per-ray (radiance, distance) data (from ScreenProbeTrace.hlsl) into each screen probe's
// OCTAHEDRAL RADIANCE tile (8x8 interior + 1px border). One thread per atlas texel: each octahedral texel
// maps to a direction; accumulate the rays whose direction is in that texel's hemisphere, cosine-weighted, →
// the incoming radiance from that direction. Unlike the DDGI blend, P4.0 does NO temporal EMA — the screen
// probe is re-placed (jittered) and re-traced every frame, so the tile is recomputed each frame; the shared
// downstream temporal+OIDN tail (around the integrate output) handles temporal stability. A 1px octahedral
// border is filled (CSBorder) so the integrate's bilinear sampling wraps correctly.
//
// Bound: CBV b0 ScreenProbeConstants; SRV t0 RayData (the trace output), t1 ProbePos (validity); UAV u0 the
// radiance atlas. Dispatched once over the atlas (CSIntegrate), then once for the border (CSBorder).

cbuffer ScreenProbeConstants : register(b0) {
    float4x4 InvViewProj;
    float4 SpParams0;   // x probesX y probesY z downsample w frameIndex
    float4 SpParams1;   // x screenW y screenH z maxRayDist w preExposure
    float4 SpParams2;   // x irrTexels(octahedron side) y normalBias z intensity w (unused)
};

StructuredBuffer<float4> RayData  : register(t0);   // [probe * RAYS + ray] = (radiance, dist)
StructuredBuffer<float4> ProbePos : register(t1);   // (worldPos.xyz, valid)
RWTexture2D<float4> RadianceAtlas : register(u0);

static const uint RAYS_PER_PROBE = 64u;
static const uint OCT_TEXELS = 8u;     // octahedral tile side (interior). MUST match SpParams2.x / the atlas alloc.
static const uint BORDER = 1u;

float3 Sanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

// Octahedral [0,1]^2 → unit dir (matches DdgiBlend.OctDecode; the trace's rays live on the full sphere but the
// hemisphere rays only ever populate the upper texels — lower texels stay near zero, which is correct).
float3 OctDecode(float2 f) {
    f = f * 2.0 - 1.0;
    float3 n = float3(f.x, f.y, 1.0 - abs(f.x) - abs(f.y));
    float t = saturate(-n.z);
    n.xy += n.xy >= 0.0 ? -t : t;
    return normalize(n);
}

// The trace's ray-direction generators (must match ScreenProbeTrace exactly to weight the right rays). The
// blend reconstructs the WORLD ray direction by rebuilding the probe's tangent frame from its stored normal.
float3 HemisphereFibonacci(uint i, uint n, float jitter) {
    float phi = 2.39996323 * (float(i) + jitter);
    float cosT = sqrt(1.0 - (float(i) + 0.5) / float(n));
    float sinT = sqrt(saturate(1.0 - cosT * cosT));
    return float3(cos(phi) * sinT, sin(phi) * sinT, cosT);
}
float Hash1(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}

// Atlas texel → (probe index, local interior UV, isBorder). Tiles laid out probesX cols x probesY rows.
struct TexelInfo { uint probe; float2 localUv; bool valid; };
TexelInfo Locate(uint2 px, uint texels) {
    uint tile = texels + 2u * BORDER;
    uint2 t = px / tile;
    uint2 inTile = px % tile;
    TexelInfo o; o.valid = false; o.probe = 0; o.localUv = 0.0.xx;
    uint probesX = (uint)SpParams0.x, probesY = (uint)SpParams0.y;
    if (t.x >= probesX || t.y >= probesY) return o;
    if (inTile.x < BORDER || inTile.y < BORDER || inTile.x >= tile - BORDER || inTile.y >= tile - BORDER)
        return o;   // border texel (CSBorder fills it)
    uint2 interior = inTile - BORDER;
    o.probe = t.y * probesX + t.x;
    o.localUv = (float2(interior) + 0.5) / float(texels);
    o.valid = true;
    return o;
}

[numthreads(8, 8, 1)]
void CSIntegrate(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    TexelInfo info = Locate(px, OCT_TEXELS);
    if (!info.valid) return;

    // Invalid (sky) probe → zero tile (still write so a stale value doesn't linger from a previous frame's
    // surface probe at the same index when the camera moves).
    if (ProbePos[info.probe].w < 0.5) { RadianceAtlas[px] = float4(0, 0, 0, 1); return; }

    // The octahedral tile is stored in PROBE-LOCAL space (+Z = the probe normal). The trace generates its rays
    // in that SAME local frame (HemisphereFibonacci about +Z, then rotated into world by the probe's tangent
    // frame). So both the texel direction AND the rays here are local — no world transform, no need to bind the
    // probe normal. The integrate pass does the inverse (rebuilds the world frame to sample along the pixel N).
    float3 texelDir = OctDecode(info.localUv);   // probe-LOCAL hemisphere direction this texel represents

    float jitter = Hash1(info.probe * 31u + (uint)SpParams0.w * 2654435761u);
    float3 sum = 0.0.xxx; float wsum = 0.0;
    [loop] for (uint r = 0; r < RAYS_PER_PROBE; r++) {
        float3 rayLocal = HemisphereFibonacci(r, RAYS_PER_PROBE, jitter);   // LOCAL (+Z = normal)
        float w = max(dot(texelDir, rayLocal), 0.0);   // cosine: gather the hemisphere about texelDir (local)
        if (w <= 0.0) continue;
        sum += Sanitize(RayData[info.probe * RAYS_PER_PROBE + r].rgb) * w;
        wsum += w;
    }
    float3 result = Sanitize(sum / max(wsum, 1e-4));
    RadianceAtlas[px] = float4(result, 1.0);
}

// 1px octahedral border-wrap (same scheme as DdgiBlend.BorderCopy) so the integrate's bilinear sampling wraps.
[numthreads(8, 8, 1)]
void CSBorder(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    uint texels = OCT_TEXELS;
    uint tile = texels + 2u * BORDER;
    uint2 inTile = px % tile;
    uint2 tileOrigin = (px / tile) * tile;
    bool border = inTile.x < BORDER || inTile.y < BORDER || inTile.x >= tile - BORDER || inTile.y >= tile - BORDER;
    if (!border) return;
    uint2 t = px / tile;
    uint probesX = (uint)SpParams0.x, probesY = (uint)SpParams0.y;
    if (t.x >= probesX || t.y >= probesY) return;

    uint last = tile - 1u;
    uint hi = texels;   // last interior index (BORDER + texels - 1 = texels)
    bool cx = inTile.x == 0u || inTile.x == last;
    bool cy = inTile.y == 0u || inTile.y == last;
    uint2 src;
    if (cx && cy) {
        src = uint2(inTile.x == 0u ? hi : BORDER, inTile.y == 0u ? hi : BORDER);
    } else if (cy) {
        uint mirroredX = last - inTile.x;
        src = uint2(mirroredX, inTile.y == 0u ? BORDER : hi);
    } else {
        uint mirroredY = last - inTile.y;
        src = uint2(inTile.x == 0u ? BORDER : hi, mirroredY);
    }
    RadianceAtlas[tileOrigin + inTile] = RadianceAtlas[tileOrigin + src];
}
