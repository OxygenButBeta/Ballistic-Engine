// Lumen FAZ 8 — LUMEN REFLECTIONS (compute).
//
// When Lumen GI owns the frame, the specular reflection ray is resolved by the SAME LumenTrace abstraction the
// screen-probe diffuse gather uses (HW TLAS RayQuery OR software global-SDF sphere-march → sample the LIT surface
// cache FinalLighting). LumenTrace(origin, dir, maxDist, preferSW).Radiance IS the reflection color: the surface
// cache is pre-lit + multi-bounce, so the reflection carries the cache's GI color bleed (a mirror floor reflecting
// the lit Cornell walls in red/green/white) with NO re-shading needed — mirroring how Aurora's RT reflections
// sample its card cache.
//
// Per HALF-RES pixel (the SAME contract as Ssr.hlsl / DxrReflections.hlsl): map to the full-res G-buffer, read
// depth (skip sky), world normal, material (metallic/roughness). Skip non-reflective. Reflect the view dir about the
// normal — VNDF importance-sample for glossy (copied from DxrReflections: 1-4 SPP + Fresnel + roughFade). For each
// sample ray: LumenTrace(P + N*bias, reflectDir, maxDist, preferSW).Radiance → accumulate → average → write
// float4(reflColor, strength) into the half-res reflection UAV (.a = Fresnel/strength for the SSR upsample-lerp).
// The existing SSR combine (depth-aware upsample + Fresnel-lerp) then mixes it into the scene color, exactly as it
// does for the SSR march / DXR-reflection target.
//
// TEST OVERRIDES (CornellBox materials are MATTE — no reflections by default): RoughnessOverride >= 0 forces the
// roughness, MetallicOverride >= 0 forces the metallic, so BALLISTIC_FX_ROUGHNESS / BALLISTIC_FX_METALLIC make the
// floor a mirror to VERIFY the reflection carries the cache GI color. -1 = no override (production path untouched).
//
// Bound (HeapDirectlyIndexed root sig, mirrors LumenScreenProbe): TLAS t0, Cards t1 / Pages t2 / InstanceRanges t3
// (root SRVs); G-buffer depth t4 / normal t5 / material t6 SRVs + reflection-target u0 UAV (per-frame table); the
// clipmap Texture3D + FinalLighting Texture2D (+ optional sky cube) resolve from ResourceDescriptorHeap[] via the
// CB bindless indices. LinearClamp s0 / LinearWrap s1.
//
// Driver rules obeyed: NaN scrub = ternary component-select (NEVER lerp(v,0,flag) — NaN*0==NaN, the proven AMD
// bug); every divide guards its denom; saturate before pow/sqrt; the reflection store is per-channel capped below
// the fp16 ceiling before the Sanitize so a finite +Inf can't reach the half-res RGBA16F target.

RaytracingAccelerationStructure Scene : register(t0);

// LumenTrace's GPU structs — declared FIRST (guarded with LT_STRUCTS_DEFINED so the include skips its own
// re-declaration). IDENTICAL layout to LumenTrace.hlsl / Dx12LumenCardScene.
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

cbuffer ReflConstants : register(b0) {
    // --- the LumenTrace parameter block (MUST be first; the include reads these by name) ---
    float3 LtClipOrigin;   float LtVoxelSize;
    float3 LtCamPosUnused; float LtClipHalfExtent;
    uint   LtClipResX, LtClipResY, LtClipResZ; float LtMaxTraceDist;
    uint   LtAtlasSize, LtCardCount, LtInstanceCount, LtFinalReadIdx;
    uint   LtClipmapIdx, LtFinalValid, LtHasTlas, LtSkyIdx;
    float  LtSkyIntensity, LtUseSky, LtSurfBias, LtPad0;
    // --- reflection params (after the trace block) ---
    float4x4 InvViewProj;     // screen+depth → world (transposed)
    float3 CameraPos;         float Intensity;
    float2 HalfTexel;         float FrameIndex;       float PreferSW;     // 1/halfRes; VNDF temporal jitter (<0 det fixed); SW backend
    uint   FullW;             uint  FullH;            uint HalfW;         uint HalfH;
    float  MaxRayDist;        float NormalBias;       float RoughnessOverride; float MetallicOverride; // override<0 = none
    uint   DebugRaw;          float ReflPad0;         float ReflPad1;     float ReflPad2;             // DebugRaw: write raw refl (no strength fade) for the debug view
};

