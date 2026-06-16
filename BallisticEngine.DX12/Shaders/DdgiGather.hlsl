// DDGI shade-time GATHER (compute, SM6.6) — GI plan P2.2. Per primary-surface pixel, reconstruct the world
// position + normal from the G-buffer, find the 8 probes of the enclosing grid cell, and accumulate their
// stored octahedral IRRADIANCE — trilinear over the cell, weighted by:
//   - a cosine (front-facing) wrap term per probe (a probe behind the surface contributes nothing),
//   - the CHEBYSHEV variance visibility test from the depth-moments atlas (THE leak gate — a probe on the
//     far side of a thin wall is statistically occluded and dropped), and
//   - a small normal/view bias on the sample position (acne / self-occlusion guard).
// The result is the incoming diffuse irradiance E at the pixel; the indirect diffuse exitance written out is
// albedo * E, PRE-EXPOSED (Params.x) into ssgiTarget — byte-compatible with the RT/SSGI near-field path, so
// the existing temporal + OIDN + PSCombine chain composites it (and GI-isolate shows it) with zero new wiring.
//
// Bound: CBV b0 DdgiConstants (the grid), CBV b1 DdgiGatherExtra (InvViewProj + preExp + screen); SRV t0
// depth, t1 world-normal, t2 albedo (G-buffer RT0), t3 irradiance atlas, t4 depth atlas; UAV u0 ssgiTarget;
// static linear-clamp sampler s0.

cbuffer DdgiConstants : register(b0) {
    float4 OriginSpacingX;   // xyz grid origin (world), w spacing.x
    float4 SpacingYZ;        // x spacing.y, y spacing.z
    float4 ProbeDims;        // xyz (ProbesX,ProbesY,ProbesZ), w ProbeCount
    float4 Params0;          // x irrTexels, y depthTexels, z hysteresis, w frameIndex
    float4 Params1;          // x maxRayDist, y normalBias, z feedbackEnable, w intensity
    float4 Params2;          // P2.5 round-robin (unused by the gather — it reads the persistent atlas every
                             // frame for every pixel; present only to match the DdgiConstants CBV layout)
};
cbuffer DdgiGatherExtra : register(b1) {
    float4x4 InvViewProj;    // screen+depth -> world (jittered, transposed)
    float4 GParams;          // x preExposure, y screenW, z screenH, w (unused)
};

Texture2D<float>  Depth     : register(t0);
Texture2D<float4> Normal    : register(t1);   // primary-surface world normal packed [0,1]
Texture2D<float4> Albedo    : register(t2);   // G-buffer RT0 (albedo.rgb + specF0.a)
Texture2D<float4> IrrAtlas  : register(t3);
Texture2D<float2> DepthAtlas : register(t4);
StructuredBuffer<float4> ProbeState : register(t5);   // P2.4: per-probe (relocation offset.xyz, active)
RWTexture2D<float4> Output  : register(u0);   // ssgiTarget (pre-exposed GI)
SamplerState LinearClamp : register(s0);

static const float PI = 3.14159265359;
static const uint BORDER = 1u;

float3 Sanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

// Octahedral ENCODE: unit dir -> [0,1]^2 tile UV (inverse of the blend's OctDecode).
float2 OctEncode(float3 dir) {
    dir /= (abs(dir.x) + abs(dir.y) + abs(dir.z));
    float2 uv = dir.xy;
    if (dir.z < 0.0) {
        uv = (1.0 - abs(uv.yx)) * float2(uv.x >= 0.0 ? 1.0 : -1.0, uv.y >= 0.0 ? 1.0 : -1.0);
    }
    return uv * 0.5 + 0.5;
}

// Atlas UV for probe `probe`, sampling direction `dir`, given the per-probe tile `texels` (+border). The tile
// layout MUST match DdgiBlend.Locate: col = pz*ProbesX + px, row = py; tile = texels + 2*BORDER. The interior
// occupies [BORDER, BORDER+texels); the gather samples within the interior (the border lets bilinear wrap).
float2 ProbeAtlasUv(uint px, uint py, uint pz, float3 dir, uint texels, float2 atlasSize) {
    uint tile = texels + 2u * BORDER;
    uint col = pz * (uint)ProbeDims.x + px;
    uint row = py;
    float2 oct = OctEncode(dir);                          // [0,1] within the interior
    float2 interiorPx = oct * float(texels);             // [0,texels]
    float2 texelXY = float2(col * tile, row * tile) + float2(BORDER, BORDER) + interiorPx;
    return texelXY / atlasSize;
}

// Flat probe index (matches DdgiTrace ProbeWorldPos / blend Locate flatten).
uint ProbeIndex(uint px, uint py, uint pz) {
    return (pz * (uint)ProbeDims.y + py) * (uint)ProbeDims.x + px;
}
// Probe world position + the P2.4 relocation offset (matches DdgiTrace ProbeWorldPos).
float3 ProbeWorldPos(uint px, uint py, uint pz) {
    float3 basePos = OriginSpacingX.xyz + float3(px * OriginSpacingX.w, py * SpacingYZ.x, pz * SpacingYZ.y);
    return basePos + ProbeState[ProbeIndex(px, py, pz)].xyz;
}

