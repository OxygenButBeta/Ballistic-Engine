// DDGI — spatial denoise (A3, between Sample and Combine). A variance/validity-driven adaptive à-trous-style
// single pass ported from kajiya's rtdgi/spatial_filter.hlsl. SPATIAL ONLY — no history, no temporal feedback —
// so it does NOT touch the single-EMA cache-space GI philosophy (the whole ghosting/disocclusion class the
// engine deliberately avoids never arises here). It cleans the probe-transition banding / disocclusion-seam
// noise that survives the screen-space gather: well-bracketed pixels (validity≈1) early-out untouched; seam /
// sub-cell / corner pixels (low validity, written by DdgiSample.a) get a wider golden-spiral blur, edge-stopped
// by depth-plane + geometric-normal + SSAO similarity, accumulated in a reversible "crunch" tonemap so a stray
// firefly can't dominate the average.
//
// Bound: b0 constants | t0 Indirect (rgb = E*albedo/π, a = validity) | t1 depth (R32F) | t2 normal (G1 [0,1]) |
//        t3 SSAO (R, 1 = unoccluded) | u0 Filtered output (RWTexture2D) | s0 clamp.

cbuffer DdgiDenoiseConstants : register(b0) {
    uint  W, H;  float UseSsao;  float FrameIndex;   // FrameIndex rotates the spiral; <0 = deterministic (fixed)
    float Strength;  float Pad0, Pad1, Pad2;          // Strength scales the max radius (0 = passthrough)
};

Texture2D<float4> Indirect : register(t0);
Texture2D<float>  Depth    : register(t1);
Texture2D<float4> NormalTex: register(t2);
Texture2D<float>  Ssao     : register(t3);
RWTexture2D<float4> Filtered : register(u0);
SamplerState LinearClamp : register(s0);

static const float PI = 3.14159265359;
static const float TAU = 6.28318530718;
static const float GOLDEN_ANGLE = 2.39996322973;

float Square(float x) { return x * x; }
float Max3(float3 v) { return max(v.x, max(v.y, v.z)); }

// Reversible firefly-suppressing tonemap (gpuopen). Average in crunch space so one bright sample can't dominate;
// uncrunch after. Bias toward dimmer input is fine — this output is not fed back, only displayed.
float3 Crunch(float3 v)   { return v * rcp(Max3(v) + 1.0); }
float3 Uncrunch(float3 v) { return v * rcp(max(1.0 - Max3(v), 1e-4)); }

// Interleaved gradient noise (Jimenez) — cheap per-pixel angular offset to break up the spiral's structure.
float Ign(float2 px) {
    return frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715))));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 tid : SV_DispatchThreadID) {
    if (tid.x >= W || tid.y >= H) return;
    int2 px = int2(tid.xy);

    float4 center = Indirect.Load(int3(px, 0));
    float centerValidity = center.a;
    float centerDepth = Depth.Load(int3(px, 0));

    // Sky / fully-confident pixels: passthrough (preserve detail, skip the blur entirely).
    if (centerDepth >= 1.0 || centerValidity >= 0.999 || Strength <= 0.0) {
        Filtered[px] = center;
        return;
    }

    float3 centerN = normalize(NormalTex.Load(int3(px, 0)).xyz * 2.0 - 1.0);
    float centerSsao = (UseSsao > 0.5) ? Ssao.Load(int3(px, 0)).r : 1.0;

    // Adaptive kernel: radius and sample count grow as validity drops (kajiya). A seam pixel (validity~0) gets a
    // ~16px radius / 8 taps; a near-confident pixel a ~2px / 2 taps. Strength scales the max radius.
    const uint MAX_SAMPLE_COUNT = 8u;
    const float KERNEL_SHARPNESS = 0.666;
    float maxRadiusPx = sqrt(lerp(16.0 * 16.0, 2.0 * 2.0, centerValidity)) * Strength;
    uint sampleCount = clamp((uint)exp2(4.0 * Square(1.0 - centerValidity)), 2u, MAX_SAMPLE_COUNT);

    float angOff = (FrameIndex >= 0.0 ? fmod(FrameIndex * 23.0, 32.0) * TAU : 0.0) + Ign(float2(px)) * PI;
    float radiusMult = maxRadiusPx / pow((float)(MAX_SAMPLE_COUNT - 1), KERNEL_SHARPNESS);

    float4 sum = float4(Crunch(center.rgb), 1.0);
    [loop] for (uint si = 1u; si < MAX_SAMPLE_COUNT; ++si) {
        if (si >= sampleCount) break;
        float ang = (si + angOff) * GOLDEN_ANGLE;
        float radius = pow((float)si, KERNEL_SHARPNESS) * radiusMult;
        int2 spx = px + (int2)(float2(cos(ang), sin(ang)) * radius);
        if (any(spx < 0) || spx.x >= (int)W || spx.y >= (int)H) continue;

        float sDepth = Depth.Load(int3(spx, 0));
        if (sDepth >= 1.0) continue;                                    // skip sky neighbours
        float3 sVal = Indirect.Load(int3(spx, 0)).rgb;
        float3 sN = normalize(NormalTex.Load(int3(spx, 0)).xyz * 2.0 - 1.0);
        float sSsao = (UseSsao > 0.5) ? Ssao.Load(int3(spx, 0)).r : 1.0;

        // Edge-stopping weights: depth-plane (along the center normal's view-z component), normal similarity, and
        // SSAO steering (kajiya — SSAO is a cheap proxy for local occlusion geometry, so it follows crevices that
        // depth+normal miss). Depth term uses a relative ratio so it's scale-independent.
        float wt = 1.0;
        wt *= exp2(-100.0 * abs(centerN.z * (centerDepth / max(sDepth, 1e-6) - 1.0)));
        wt *= pow(saturate(dot(centerN, sN)), 8.0);
        if (UseSsao > 0.5) wt *= exp2(-20.0 * abs(sSsao - centerSsao));

        sum += float4(Crunch(sVal), 1.0) * wt;
    }

    float3 filtered = Uncrunch(sum.rgb / max(sum.a, 1e-5));
    Filtered[px] = float4(filtered, 1.0);
}
