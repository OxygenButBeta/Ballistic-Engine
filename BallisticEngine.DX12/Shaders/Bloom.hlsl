// Progressive dual-filter bloom for the DX12 backend (Jimenez, SIGGRAPH 2014 — "Next Generation Post
// Processing in Call of Duty: Advanced Warfare"), a faithful port of the GL Bloom_Down/Bloom_Up pair.
// A mip pyramid (half, quarter, ...) is built by repeated 13-tap downsampling; the first (HDR) level
// applies a Karis-weighted average to kill firefly flicker + a soft-knee threshold; then each smaller
// level is tent-filtered (9-tap) back up and ADDED onto the next larger level (additive blend PSO). The
// half-res result feeds the composite (BloomTex × BloomIntensity).
//
// Entry points: VSMain (fullscreen triangle), PSDownThreshold (level 0 only), PSDown (levels 1..N),
// PSUp (additive tent upsample). The single shared b0 cbuffer carries the source texel size + threshold/knee.

cbuffer BloomConstants : register(b0) {
    float2 TexelSize;    // 1 / SOURCE size (the level being read), for tap spacing
    float Threshold;     // HDR luminance above which pixels bloom (level-0 threshold pass only)
    float Knee;          // soft-knee half-width below the threshold (0 = hard cutoff)
};

Texture2D Source : register(t0);
SamplerState LinearClamp : register(s0);

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float3 SampleSrc(float2 uv) { return max(Source.SampleLevel(LinearClamp, uv, 0).rgb, 0.0); }

// Karis average weight: down-weight very bright samples so single HDR fireflies don't flicker as huge
// blobs after the blur. (Reads the max channel, the GL implementation's brightness proxy.)
float KarisWeight(float3 c) { return 1.0 / (1.0 + max(c.r, max(c.g, c.b))); }

// Soft-knee threshold: keep energy above Threshold, fade smoothly across a [Threshold-Knee, Threshold]
// band, hard zero below it. Knee comes from the Bloom volume (the GL path hardcoded threshold*0.5).
float3 ApplyThreshold(float3 c) {
    float lum = max(c.r, max(c.g, c.b));
    float knee = max(Knee, 1e-4);
    float soft = clamp(lum - Threshold + knee, 0.0, 2.0 * knee);
    soft = (soft * soft) / (4.0 * knee + 1e-4);
    float contribution = max(soft, lum - Threshold) / max(lum, 1e-4);
    return c * clamp(contribution, 0.0, 1.0);
}

// 13-tap Jimenez downsample. `applyThreshold` (the level-0 HDR tap) Karis-averages 2x2 groups to kill
// fireflies, then thresholds; deeper levels use the plain weighted 13-tap (energy-preserving, no Karis).
float4 Downsample13(float2 uv, bool applyThreshold) {
    float2 t = TexelSize;
    float3 a = SampleSrc(uv + t * float2(-2.0,  2.0));
    float3 b = SampleSrc(uv + t * float2( 0.0,  2.0));
    float3 c = SampleSrc(uv + t * float2( 2.0,  2.0));
    float3 d = SampleSrc(uv + t * float2(-2.0,  0.0));
    float3 e = SampleSrc(uv);
    float3 f = SampleSrc(uv + t * float2( 2.0,  0.0));
    float3 g = SampleSrc(uv + t * float2(-2.0, -2.0));
    float3 h = SampleSrc(uv + t * float2( 0.0, -2.0));
    float3 i = SampleSrc(uv + t * float2( 2.0, -2.0));
    float3 j = SampleSrc(uv + t * float2(-1.0,  1.0));
    float3 k = SampleSrc(uv + t * float2( 1.0,  1.0));
    float3 l = SampleSrc(uv + t * float2(-1.0, -1.0));
    float3 m = SampleSrc(uv + t * float2( 1.0, -1.0));

    float3 color;
    if (applyThreshold) {
        float3 g0 = (a + b + d + e) * 0.25;
        float3 g1 = (b + c + e + f) * 0.25;
        float3 g2 = (d + e + g + h) * 0.25;
        float3 g3 = (e + f + h + i) * 0.25;
        float3 g4 = (j + k + l + m) * 0.25;
        color = g0 * (0.125 * KarisWeight(g0))
              + g1 * (0.125 * KarisWeight(g1))
              + g2 * (0.125 * KarisWeight(g2))
              + g3 * (0.125 * KarisWeight(g3))
              + g4 * (0.5   * KarisWeight(g4));
        color = ApplyThreshold(color);
    }
    else {
        color = e * 0.125
              + (a + c + g + i) * 0.03125
              + (b + d + f + h) * 0.0625
              + (j + k + l + m) * 0.125;
    }
    return float4(color, 1.0);
}

float4 PSDownThreshold(VSOut i) : SV_Target { return Downsample13(i.Uv, true); }
float4 PSDown(VSOut i)          : SV_Target { return Downsample13(i.Uv, false); }

// 9-tap tent upsample (Jimenez). Output is ADDITIVELY blended onto the larger destination level by the
// up-pass PSO's One/One blend state — exactly the GL fixed-function additive upsample.
float4 PSUp(VSOut i) : SV_Target {
    float2 t = TexelSize;   // 1 / source (the SMALLER level being read)
    float3 a = SampleSrc(i.Uv + float2(-t.x,  t.y));
    float3 b = SampleSrc(i.Uv + float2( 0.0,  t.y));
    float3 c = SampleSrc(i.Uv + float2( t.x,  t.y));
    float3 d = SampleSrc(i.Uv + float2(-t.x,  0.0));
    float3 e = SampleSrc(i.Uv);
    float3 f = SampleSrc(i.Uv + float2( t.x,  0.0));
    float3 g = SampleSrc(i.Uv + float2(-t.x, -t.y));
    float3 h = SampleSrc(i.Uv + float2( 0.0, -t.y));
    float3 k = SampleSrc(i.Uv + float2( t.x, -t.y));
    float3 color = e * 4.0 + (b + d + f + h) * 2.0 + (a + c + g + k);
    return float4(color / 16.0, 1.0);
}