StructuredBuffer<LtCard>          Cards          : register(t1);
StructuredBuffer<LtPage>          Pages          : register(t2);
StructuredBuffer<LtInstanceRange> InstanceRanges : register(t3);
Texture2D<float>  Depth     : register(t4);   // G-buffer depth (full-res)
Texture2D<float4> Normal    : register(t5);   // G-buffer world normal, packed N*0.5+0.5 (full-res)
Texture2D<float4> Material  : register(t6);   // G-buffer r metallic, g roughness (full-res)
RWTexture2D<float4> Output  : register(u0);   // half-res reflection target (rgb refl, a strength)

SamplerState LinearClamp : register(s0);
SamplerState LinearWrap  : register(s1);

#include "Lumen/LumenTrace.hlsl"

// LtSanitize (ternary select), LumenTrace(...) come from the include.

static const float MAX_ROUGHNESS = 1.0;   // matches DxrReflections — rough surfaces also get a (tapered) reflection.

// ---- VNDF (Heitz 2018) GGX visible-normal importance sampling (copied from DxrReflections) ----
float2 R2(uint i) { return frac(float2(0.7548776662466927, 0.5698402909980532) * (float)i + 0.5); }
float Hash1(uint s) { s = (s ^ 61u) ^ (s >> 16); s *= 9u; s ^= s >> 4; s *= 0x27d4eb2du; s ^= s >> 15; return float(s & 0x7fffffffu) / float(0x7fffffff); }

void OnbFrame(float3 n, out float3 t, out float3 b) {
    float s = n.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (s + n.z);
    float bb = n.x * n.y * a;
    t = float3(1.0 + s * n.x * n.x * a, s * bb, -s * n.x);
    b = float3(bb, s + n.y * n.y * a, -n.y);
}

float3 SampleVndfH(float3 N, float3 V, float roughness, float2 urand) {
    float alpha = max(roughness * roughness, 1e-3);
    float3 T, B;
    OnbFrame(N, T, B);
    float3 wo = normalize(float3(dot(V, T), dot(V, B), dot(V, N)));
    float3 Vh = normalize(float3(alpha * wo.x, alpha * wo.y, wo.z));
    float3 t1 = (Vh.z < 0.9999) ? normalize(cross(float3(0, 0, 1), Vh)) : float3(1, 0, 0);
    float3 t2 = cross(Vh, t1);
    float r = sqrt(urand.x);
    float phi = 6.28318530718 * urand.y;
    float p1 = r * cos(phi);
    float p2 = r * sin(phi);
    float s = 0.5 * (1.0 + Vh.z);
    p2 = (1.0 - s) * sqrt(max(0.0, 1.0 - p1 * p1)) + s * p2;
    float3 Nh = p1 * t1 + p2 * t2 + sqrt(max(0.0, 1.0 - p1 * p1 - p2 * p2)) * Vh;
    float3 hTan = normalize(float3(alpha * Nh.x, alpha * Nh.y, max(0.0, Nh.z)));
    return normalize(hTan.x * T + hTan.y * B + hTan.z * N);
}

float3 WorldFromUvDepth(float2 uv, float depth) {
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 w = mul(ndc, InvViewProj);
    return w.xyz / w.w;
}

