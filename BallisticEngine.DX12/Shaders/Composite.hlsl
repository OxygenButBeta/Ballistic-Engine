// Final composite for the DX12 backend: HDR scene color → exposure → ACES tonemap → +bloom → sRGB → LDR.
// The scene now renders RAW HDR radiance into an R16F target (the material/sky/fog shaders no longer
// tonemap inline); this single pass owns the HDR→display transform, which is what lets auto-exposure and
// bloom exist (they need the HDR signal before tonemapping). Fullscreen triangle into the LDR backbuffer.

cbuffer CompositeConstants : register(b0) {
    float Exposure;       // manual exposure (used when AutoExposure < 0.5); fixed stand-in fallback
    float BloomIntensity; // 0 = no bloom
    float AutoExposure;   // > 0.5 = derive exposure from the avg-luminance metering target
    float ExposureKey;    // middle-grey key for auto-exposure (~0.18 * tuning)
    float UseAo;          // > 0.5 = multiply by the SSAO texture
    float3 _pad2;
};

Texture2D HdrColor : register(t0);
Texture2D BloomTex : register(t1);
Texture2D AvgLum   : register(t2);   // 1×1 geometric-mean scene luminance (auto-exposure metering)
Texture2D AoTex    : register(t3);   // screen-space AO (1 = unoccluded); UseAo gates it
SamplerState LinearClamp : register(s0);

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float3 ACESFilm(float3 x) {
    const float a = 2.51, b = 0.03, c = 2.43, d = 0.59, e = 0.14;
    return saturate((x * (a * x + b)) / (x * (c * x + d) + e));
}

float4 PSMain(VSOut i) : SV_Target {
    float3 hdr = HdrColor.SampleLevel(LinearClamp, i.Uv, 0).rgb;
    if (UseAo > 0.5) {
        float ao = AoTex.SampleLevel(LinearClamp, i.Uv, 0).r;
        hdr *= ao;   // forward-path approximation: dim the lit color by AO (before bloom adds glow)
    }
    if (BloomIntensity > 0.0)
        hdr += BloomTex.SampleLevel(LinearClamp, i.Uv, 0).rgb * BloomIntensity;

    // Exposure: auto (Key / average scene luminance) or the manual constant.
    float exposure = Exposure;
    if (AutoExposure > 0.5) {
        float avgLum = max(AvgLum.SampleLevel(LinearClamp, float2(0.5, 0.5), 0).r, 1e-4);
        exposure = ExposureKey / avgLum;
    }
    float3 mapped = ACESFilm(hdr * exposure);
    return float4(pow(mapped, 1.0 / 2.2), 1.0);   // sRGB-encode for the UNORM backbuffer/BMP
}