[numthreads(8, 8, 1)]
void CSGather(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    float w = GParams.y, h = GParams.z;
    if (px.x >= (uint)w || px.y >= (uint)h) return;
    float2 uv = (float2(px) + 0.5) / float2(w, h);

    Output[px] = float4(0, 0, 0, 1);   // default: no GI (sky / un-shaded)
    float depth = Depth.SampleLevel(LinearClamp, uv, 0).r;
    if (depth >= 1.0) return;
    float3 worldN = Normal.SampleLevel(LinearClamp, uv, 0).rgb * 2.0 - 1.0;
    if (dot(worldN, worldN) < 0.1) return;
    float3 N = normalize(worldN);

    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 wp = mul(ndc, InvViewProj);
    float3 worldPos = wp.xyz / wp.w;

    float3 albedo = Albedo.SampleLevel(LinearClamp, uv, 0).rgb;

    float3 spacing = float3(OriginSpacingX.w, SpacingYZ.x, SpacingYZ.y);
    // Bias the sample position off the surface along the normal so the trilinear cell pick AND the Chebyshev
    // distance compare share ONE origin (RTXGI surfaceBias) → no self-occlusion at the receiver.
    float3 biasPos = worldPos + N * Params1.y;

    float3 rel = (biasPos - OriginSpacingX.xyz) / spacing;   // grid coords
    int3 baseCoord = (int3)floor(rel);
    float3 frac3 = rel - (float3)baseCoord;

    int3 dims = int3((int)ProbeDims.x, (int)ProbeDims.y, (int)ProbeDims.z);
    float2 irrAtlasSize = float2((uint)ProbeDims.x * (uint)ProbeDims.z, (uint)ProbeDims.y) * float((uint)Params0.x + 2u * BORDER);
    float2 depAtlasSize = float2((uint)ProbeDims.x * (uint)ProbeDims.z, (uint)ProbeDims.y) * float((uint)Params0.y + 2u * BORDER);

    float3 sumIrr = 0.0.xxx;
    float sumW = 0.0;
    [unroll] for (int i = 0; i < 8; i++) {
        int3 off = int3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        int3 c = baseCoord + off;
        if (any(c < 0) || any(c >= dims)) continue;
        uint cx = (uint)c.x, cy = (uint)c.y, cz = (uint)c.z;

        // P2.4: skip probes classified INACTIVE (buried in geometry → garbage field). The remaining cell
        // probes still light the point; the trilinear renormalizes by sumW.
        if (ProbeState[ProbeIndex(cx, cy, cz)].w < 0.5) continue;

        float3 probePos = ProbeWorldPos(cx, cy, cz);
        float3 toProbe = probePos - biasPos;   // biasPos: shared origin for cell pick + Chebyshev (RTXGI)
        float distToProbe = length(toProbe);
        float3 dirToProbe = distToProbe > 1e-5 ? toProbe / distToProbe : N;

        // Trilinear weight (per-axis lerp of frac).
        float3 triv = lerp(1.0 - frac3, frac3, (float3)off);
        float trilinear = triv.x * triv.y * triv.z;

        // Cosine wrap: front-facing probes weigh more; a probe behind the surface ~0 (RTXGI "wrapShading").
        float wrap = saturate(dot(dirToProbe, N) * 0.5 + 0.5);
        wrap = wrap * wrap + 0.2;

        // Chebyshev variance visibility (the LEAK gate). Sample the depth moments along the probe->surface
        // direction (the surface is what the depth tile must "see"); compare the stored mean distance to the
        // actual distance. If the surface is farther than the probe statistically sees, it's occluded.
        float3 biasDir = normalize(-dirToProbe);          // from probe toward the surface
        float2 mom = DepthAtlas.SampleLevel(LinearClamp, ProbeAtlasUv(cx, cy, cz, biasDir, (uint)Params0.y, depAtlasSize), 0).rg;
        float meanDist = mom.x;
        float vis = 1.0;
        if (distToProbe > meanDist) {
            float variance = abs(mom.x * mom.x - mom.y);
            float diff = distToProbe - meanDist;
            vis = variance / (variance + diff * diff);     // Chebyshev upper bound
            vis = max(vis * vis * vis, 0.0);               // sharpen (RTXGI) — kill faint leaks
        }

        float weight = trilinear * wrap * vis;
        if (weight < 1e-6) continue;

        // Irradiance: sample along the surface NORMAL (the hemisphere the receiver integrates).
        float3 irr = IrrAtlas.SampleLevel(LinearClamp, ProbeAtlasUv(cx, cy, cz, N, (uint)Params0.x, irrAtlasSize), 0).rgb;
        sumIrr += Sanitize(irr) * weight;
        sumW += weight;
    }

    float3 E = sumW > 1e-5 ? sumIrr / sumW : 0.0.xxx;
    // Indirect diffuse exitance = albedo * E. The 1/PI normalisation is folded into how the irradiance was
    // accumulated (cosine-weighted ray average), matching RTXGI's "multiply by albedo" convention. Pre-expose
    // to match the RT/SSGI ssgiTarget contract (PSCombine multiplies by 1/preExp + Intensity). NOT *intensity
    // here — PSCombine applies Combine0.x (SsgiIntensity).
    float3 outRgb = Sanitize(albedo * E) * GParams.x;
    Output[px] = float4(outRgb, 1.0);
}
