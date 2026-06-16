// SCREEN-SPACE RADIANCE PROBE — PLACEMENT pass (compute, SM6.6). GI plan Phase 4 (P4.0).
//
// One thread per SCREEN PROBE. The screen is tiled into DOWNSAMPLE x DOWNSAMPLE blocks (default 16x16); each
// tile gets ONE probe, snapped to the G-buffer surface of a REPRESENTATIVE pixel inside the tile (jittered
// per frame so coverage rotates over the tile). We read that pixel's depth + world-normal, reconstruct its
// world position, and store {worldPos, valid} + {worldNormal} for the trace pass. A tile whose representative
// pixel is sky/un-shaded is marked INVALID (the trace + integrate skip it).
//
// This is the published Lumen "screen probe" placement, minimal real version: uniform 1-probe-per-tile with a
// per-frame jitter. Adaptive edge infill (extra probes at depth discontinuities) is a later refinement (P4.1+).
//
// Bound: CBV b0 ScreenProbeConstants; SRV t0 depth, t1 world-normal (G-buffer RT1, [0,1]-packed); UAV u0
// ProbePos[probe] = (worldPos.xyz, valid), u1 ProbeNormal[probe] = (worldN.xyz, 0); static linear-clamp s0.

cbuffer ScreenProbeConstants : register(b0) {
    float4x4 InvViewProj;   // screen+depth -> world (jittered, transposed)
    float4 SpParams0;       // x=probesX y=probesY z=downsample w=frameIndex
    float4 SpParams1;       // x=screenW y=screenH z=maxRayDist w=preExposure
    float4 SpParams2;       // x=irrTexels(octahedron side) y=normalBias z=intensity w=(unused)
};

Texture2D<float>  Depth  : register(t0);
Texture2D<float4> Normal : register(t1);
RWStructuredBuffer<float4> ProbePos    : register(u0);   // (worldPos.xyz, valid)
RWStructuredBuffer<float4> ProbeNormal : register(u1);   // (worldNormal.xyz, 0)
SamplerState LinearClamp : register(s0);

// Cheap per-tile hash → a jittered representative pixel offset within the tile (rotates coverage over frames so
// the temporally-accumulated field samples the whole tile, not one fixed corner).
float Hash1(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}

[numthreads(8, 8, 1)]
void CSPlace(uint3 dtid : SV_DispatchThreadID) {
    uint pbx = dtid.x, pby = dtid.y;
    uint probesX = (uint)SpParams0.x, probesY = (uint)SpParams0.y;
    if (pbx >= probesX || pby >= probesY) return;
    uint probe = pby * probesX + pbx;

    uint ds = (uint)SpParams0.z;
    float w = SpParams1.x, h = SpParams1.y;
    uint frame = (uint)SpParams0.w;

    // Representative pixel = jittered point inside the tile, clamped to screen. Jitter is per-(probe,frame).
    float jx = Hash1(probe * 9277u + frame * 2654435761u);
    float jy = Hash1(probe * 9311u + frame * 40503u + 7u);
    uint repX = min((uint)(pbx * ds + jx * float(ds)), (uint)w - 1u);
    uint repY = min((uint)(pby * ds + jy * float(ds)), (uint)h - 1u);
    float2 uv = (float2(repX, repY) + 0.5) / float2(w, h);

    ProbePos[probe]    = float4(0, 0, 0, 0);   // default INVALID (sky / off-surface)
    ProbeNormal[probe] = float4(0, 1, 0, 0);

    float depth = Depth.SampleLevel(LinearClamp, uv, 0).r;
    if (depth >= 1.0) return;
    float3 worldN = Normal.SampleLevel(LinearClamp, uv, 0).rgb * 2.0 - 1.0;
    if (dot(worldN, worldN) < 0.1) return;
    float3 N = normalize(worldN);

    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 wp = mul(ndc, InvViewProj);
    float3 worldPos = wp.xyz / wp.w;

    ProbePos[probe]    = float4(worldPos, 1.0);
    ProbeNormal[probe] = float4(N, 0.0);
}
