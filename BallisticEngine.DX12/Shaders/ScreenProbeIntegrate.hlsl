// SCREEN-SPACE RADIANCE PROBE — INTEGRATE pass (compute, SM6.6). GI plan Phase 4 (P4.0).
//
// One thread per FULL-RES pixel. Reconstruct the pixel's world normal from the G-buffer, find the screen
// probe of the pixel's tile, and sample that probe's octahedral RADIANCE map along the pixel's normal → the
// incoming diffuse irradiance E. The indirect diffuse exitance written out is albedo * E, PRE-EXPOSED into
// ssgiTarget — byte-compatible with the RT/SSGI/DDGI path, so the shared temporal + OIDN + PSCombine tail
// composites it (and GI-isolate shows it) with zero new downstream wiring.
//
// P4.1 BILATERAL upsample: gather the 4 screen probes of the pixel's 2x2 tile neighborhood, weight each by
//   - bilinear screen distance (the smooth interpolation),
//   - a DEPTH/PLANE test (reject a probe whose world position is far off the pixel's tangent plane — this is
//     what kills the 16x16 blockiness AND the silhouette halos: a background-tile probe straddling a
//     foreground edge fails the plane test and drops out), and
//   - a NORMAL test (reject probes facing away from the pixel).
// Renormalised by the surviving weight; falls back to the nearest valid probe if all 4 are rejected. The probe
// radiance is stored in PROBE-LOCAL octahedral space (+Z = probe normal); the pixel's world normal is rotated
// into each probe's frame before the octahedral lookup.
//
// Bound: CBV b0 ScreenProbeConstants; SRV t0 depth, t1 world-normal (G-buffer RT1), t2 albedo (G-buffer RT0),
// t3 radiance atlas, t4 ProbePos (validity + worldPos), t5 ProbeNormal (probe local +Z frame); UAV u0
// ssgiTarget; static linear-clamp s0.

cbuffer ScreenProbeConstants : register(b0) {
    float4x4 InvViewProj;
    float4 SpParams0;   // x probesX y probesY z downsample w frameIndex
    float4 SpParams1;   // x screenW y screenH z maxRayDist w preExposure
    float4 SpParams2;   // x irrTexels(octahedron side) y normalBias z intensity w (unused)
};

Texture2D<float>  Depth   : register(t0);
Texture2D<float4> Normal  : register(t1);
Texture2D<float4> Albedo  : register(t2);
Texture2D<float4> RadianceAtlas : register(t3);
StructuredBuffer<float4> ProbePos    : register(t4);   // (worldPos.xyz, valid)
StructuredBuffer<float4> ProbeNormal : register(t5);   // (worldNormal.xyz, 0) — the probe-local +Z axis
RWTexture2D<float4> Output : register(u0);   // ssgiTarget (pre-exposed GI)
SamplerState LinearClamp : register(s0);

static const uint BORDER = 1u;

float3 Sanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

// World dir → octahedral [0,1]^2 (matches the blend's OctDecode inverse).
float2 OctEncode(float3 dir) {
    dir /= (abs(dir.x) + abs(dir.y) + abs(dir.z));
    float2 uv = dir.xy;
    if (dir.z < 0.0)
        uv = (1.0 - abs(uv.yx)) * float2(uv.x >= 0.0 ? 1.0 : -1.0, uv.y >= 0.0 ? 1.0 : -1.0);
    return uv * 0.5 + 0.5;
}

void OrthoBasis(float3 N, out float3 T, out float3 B) {
    float s = N.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (s + N.z);
    float b = N.x * N.y * a;
    T = float3(1.0 + s * N.x * N.x * a, s * b, -s * N.x);
    B = float3(b, s + N.y * N.y * a, -N.y);
}

// Atlas UV for probe `probe` sampling local direction `localDir`, given the octahedral tile side `texels`.
float2 ProbeAtlasUv(uint probe, float3 localDir, uint texels, float2 atlasSize) {
    uint tile = texels + 2u * BORDER;
    uint probesX = (uint)SpParams0.x;
    uint col = probe % probesX, row = probe / probesX;
    float2 oct = OctEncode(localDir);
    float2 interiorPx = oct * float(texels);
    float2 texelXY = float2(col * tile, row * tile) + float2(BORDER, BORDER) + interiorPx;
    return texelXY / atlasSize;
}

// Sample probe `probe`'s octahedral radiance along world normal N (rotate N into the probe's local frame).
float3 SampleProbe(uint probe, float3 N, uint texels, float2 atlasSize) {
    float3 pN = normalize(ProbeNormal[probe].xyz);
    float3 T, B; OrthoBasis(pN, T, B);
    float3 localN = float3(dot(N, T), dot(N, B), dot(N, pN));
    return Sanitize(RadianceAtlas.SampleLevel(LinearClamp, ProbeAtlasUv(probe, localN, texels, atlasSize), 0).rgb);
}

