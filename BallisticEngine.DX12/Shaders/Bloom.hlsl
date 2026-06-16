// Bloom for the DX12 backend: bright-pass the HDR scene, then a separable Gaussian blur (H then V).
// Output is added into the final composite (BloomTex × BloomIntensity). Runs at HALF resolution (the
// bloom target is half the backbuffer) — cheap and the blur hides the lower res. Three entry points:
// PSBrightPass, PSBlurH, PSBlurV, all over a fullscreen triangle.

cbuffer BloomConstants : register(b0) {
    float Threshold;     // HDR luminance above which pixels bloom
    float2 TexelSize;    // 1/target size, for the blur tap spacing
    float _pad;
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

float Luminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

// Bright-pass: keep only HDR above Threshold, soft-knee so it ramps in rather than hard-clips.
float4 PSBrightPass(VSOut i) : SV_Target {
    float3 c = Source.SampleLevel(LinearClamp, i.Uv, 0).rgb;
    float lum = Luminance(c);
    float contrib = max(lum - Threshold, 0.0) / max(lum, 1e-4);   // fraction of the pixel that blooms
    return float4(c * contrib, 1.0);
}

// 9-tap Gaussian, separable (weights for sigma ~2).
static const float W[5] = { 0.227027, 0.1945946, 0.1216216, 0.054054, 0.016216 };

float4 Blur(float2 uv, float2 dir) {
    float3 sum = Source.SampleLevel(LinearClamp, uv, 0).rgb * W[0];
    [unroll] for (int k = 1; k < 5; k++) {
        float2 off = dir * (k * TexelSize);
        sum += Source.SampleLevel(LinearClamp, uv + off, 0).rgb * W[k];
        sum += Source.SampleLevel(LinearClamp, uv - off, 0).rgb * W[k];
    }
    return float4(sum, 1.0);
}
float4 PSBlurH(VSOut i) : SV_Target { return Blur(i.Uv, float2(1, 0)); }
float4 PSBlurV(VSOut i) : SV_Target { return Blur(i.Uv, float2(0, 1)); }
