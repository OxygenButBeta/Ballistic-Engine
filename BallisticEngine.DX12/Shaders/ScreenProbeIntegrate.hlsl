// SCREEN-SPACE RADIANCE PROBE — INTEGRATE pass (compute, SM6.6). GI plan Phase 4 (P4.0).
//
// One thread per FULL-RES pixel. Reconstruct the pixel's world normal from the G-buffer, find the screen
// probe of the pixel's tile, and sample that probe's octahedral RADIANCE map along the pixel's normal → the
// incoming diffuse irradiance E. The indirect diffuse exitance written out is albedo * E, PRE-EXPOSED into
// ssgiTarget — byte-compatible with the RT/SSGI/DDGI path, so the shared temporal + OIDN + PSCombine tail
// composites it (and GI-isolate shows it) with zero new downstream wiring.
//
// P4.0 is the NAIVE integrate: nearest single probe (the pixel's own tile), no bilateral upsample → expect
// blocky 16x16 GI. The bilateral depth+normal upsample over the 4 nearest probes is P4.1 (the quality pass).
// The probe's radiance is stored in PROBE-LOCAL octahedral space (+Z = probe normal); we transform the pixel's
// world normal into that local frame before the octahedral lookup.
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

    // P4.0 NAIVE: the pixel's own tile probe. (Bilateral over the 4 nearest = P4.1.)
    uint ds = (uint)SpParams0.z;
    uint probesX = (uint)SpParams0.x, probesY = (uint)SpParams0.y;
    uint pbx = min(px.x / ds, probesX - 1u);
    uint pby = min(px.y / ds, probesY - 1u);
    uint probe = pby * probesX + pbx;

    if (ProbePos[probe].w < 0.5) return;   // probe landed on sky → no GI here

    // Transform the pixel's world normal into the probe's LOCAL octahedral frame (+Z = the probe's normal).
    float3 pN = normalize(ProbeNormal[probe].xyz);
    float3 T, B; OrthoBasis(pN, T, B);
    float3 localN = float3(dot(N, T), dot(N, B), dot(N, pN));

    uint texels = (uint)SpParams2.x;
    float2 atlasSize = float2(probesX, probesY) * float(texels + 2u * BORDER);
    float3 E = RadianceAtlas.SampleLevel(LinearClamp, ProbeAtlasUv(probe, localN, texels, atlasSize), 0).rgb;
    E = Sanitize(E);

    // Indirect diffuse exitance = albedo * E, pre-exposed to the ssgiTarget contract (PSCombine multiplies by
    // 1/preExp + Intensity). NOT *intensity here — PSCombine applies SsgiIntensity.
    float3 outRgb = Sanitize(albedo * E) * SpParams1.w;
    Output[px] = float4(outRgb, 1.0);
}
