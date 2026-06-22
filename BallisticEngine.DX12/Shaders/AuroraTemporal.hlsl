// Aurora — MOTION-VECTOR temporal resolve of the indirect irradiance E (the proper fix for motion boiling).
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
    // The absolute 1e-3 floor was too LOOSE in the dark: when E itself is ~1e-3, the band (nmax+ext) is several×
    // the signal, so the clamp never bites and the history's per-frame wobble leaks through as flicker (the user's
    // "düşük ışıkta daha belirgin"). Make the floor RELATIVE to the local mean brightness so it scales down with the
    // signal — the band stays a fixed FRACTION of the local radiance, binding the history just as tightly in shadow
    // as in light, while the (nmax-nmin)*1.5 spread term still keeps it loose enough not to re-inject Monte-Carlo grain.
    float3 nmean = (nmin + nmax) * 0.5;
    float3 ext = (nmax - nmin) * 1.5 + nmean * 0.25 + 1e-4;
    float3 clampedHist = clamp(hist.rgb, nmin - ext, nmax + ext);

    // EMA: trusted → slow accumulation (low alpha); untrusted (disocclusion) → fresh E. lerp toward fresh, never black.
    float alpha = lerp(1.0, saturate(Alpha), trust);
    float3 resolved = lerp(lerp(E, clampedHist, trust), E, alpha);

    // TEMPORAL RATE-LIMIT — the real fix for the "saniyede bir parlama". The GI card-radiance cache relights only a
    // ROUND-ROBIN SLICE of records per frame (391k records ≫ 50k budget), so every ~8 frames a whole cluster of cards
    // STEPS its radiance (EmaAlpha) at once. That step is spatially COHERENT (the entire card jumps together), so the
    // neighbourhood AABB clamp above can't catch it — nmin/nmax jump with it — and the EMA alone lets ~alpha of the
    // step through each frame, a visible per-cluster flash. Bound how far the resolved value may move from the trusted
    // history in ONE frame to a small fraction of the local brightness: a coherent cache step is then SMEARED over
    // many frames (invisible ramp) instead of flashing, while genuine lighting changes (sun moves) just take a few
    // extra frames to settle. Only applied when the history is trusted (a real disocclusion already took fresh E).
    // The limit is applied UNCONDITIONALLY (NOT ×trust). Earlier it was eased out by `trust`, but on a STATIC scene
    // there is no real disocclusion — the only thing pulling trust below 1 is reprojection/edge noise (e.g. the high-
    // contrast rim around a street lamp, exactly the spot the user flagged). Easing the rate-limit there let the big
    // per-cluster step flash through precisely at the visible edges. A genuine disocclusion already took fresh E in
    // the `lerp(E, clampedHist, trust)` above (trust→0 ⇒ resolved≈E), and clampedHist≈E there too, so the limit is a
    // no-op on real disocclusion anyway — making it unconditional only bites the static-scene cache step we want gone.
    // Reference = the trust-blended history (NOT raw clampedHist): on a real disocclusion trust→0 so lhRef→E and the
    // limit is a no-op (fresh E passes, no trail); on a static surface trust→1 so lhRef→clampedHist and the limit
    // bites the cache step. This keeps the rate-limit unconditional WITHOUT trapping disoccluded pixels on stale
    // history. (clampedHist≈E on disocclusion too, since the AABB is built from this frame's E, so lhRef→E either way.)
    float3 lhRef = lerp(E, clampedHist, trust);
    float lref = max(dot(lhRef, float3(0.2126, 0.7152, 0.0722)), 1e-4);
    float maxStep = lref * 0.04 + 1e-4;                          // ≤4% of local brightness per frame (was 6%)
    float3 delta = resolved - lhRef;
    float dlen = max(max(abs(delta.r), abs(delta.g)), abs(delta.b));
    float limited = (dlen > maxStep) ? maxStep / dlen : 1.0;
    resolved = lhRef + delta * limited;

    OutE[px] = float4(San(resolved), depth);
}
