// DDGI — near-field SSGI complement (A4). DDGI probes are ~2 m apart, so all contact GI / crevice darkening /
// near-surface colour bleed UNDER that scale is structurally missing from the probe gather. This pass fills it
// at full pixel resolution with a screen-space GTAO-style horizon march that CARRIES RADIANCE (kajiya ssgi.hlsl):
// the same ground-truth cosine-weighted horizon integral our Gtao.hlsl already uses for occlusion, but at each
// VISIBLE horizon sample it also gathers that surface's outgoing radiance (the current frame's lit SceneColor —
// direct sun + sky + punctual, before DDGI-far is combined) weighted by the arc the sample subtends. The result
// is a per-pixel near-field one-bounce GI that the Combine then blends against the DDGI far-field by sample
// distance (near = SSGI, far = DDGI) so the two never double-count.
//
// View-independent of any history: it reads ONLY the current frame's SceneColor + G-buffer → NO prev-frame
// radiance, NO reprojection, NO temporal feedback. The whole ghosting/disocclusion class never arises and the
// single-EMA cache-space GI philosophy stays intact — this is a pure spatial estimator.
//
// Bound: b0 constants | t0 depth (R32F) | t1 normal (G1 [0,1]) | t2 SceneColor (lit HDR) | u0 NearField out | s0.

cbuffer DdgiNearFieldConstants : register(b0) {
    float4x4 InvProjection;   // view-space reconstruct (DX z[0,1]), transposed
    float4x4 Projection;      // view→clip, for the sample's screen reprojection (unused path kept for parity)
    float4x4 View;            // world→view (transposed) to rotate the G-buffer world normal into view space
    uint  W, H;  float Radius;  float FrameIndex;       // Radius = near-field world march radius (m); FrameIndex<0 = det
    float SliceCount; float StepCount; float Intensity; float Thickness;   // march quality + GI gain + thin-occluder bias
};

Texture2D<float>  Depth     : register(t0);
Texture2D<float4> NormalTex : register(t1);
Texture2D<float4> SceneColor: register(t2);   // current lit HDR (direct + sky + punctual; pre-DDGI-far)
Texture2D<float4> Albedo    : register(t3);   // G-buffer base color (receiver Lambert BRDF, folded here)
RWTexture2D<float4> NearField : register(u0); // rgb = near-field indirect CONTRIBUTION (E*albedo/π), a = coverage [0,1]
SamplerState LinearClamp : register(s0);

static const float PI = 3.14159265359;
static const float HALF_PI = 1.57079632679;
static const int MAX_SLICES = 6;
static const int MAX_STEPS = 12;

float3 ViewPosFromUv(float2 uv, float depth) {
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 v = mul(ndc, InvProjection);
    return v.xyz / v.w;
}
float3 ViewNormalFromUv(float2 uv) {
    float3 nW = normalize(NormalTex.SampleLevel(LinearClamp, uv, 0).rgb * 2.0 - 1.0);
    return normalize(mul(float4(nW, 0.0), View).xyz);
}
float Ign(float2 px, float frame) {
    float n = frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715))));
    return (frame >= 0.0) ? frac(n + 0.61803398875 * frame) : n;   // det: no temporal advance
}

