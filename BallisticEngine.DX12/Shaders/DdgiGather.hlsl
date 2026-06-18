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

// CHUNK3 CASCADE: a DDGI grid description (one per cascade). The gather samples the NEAR cascade first and
// falls back to the FAR cascade where near has no coverage (the receiver is outside the near grid, or all 8
// near probes are occluded/inactive). The struct mirrors the original DdgiConstants layout so the same C#
// DdgiConstants.GridConstants() fills both b0 (near) and b2 (far).
struct DdgiGrid {
    float4 OriginSpacingX;   // xyz grid origin (world), w spacing.x
    float4 SpacingYZ;        // x spacing.y, y spacing.z
    float4 ProbeDims;        // xyz (ProbesX,ProbesY,ProbesZ), w ProbeCount
    float4 Params0;          // x irrTexels, y depthTexels, z hysteresis, w frameIndex
    float4 Params1;          // x maxRayDist, y normalBias, z feedbackEnable, w intensity
    float4 Params2;          // round-robin (unused by the gather)
    float4 Params3;          // bake cam/band (unused by the gather)
    float4 Params4;          // bake state (unused by the gather)
};
cbuffer NearGrid : register(b0) { DdgiGrid GNear; };
cbuffer FarGrid  : register(b2) { DdgiGrid GFar; };   // CHUNK3: second cascade (sparse, wide)
cbuffer DdgiGatherExtra : register(b1) {
    float4x4 InvViewProj;    // screen+depth -> world (jittered, transposed)
    float4 GParams;          // x preExposure, y screenW, z screenH, w cascadeCount (1 or 2)
};

Texture2D<float>  Depth     : register(t0);
Texture2D<float4> Normal    : register(t1);   // primary-surface world normal packed [0,1]
Texture2D<float4> Albedo    : register(t2);   // G-buffer RT0 (albedo.rgb + specF0.a)
Texture2D<float4> IrrAtlas  : register(t3);   // NEAR cascade irradiance
Texture2D<float2> DepthAtlas : register(t4);  // NEAR cascade depth moments
StructuredBuffer<float4> ProbeState : register(t5);   // NEAR cascade per-probe (relocation offset.xyz, active)
Texture2D<float4> IrrAtlasFar  : register(t6);   // CHUNK3 FAR cascade irradiance
Texture2D<float2> DepthAtlasFar : register(t7);  // CHUNK3 FAR cascade depth moments
StructuredBuffer<float4> ProbeStateFar : register(t8);   // CHUNK3 FAR cascade per-probe
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

// Atlas UV for probe (px,py,pz), sampling direction `dir`, tile `texels`. Layout MUST match DdgiBlend.Locate:
// col = pz*ProbesX + px, row = py; tile = texels + 2*BORDER. `dims` = this cascade's grid dims.
float2 ProbeAtlasUv(uint px, uint py, uint pz, float3 dir, uint texels, float2 atlasSize, float3 dims) {
    uint tile = texels + 2u * BORDER;
    uint col = pz * (uint)dims.x + px;
    uint row = py;
    float2 oct = OctEncode(dir);
    float2 interiorPx = oct * float(texels);
    float2 texelXY = float2(col * tile, row * tile) + float2(BORDER, BORDER) + interiorPx;
    return texelXY / atlasSize;
}
uint ProbeIndex(uint px, uint py, uint pz, float3 dims) {
    return (pz * (uint)dims.y + py) * (uint)dims.x + px;
}

