// Lumen FAZ 5 — TRACE DEBUG view. Per camera pixel: reconstruct the world position + normal from the G-buffer, build a
// TBN basis, trace N cosine-weighted hemisphere rays through the shared LumenTrace abstraction (LumenTrace.hlsl), and
// write the MEAN gathered radiance E (the indirect irradiance) into the HDR scene color. This is the PROOF that the
// trace works end-to-end (SDF/TLAS hit → surface-cache FinalLighting sample) and the preview of FAZ 6 (screen probes).
//
// Fullscreen pixel shader (replace into SceneColor), mirroring the other Lumen debug views (GlobalSdfDebug etc.) — a
// PS can issue inline RayQuery just like a compute shader, so no UAV/transient management is needed. The G-buffer depth
// + normal are bound as a 2-SRV table (t4/t5); the trace's TLAS/cards/pages/ranges are root SRVs (t0-t3); the clipmap
// + FinalLighting + sky resolve from the reserved bindless tail via ResourceDescriptorHeap[] (indices in the CB).
//
// NaN-safe throughout (the include's LtSanitize + saturate); the gather mean guards its divide.

RaytracingAccelerationStructure Scene : register(t0);

// Struct types the StructuredBuffer binds below need — declared FIRST (the include re-uses these via LT_STRUCTS_DEFINED).
#define LT_STRUCTS_DEFINED
struct LtCard {
    float3 Origin; uint  PageId;
    float3 AxisX;  float ExtentX;
    float3 AxisY;  float ExtentY;
    float3 AxisZ;  float ExtentZ;
};
struct LtPage {
    uint AtlasOffsetX, AtlasOffsetY;
    uint SizeX, SizeY;
    uint CardId, ResLevel, Pad0, Pad1;
};
struct LtInstanceRange { uint Offset; uint Count; };

cbuffer LumenTraceDebugConstants : register(b0) {
    // --- the LumenTrace parameter block (MUST be first; the include reads these by name) ---
    float3 LtClipOrigin;   float LtVoxelSize;
    float3 LtCamPosUnused; float LtClipHalfExtent;
    uint   LtClipResX, LtClipResY, LtClipResZ; float LtMaxTraceDist;
    uint   LtAtlasSize, LtCardCount, LtInstanceCount, LtFinalReadIdx;
    uint   LtClipmapIdx, LtFinalValid, LtHasTlas, LtSkyIdx;
    float  LtSkyIntensity, LtUseSky, LtSurfBias, LtPad0;
    // FAZ 11 — spatial card grid (matches LUMEN_TRACE_PARAMS tail; LtCgEnabled=0 = linear scan)
    float3 LtCgOrigin;     float LtCgEnabled;
    float3 LtCgCellSize;   uint  LtCgDim;
    uint   LtCgCellIdx, LtCgIndexIdx, LtCgPad0, LtCgPad1;
    // --- debug-view-only fields (after the trace block) ---
    float4x4 InvViewProj;    // clip → world (transposed on upload)
    float3 CamPos;           uint   RayCount;       // hemisphere rays per pixel
    uint   PreferSW;         uint   FrameIndex;     // 1 = software SDF backend, 0 = HW TLAS
    uint   DebugMode;        float  Intensity;      // 0 = E (irradiance), 1 = hitT heat, 2 = hit/miss
    float2 DbgPad;
};

StructuredBuffer<LtCard>          Cards          : register(t1);
StructuredBuffer<LtPage>          Pages          : register(t2);
StructuredBuffer<LtInstanceRange> InstanceRanges : register(t3);
Texture2D<float>  GbDepth  : register(t4);
Texture2D<float4> GbNormal : register(t5);   // RT1, packed N*0.5+0.5
SamplerState LinearClamp : register(s0);

#include "Lumen/LumenTrace.hlsl"

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSDebug(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float DbgHash(uint s) {
    s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15;
    return float(s & 0x7fffffffu) / float(0x7fffffff);
}
float3x3 DbgBasis(float3 n) {
    float3 up = abs(n.z) < 0.999 ? float3(0, 0, 1) : float3(1, 0, 0);
    float3 t = normalize(cross(up, n)); float3 b = cross(n, t);
    return float3x3(t, b, n);
}
float3 DbgCosineHemisphere(uint i, uint cnt, float jitter) {
    float u1 = (float(i) + jitter) / float(cnt);
    float u2 = frac(jitter * 1.61803398875 + float(i) * 0.7548776662);
    float r = sqrt(saturate(u1)); float phi = 6.28318530718 * u2;
    return float3(r * cos(phi), r * sin(phi), sqrt(saturate(1.0 - u1)));
}

float4 PSDebug(VSOut i) : SV_Target {
    int2 pix = int2(i.Position.xy);
    float depth = GbDepth.Load(int3(pix, 0));
    // Sky / background (depth at the far plane) — leave the existing scene colour (here: a dark slate so the gather
    // region reads clearly). depth==1.0 is the cleared far value.
    if (depth >= 0.99999)
        return float4(0.01, 0.01, 0.015, 1.0);

    // Reconstruct world pos (DX NDC y-flip) + world normal (unpack RT1).
    float2 ndc = i.Uv * 2.0 - 1.0; ndc.y = -ndc.y;
    float4 wh = mul(float4(ndc, depth, 1.0), InvViewProj);
    float3 worldPos = wh.xyz / max(wh.w, 1e-6);
    float3 N = normalize(GbNormal.Load(int3(pix, 0)).rgb * 2.0 - 1.0);
    if (dot(N, N) < 1e-6) N = float3(0, 1, 0);

    bool preferSW = PreferSW != 0u;
    float maxDist = LtMaxTraceDist > 0.0 ? LtMaxTraceDist : 1e4;
    float surfBias = max(LtSurfBias, 0.01);
    float3 P = worldPos + N * surfBias;   // lift off the surface so the trace's own TMin doesn't self-hit.

    uint rays = clamp(RayCount, 1u, 64u);
    float jit = DbgHash((uint)pix.x * 2654435761u ^ (uint)pix.y * 40503u ^ FrameIndex);
    float3x3 basis = DbgBasis(N);

    float3 acc = 0.0.xxx;
    uint hits = 0u; float hitTSum = 0.0;
    [loop] for (uint k = 0; k < rays; k++) {
        float3 local = DbgCosineHemisphere(k, rays, jit);
        float3 dir = normalize(mul(local, basis));
        LumenTraceResult tr = LumenTrace(P, dir, maxDist, preferSW);
        acc += tr.Radiance;
        if (tr.Hit) { hits++; hitTSum += tr.HitT; }
    }
    float3 E = acc / float(rays);
    E = LtSanitize(max(E, 0.0.xxx)) * max(Intensity, 0.0);

    if (DebugMode == 1u) {
        // hitT heat: average hit distance normalised by maxDist (green=near, red=far, black=all-miss).
        float avgT = hits > 0u ? (hitTSum / float(hits)) / max(maxDist, 1e-3) : 0.0;
        float frac = hits > 0u ? float(hits) / float(rays) : 0.0;
        return float4(saturate(avgT) * frac, saturate(1.0 - avgT) * frac, 0.0, 1.0);
    }
    if (DebugMode == 2u) {
        float frac = float(hits) / float(rays);   // hit fraction (white = all rays hit geometry)
        return float4(frac, frac, frac, 1.0);
    }
    return float4(E, 1.0);   // E = gathered indirect irradiance (the GI preview)
}