[numthreads(8, 8, 1)]
void CSMain(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= W || tid.y >= H) return;
    int2 px = int2(tid.xy);
    float2 texel = float2(1.0 / W, 1.0 / H);
    float2 uv = (float2(px) + 0.5) * texel;

    float depth = Depth.Load(int3(px, 0));
    if (depth >= 1.0) { NearField[px] = float4(0, 0, 0, 0); return; }   // sky: no near-field GI

    float3 P = ViewPosFromUv(uv, depth);     // view-space pos (z negative forward, RH)
    float3 N = ViewNormalFromUv(uv);
    float3 V = normalize(-P);

    // World near-field radius → screen pixels (clamped so grazing pixels don't march the whole screen).
    float radiusPx = Radius / max(-P.z, 1e-3) * (0.5 / texel.y);
    radiusPx = clamp(radiusPx, 2.0, 0.25 / texel.y);

    int slices = (int)SliceCount;
    int steps = (int)StepCount;
    float noise = Ign(float2(px), FrameIndex);

    float3 giAccum = 0.0.xxx;
    float coverage = 0.0;
    float weightSum = 0.0;

    [unroll] for (int s = 0; s < MAX_SLICES; s++) {
        if (s >= slices) break;
        float phi = (s + noise) * PI / slices;
        float2 dir = float2(cos(phi), sin(phi));
        float3 sliceDir = float3(dir, 0.0);
        float3 sliceNormal = normalize(cross(sliceDir, V));
        float3 projN = N - sliceNormal * dot(N, sliceNormal);
        float projNLen = length(projN);
        if (projNLen < 1e-4) continue;
        float3 projNDir = projN / projNLen;
        float3 sliceTangent = cross(sliceNormal, V);
        float nAng = atan2(dot(projNDir, sliceTangent), dot(projNDir, V));

        [unroll] for (int side = 0; side < 2; side++) {
            float sgn = side == 0 ? -1.0 : 1.0;
            float cHorizon = -1.0;
            float prevCHorizon = -1.0;
            [unroll] for (int t = 0; t < MAX_STEPS; t++) {
                if (t >= steps) break;
                float fr = (t + 0.5 + noise) / steps;
                float2 sampleUv = uv + sgn * dir * fr * radiusPx * texel;
                if (any(sampleUv < 0.0) || any(sampleUv > 1.0)) continue;
                float sDepth = Depth.SampleLevel(LinearClamp, sampleUv, 0).r;
                if (sDepth >= 1.0) continue;                    // sky sample: no occluder, no radiance
                float3 sv = ViewPosFromUv(sampleUv, sDepth) - P;
                float dist = length(sv);
                if (dist < 1e-4 || dist > Radius) continue;
                float3 svDir = sv / dist;
                float cSample = dot(svDir, V);
                // Thin-occluder bias (same as our GTAO): a thin slab behind the horizon doesn't block it.
                float cBiased = cSample - Thickness * (1.0 - saturate(cSample));
                // RADIANCE GATHER (kajiya): when this sample RAISES the horizon, the newly-revealed arc
                // [prevHorizon, newHorizon] is lit by the sample surface — accumulate its outgoing radiance
                // weighted by that arc's cosine-integral and a smooth distance falloff. A sample that does NOT
                // raise the horizon contributes nothing (it's behind an already-seen occluder).
                if (cBiased > cHorizon) {
                    float hPrev = acos(clamp(prevCHorizon, -1.0, 1.0));
                    float hNew  = acos(clamp(cBiased,      -1.0, 1.0));
                    // Cosine-weighted arc contribution (GTAO inner integral over [hNew, hPrev], relative to nAng).
                    float aPrev = 0.25 * (-cos(2.0 * hPrev - nAng) + cos(nAng) + 2.0 * hPrev * sin(nAng));
                    float aNew  = 0.25 * (-cos(2.0 * hNew  - nAng) + cos(nAng) + 2.0 * hNew  * sin(nAng));
                    float arc = max(aPrev - aNew, 0.0);
                    float falloff = smoothstep(1.0, 0.0, dist / Radius);
                    float3 sampleRadiance = SceneColor.SampleLevel(LinearClamp, sampleUv, 0).rgb;
                    // Facing test: the sample surface must face back toward us to bounce light here.
                    float3 sN = ViewNormalFromUv(sampleUv);
                    float facing = saturate(dot(-svDir, sN));
                    giAccum += sampleRadiance * (arc * falloff * facing * projNLen);
                    coverage += arc * falloff * projNLen;
                    prevCHorizon = cBiased;
                    cHorizon = cBiased;
                }
            }
        }
        weightSum += projNLen;
    }

    float3 E = (weightSum > 1e-4) ? giAccum / weightSum : 0.0.xxx;   // near-field incident irradiance
    float cov = (weightSum > 1e-4) ? saturate(coverage / weightSum) : 0.0;
    // Receiver Lambert BRDF (albedo/π), folded here so the combine never binds the G-buffer — exactly like
    // DdgiSample. The result is a finished indirect CONTRIBUTION directly addable to the HDR color.
    float3 albedo = Albedo.SampleLevel(LinearClamp, uv, 0).rgb;
    float3 contrib = E * albedo * (1.0 / PI) * Intensity;
    // Sanitize (component select, never lerp-with-flag — CLAUDE.md NaN rule) + fp16 clamp.
    contrib = (any(isnan(contrib)) || any(isinf(contrib))) ? 0.0.xxx : min(contrib, 60000.0.xxx);
    NearField[px] = float4(contrib, cov);
}