// CHUNK3: gather one cascade. `far` selects the FAR atlas set (the two cascades have identical math, only the
// grid + atlas SRVs differ). Returns the trilinear-accumulated irradiance E and the total weight (sumW) — the
// caller uses sumW to decide whether near had coverage (sumW>0) before falling back to far. Out-of-grid → sumW 0.
// bypassVis: skip the Chebyshev leak gate (a COVERAGE FALLBACK for receivers the gate rejects entirely — small/
// thin geometry like the Cornell boxes the sparse probe grid "can't see", which would otherwise render pure
// black). Used only when the normal (gated) sample returned ~no weight, so it can't leak into well-covered areas.
float3 SampleCascade(DdgiGrid g, bool far, float3 worldPos, float3 N, bool bypassVis, out float sumW) {
    sumW = 0.0;
    float3 spacing = float3(g.OriginSpacingX.w, g.SpacingYZ.x, g.SpacingYZ.y);
    float3 biasPos = worldPos + N * g.Params1.y;
    float3 rel = (biasPos - g.OriginSpacingX.xyz) / spacing;
    int3 baseCoord = (int3)floor(rel);
    float3 frac3 = rel - (float3)baseCoord;
    int3 dims = int3((int)g.ProbeDims.x, (int)g.ProbeDims.y, (int)g.ProbeDims.z);
    float2 irrAtlasSize = float2((uint)g.ProbeDims.x * (uint)g.ProbeDims.z, (uint)g.ProbeDims.y) * float((uint)g.Params0.x + 2u * BORDER);
    float2 depAtlasSize = float2((uint)g.ProbeDims.x * (uint)g.ProbeDims.z, (uint)g.ProbeDims.y) * float((uint)g.Params0.y + 2u * BORDER);

    float3 sumIrr = 0.0.xxx;
    [unroll] for (int i = 0; i < 8; i++) {
        int3 off = int3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        int3 c = baseCoord + off;
        if (any(c < 0) || any(c >= dims)) continue;
        uint cx = (uint)c.x, cy = (uint)c.y, cz = (uint)c.z;
        uint idx = ProbeIndex(cx, cy, cz, g.ProbeDims);

        float4 ps = far ? ProbeStateFar[idx] : ProbeState[idx];
        if (ps.w < 0.5) continue;   // inactive (buried) probe

        float3 basePos = g.OriginSpacingX.xyz + float3(cx * g.OriginSpacingX.w, cy * g.SpacingYZ.x, cz * g.SpacingYZ.y);
        float3 probePos = basePos + ps.xyz;
        float3 toProbe = probePos - biasPos;
        float distToProbe = length(toProbe);
        float3 dirToProbe = distToProbe > 1e-5 ? toProbe / distToProbe : N;

        float3 triv = lerp(1.0 - frac3, frac3, (float3)off);
        float trilinear = triv.x * triv.y * triv.z;
        float wrap = saturate(dot(dirToProbe, N) * 0.5 + 0.5); wrap = wrap * wrap + 0.2;

        float3 biasDir = normalize(-dirToProbe);
        float2 duv = ProbeAtlasUv(cx, cy, cz, biasDir, (uint)g.Params0.y, depAtlasSize, g.ProbeDims);
        float2 mom = far ? DepthAtlasFar.SampleLevel(LinearClamp, duv, 0).rg
                         : DepthAtlas.SampleLevel(LinearClamp, duv, 0).rg;
        float vis = 1.0;
        if (!bypassVis && distToProbe > mom.x) {
            float variance = abs(mom.x * mom.x - mom.y);
            float diff = distToProbe - mom.x;
            vis = variance / (variance + diff * diff);
            vis = max(vis * vis * vis, 0.0);
        }

        float weight = trilinear * wrap * vis;
        if (weight < 1e-6) continue;

        float2 iuv = ProbeAtlasUv(cx, cy, cz, N, (uint)g.Params0.x, irrAtlasSize, g.ProbeDims);
        float3 irr = far ? IrrAtlasFar.SampleLevel(LinearClamp, iuv, 0).rgb
                         : IrrAtlas.SampleLevel(LinearClamp, iuv, 0).rgb;
        sumIrr += Sanitize(irr) * weight;
        sumW += weight;
    }
    return sumW > 1e-5 ? sumIrr / sumW : 0.0.xxx;
}
// Default (leak-gated) sample — the common path. Defined AFTER the 6-arg overload so DXC has it in scope (HLSL
// has no forward declarations).
float3 SampleCascade(DdgiGrid g, bool far, float3 worldPos, float3 N, out float sumW) {
    return SampleCascade(g, far, worldPos, N, false, sumW);
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

    // CHUNK3 CASCADE: NEAR first (dense, high detail). Where near has no coverage (receiver outside the near grid
    // or all near probes occluded/inactive → sumWNear ~0) fall back to the FAR cascade (sparse, wide). A soft
    // blend in the near grid's outer shell would remove the hard cascade boundary; for now a coverage test
    // (near if it has weight, else far) is seam-free enough because the near grid's edge probes still contribute
    // until the cell genuinely leaves the grid. cascadeCount (GParams.w) < 1.5 → near only (byte-identical 1-cascade).
    float sumWNear = 0.0;
    float3 E = SampleCascade(GNear, false, worldPos, N, sumWNear);
    float coverage = sumWNear;
    if (GParams.w >= 1.5 && sumWNear <= 1e-4) {
        float sumWFar = 0.0;
        E = SampleCascade(GFar, true, worldPos, N, sumWFar);
        coverage = max(coverage, sumWFar);
    }
    // COVERAGE FALLBACK: a receiver the leak gate rejects entirely (small/thin geometry the sparse probe grid
    // "can't see" — e.g. the Cornell boxes) returns ~0 weight → pure black. Re-sample NEAR with the gate bypassed
    // and blend it in, weighted DOWN (0.5) so any leak it admits is soft, not a bright wall-punch-through. Only
    // kicks in where the gated sample failed (coverage ~0), so well-covered surfaces are byte-identical.
    if (coverage <= 1e-3) {
        float sumWBp = 0.0;
        float3 Ebp = SampleCascade(GNear, false, worldPos, N, true, sumWBp);
        if (sumWBp > 1e-4) E = lerp(E, Ebp, saturate(1.0 - coverage * 1000.0) * 0.5);
    }

    float3 outRgb = Sanitize(albedo * E) * GParams.x;
    Output[px] = float4(outRgb, 1.0);
}
