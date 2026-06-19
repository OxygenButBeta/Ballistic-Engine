// Lumen — MOTION-VECTOR temporal resolve of the indirect irradiance E (the proper fix for motion boiling).
//
// The per-pixel trace and the screen-probe gather both write a NOISY few-ray E into `indirect`. A static capture
// hides the noise (it accumulates over identical frames), but under CAMERA/OBJECT MOTION the noise BOILS — the
// previous frame's E no longer lines up with this frame's pixel. The earlier in-shader temporal reprojected with
// worldPos*PrevViewProj (camera-only, and the screen-probe path had NO temporal at all) — a half-measure.
//
// This pass does it RIGHT, using the engine's REAL G-buffer motion vector (RT4 = prevUV - currUV, UNJITTERED,
// the same buffer TAA + FSR consume — it captures BOTH camera and object motion). Per pixel:
//   1) reproject: read the history at prevUV = uv + motion (where this surface WAS last frame),
//   2) disocclusion reject: if prevUV left the screen OR the reprojected depth disagrees → take fresh E (no trail),
//   3) neighbourhood AABB clamp (TAA-style): clamp the history to this frame's local 3x3 E min/max so a stale
//      value can't ghost/boil — bounds the history to plausible current radiance,
//   4) EMA blend the fresh E over the clamped, reprojected history (low alpha = strong accumulation = low noise).
// Output overwrites `indirect` (rgb=E, a=depth for next frame's reject) and is snapshotted into the history.

Texture2D<float4> InE      : register(t0);   // this frame's fresh E (rgb) — from the trace/gather
Texture2D<float4> History  : register(t1);   // previous frame's RESOLVED E (rgb) + depth (a)
Texture2D<float>  Depth    : register(t2);   // current depth (full-res)
Texture2D<float2> Motion   : register(t3);   // RT4 screen motion: prevUV - currUV (UNJITTERED)
RWTexture2D<float4> OutE    : register(u0);   // resolved E (rgb) + depth (a)

cbuffer TemporalConstants : register(b0) {
    float2 Texel;       // 1 / indirect-res
    float  HistoryValid; // 0 first frame / after resize → take fresh
    float  Alpha;        // this-frame EMA weight (low = strong accumulation)
    uint   W, H;        float Pad0, Pad1;
};
SamplerState LinearClamp : register(s0);

float3 San(float3 v) {
    return float3(isnan(v.x)||isinf(v.x)?0:v.x, isnan(v.y)||isinf(v.y)?0:v.y, isnan(v.z)||isinf(v.z)?0:v.z);
}

[numthreads(8, 8, 1)]
void CSTemporal(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    if (px.x >= W || px.y >= H) return;
    float2 uv = (float2(px) + 0.5) * Texel;

    float3 E = San(InE[px].rgb);
    float depth = Depth.SampleLevel(LinearClamp, uv, 0).r;
    if (depth >= 1.0 || HistoryValid < 0.5) { OutE[px] = float4(E, depth); return; }   // sky / first frame → fresh

    // 3x3 neighbourhood of the fresh E for the AABB clamp (kills boil/ghost: history is bounded to plausible local
    // radiance). Computed on the fresh signal so the clamp tracks the true current lighting.
    float3 nmin = E, nmax = E;
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++) {
        int2 q = clamp(int2(px) + int2(dx, dy), int2(0,0), int2(W-1, H-1));
        float3 s = San(InE[q].rgb);
        nmin = min(nmin, s); nmax = max(nmax, s);
    }

    // Reproject with the REAL motion vector: prevUV = uv + motion. (Motion = prevUV - currUV.)
    // DEBUG door BALLISTIC_DX12_LUMEN_TEMPORAL_NOMOTION=1 (Pad0>0.5) ignores motion (prevUv=uv) to isolate whether
    // the motion vector itself is destabilising the reproject (e.g. jitter leaking into a static-camera motion).
    float2 motion = (Pad0 > 0.5) ? float2(0,0) : Motion.SampleLevel(LinearClamp, uv, 0).rg;
    float2 prevUv = uv + motion;
    float4 hist = History.SampleLevel(LinearClamp, prevUv, 0);   // rgb = prev resolved E, a = prev depth

    bool onScreen = all(prevUv >= 0.0) && all(prevUv <= 1.0);
    // Disocclusion: the reprojected texel must be the SAME surface — its stored depth must agree with this pixel's
    // (relative tolerance so far surfaces aren't falsely rejected). A different surface → reject (fresh, no leak).
    float depthAgree = saturate(1.0 - abs(hist.a - depth) / max(depth * 0.08, 1e-4));
    float trust = (onScreen ? 1.0 : 0.0) * depthAgree;

    // AABB clamp — DELIBERATELY LOOSE. A tight 3x3 clamp on a FEW-RAY noisy signal is fatal: the neighbourhood is
    // itself noisy, so clamping the history into it just re-injects this frame's noise → temporal can't accumulate
    // (measured: tight clamp left boiling ~0.8 even at alpha 0.01). Expand the AABB by a margin tied to the local
    // spread so the converged (low-variance) history is NOT pulled back into the noisy per-frame range — the clamp
    // then only catches a genuine lighting change (history far outside the widened band), not Monte-Carlo grain.
    float3 ext = (nmax - nmin) * 1.5 + 1e-3;
    float3 clampedHist = clamp(hist.rgb, nmin - ext, nmax + ext);

    // EMA: trusted → slow accumulation (low alpha); untrusted (disocclusion) → fresh E. lerp toward fresh, never black.
    float alpha = lerp(1.0, saturate(Alpha), trust);
    float3 resolved = lerp(lerp(E, clampedHist, trust), E, alpha);
    OutE[px] = float4(San(resolved), depth);
}