// One thread per HALF-RES reflection pixel. Reads the full-res G-buffer at the pixel center, traces the reflection
// ray(s) via LumenTrace, writes the half-res reflection target.
[numthreads(8, 8, 1)]
void CSReflect(uint3 dtid : SV_DispatchThreadID) {
    uint2 hpx = dtid.xy;
    if (hpx.x >= HalfW || hpx.y >= HalfH) return;
    float2 uv = (float2(hpx) + 0.5) * HalfTexel;   // half-res uv == full-res uv (same [0,1] domain)
    Output[hpx] = float4(0, 0, 0, 0);

    float depth = Depth.SampleLevel(LinearClamp, uv, 0).r;
    if (depth >= 1.0) return;                                   // sky: nothing reflects here
    float4 mat = Material.SampleLevel(LinearClamp, uv, 0);
    float metallic  = MetallicOverride  >= 0.0 ? MetallicOverride  : mat.r;
    float roughness = RoughnessOverride >= 0.0 ? RoughnessOverride : mat.g;
    float3 worldN = Normal.SampleLevel(LinearClamp, uv, 0).rgb * 2.0 - 1.0;
    if (dot(worldN, worldN) < 0.1 || roughness > MAX_ROUGHNESS) return;

    float3 worldPos = WorldFromUvDepth(uv, depth);
    float3 N = normalize(worldN);
    float3 V = normalize(CameraPos - worldPos);
    float NdotV = max(dot(N, V), 0.0);

    // Fresnel strength (matches Ssr.hlsl / DxrReflections so the shared SSR combine lerps consistently).
    float F0 = metallic >= 0.5 ? 0.6 : 0.04;
    float fres = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);
    float grazeKeep = 1.0 - smoothstep(0.05, 0.45, roughness);
    fres = F0 + (fres - F0) * grazeKeep;
    float roughFade = 1.0 - smoothstep(0.5, 1.0, roughness) * 0.85;
    float strength = saturate(fres * Intensity) * roughFade;
    if (DebugRaw == 0u && strength <= 0.001) return;

    bool preferSW = PreferSW > 0.5;
    float maxDist = MaxRayDist > 0.0 ? MaxRayDist : (LtMaxTraceDist > 0.0 ? LtMaxTraceDist : 1e4);
    float bias = max(LtSurfBias, NormalBias);

    // VNDF-sampled reflection ray(s): near-mirror collapses to reflect(-V,N); rough draws from the GGX visible-normal
    // lobe. Multi-sample the rough band (1 ray near-mirror, up to SPP fully rough), each decorrelated, averaged.
    float2 baseRand = float2(Hash1(hpx.x * 1973u + hpx.y * 9277u + 1u),
                             Hash1(hpx.x * 26699u + hpx.y * 8537u + 7u));
    float2 frameRand = (FrameIndex < 0.0) ? 0.0.xx : R2((uint)FrameIndex);
    const uint SPP_MAX = 4u;
    uint spp = (uint)clamp(1.0 + smoothstep(0.15, 0.8, roughness) * (float)(SPP_MAX - 1u) + 0.5, 1.0, (float)SPP_MAX);

    float3 colSum = 0.0.xxx; float wSum = 0.0;
    [loop] for (uint si = 0u; si < SPP_MAX; ++si) {
        if (si >= spp) break;
        float2 urand = frac(baseRand + frameRand + R2(si * 977u + 13u));
        float3 H = SampleVndfH(N, V, roughness, urand);
        float3 R = reflect(-V, H);
        if (dot(R, N) <= 0.0) R = reflect(-V, N);   // grazing VNDF tail → mirror about N

        float3 origin = worldPos + N * bias;
        // LumenTrace returns the surface-cache FinalLighting at the hit (or sky on miss) — this IS the reflection
        // color (pre-lit + multi-bounce), so it carries the same GI color bleed the diffuse view sees.
        LumenTraceResult tr = LumenTrace(origin, R, maxDist, preferSW);
        colSum += LtSanitize(tr.Radiance); wSum += 1.0;
    }
    float3 col = (wSum > 0.0) ? colSum / wSum : 0.0.xxx;

    // Per-channel cap below the fp16 ceiling (~65504) BEFORE the ternary Sanitize so a finite +Inf can't reach the
    // half-res RGBA16F target (the SSR combine has a read-side SanitizeSsr too, but cap at the source).
    col = LtSanitize(min(col, 60000.0.xxx));
    // DebugRaw: write the raw reflection at full strength (the debug view blits this straight to scene color so the
    // reflection is visible even on a matte surface where strength would be ~0).
    Output[hpx] = float4(col, DebugRaw != 0u ? 1.0 : strength);
}