[numthreads(8, 8, 1)]
void CSIntegrate(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    float w = SpParams1.x, h = SpParams1.y;
    if (px.x >= (uint)w || px.y >= (uint)h) return;
    float2 uv = (float2(px) + 0.5) / float2(w, h);

    Output[px] = float4(0, 0, 0, 1);   // default: no GI (sky / un-shaded)
    float depth = Depth.SampleLevel(LinearClamp, uv, 0).r;
    if (depth >= 1.0) return;
    float3 worldN = Normal.SampleLevel(LinearClamp, uv, 0).rgb * 2.0 - 1.0;
    if (dot(worldN, worldN) < 0.1) return;
    float3 N = normalize(worldN);
    float3 albedo = Albedo.SampleLevel(LinearClamp, uv, 0).rgb;

    // Pixel world position (for the depth/plane reject).
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 wp = mul(ndc, InvViewProj);
    float3 worldPos = wp.xyz / wp.w;

    uint ds = (uint)SpParams0.z;
    uint probesX = (uint)SpParams0.x, probesY = (uint)SpParams0.y;
    uint texels = (uint)SpParams2.x;
    float2 atlasSize = float2(probesX, probesY) * float(texels + 2u * BORDER);

    // --- BILATERAL 4x4-PROBE GATHER (Lumen-style spatial filter in radiance-cache space). The pixel sits at
    // fractional grid coords (px+0.5)/ds - 0.5. The old gather used only the 2x2 enclosing probes — too few to
    // smooth the per-probe Monte-Carlo noise (each probe is one frame of 64 jittered rays). Widening to the 4x4
    // neighbourhood SHARES radiance across ~16 probes, which is exactly how Lumen kills noise: average the
    // precious traces over a probe neighbourhood, weighted so edges/silhouettes don't bleed:
    //   - a smooth GAUSSIAN spatial falloff (replaces bilinear; the 2 inner probes dominate, outer ones feather),
    //   - the SAME depth/plane test (a probe off the pixel's tangent plane drops out → no halo across edges),
    //   - the SAME normal test (a probe facing away drops out).
    // Renormalised by the surviving weight; nearest-probe fallback when all are rejected (a thin silhouette).
    float2 g = (float2(px) + 0.5) / float(ds) - 0.5;   // probe-grid coords (probe centre = integer)
    int2 gc = (int2)round(g);                          // nearest probe cell (centre of the 4x4 window)

    // A plane-distance scale: how far off the pixel's tangent plane a probe may be before it's rejected. Tie it
    // to the world spacing between probes (≈ depth*tile/focal — approximate with a fraction of the view depth).
    float planeScale = max(length(worldPos) * 0.05, 0.05);

    float3 sumE = 0.0.xxx; float sumW = 0.0;
    uint nearestProbe = 0; float nearestW = -1.0;
    [unroll] for (int dy = -1; dy <= 2; dy++)
    [unroll] for (int dx = -1; dx <= 2; dx++) {
        int2 c = gc + int2(dx, dy);
        if (c.x < 0 || c.y < 0 || c.x >= (int)probesX || c.y >= (int)probesY) continue;
        uint probe = (uint)c.y * probesX + (uint)c.x;
        float4 pp = ProbePos[probe];
        if (pp.w < 0.5) continue;   // probe landed on sky

        // Smooth spatial falloff: Gaussian in probe-grid distance from the pixel's fractional position.
        float2 d2 = (float2)c - g;
        float spatial = exp(-dot(d2, d2) * 0.5);

        // Plane/depth test: distance of the probe's world position from the pixel's tangent plane.
        float planeDist = abs(dot(pp.xyz - worldPos, N));
        float wPlane = exp(-planeDist / planeScale);

        // Normal test: probe must face roughly the same way as the pixel.
        float3 pN = normalize(ProbeNormal[probe].xyz);
        float wNormal = saturate(dot(pN, N));
        wNormal = wNormal * wNormal;

        float weight = spatial * wPlane * wNormal;
        if (weight > nearestW) { nearestW = weight; nearestProbe = probe; }   // best for the fallback
        if (weight < 1e-5) continue;

        sumE += SampleProbe(probe, N, texels, atlasSize) * weight;
        sumW += weight;
    }

    float3 E;
    if (sumW > 1e-4) E = sumE / sumW;
    else if (nearestW >= 0.0) E = SampleProbe(nearestProbe, N, texels, atlasSize);   // all rejected → nearest
    else { Output[px] = float4(0, 0, 0, 1); return; }   // no valid probe → WRITE BLACK (was `return`, leaving the
                                                        // UAV's stale value = the torn/garbage speckle on edges)

    // Firefly clamp on the irradiance BEFORE the albedo product: a single probe texel near the fp16 ceiling (a
    // bright bounce hit) upsampled to full-res shows as the white speckle dotting the walls/statue. Cap E to a
    // soft multiple of its own luma's neighbourhood — here a hard but high ceiling kills the Inf-class spikes
    // while leaving legitimate bright bounce intact (the shared OIDN + temporal pass smooth the rest).
    E = Sanitize(E);
    float eLuma = dot(E, float3(0.2126, 0.7152, 0.0722));
    if (eLuma > 8000.0) E *= 8000.0 / max(eLuma, 1e-4);   // clamp raw-HDR irradiance fireflies

    // Indirect diffuse exitance = albedo * E, pre-exposed to the ssgiTarget contract (PSCombine multiplies by
    // 1/preExp + Intensity). NOT *intensity here — PSCombine applies SsgiIntensity.
    float3 outRgb = Sanitize(albedo * E) * SpParams1.w;
    Output[px] = float4(outRgb, 1.0);
}
