// Final composite for the DX12 backend: HDR scene color → exposure → ACES tonemap → +bloom → sRGB → LDR.
// The scene now renders RAW HDR radiance into an R16F target (the material/sky/fog shaders no longer
// tonemap inline); this single pass owns the HDR→display transform, which is what lets auto-exposure and
// bloom exist (they need the HDR signal before tonemapping). Fullscreen triangle into the LDR backbuffer.

cbuffer CompositeConstants : register(b0) {
    float Exposure;       // linear pre-tonemap scale (auto-exposure drives this; fixed stand-in for now)
    float BloomIntensity; // 0 = no bloom
    float2 _pad;
};

Texture2D HdrColor : register(t0);
Texture2D BloomTex : register(t1);
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
    if (BloomIntensity > 0.0)
        hdr += BloomTex.SampleLevel(LinearClamp, i.Uv, 0).rgb * BloomIntensity;
    float3 mapped = ACESFilm(hdr * Exposure);
    return float4(pow(mapped, 1.0 / 2.2), 1.0);   // sRGB-encode for the UNORM backbuffer/BMP
}
