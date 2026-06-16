// Final composite for the DX12 backend: HDR scene color → exposure → ACES tonemap → +bloom → sRGB → LDR.
// The scene renders RAW HDR radiance into an R16F target (the material/sky/fog shaders no longer tonemap
// inline); this single pass owns the HDR→display transform, which is what lets auto-exposure and bloom exist
// (they need the HDR signal before tonemapping). Fullscreen triangle into the LDR backbuffer.
//
// EXPOSURE (P1): physical EV100, mirroring PostProcessSettings.ExposureMultiplier = LegacyMul/(1.2*2^(EV-comp)).
//   - Manual / Fixed mode: ExposureMul is resolved CPU-side from the Exposure volume's EV dial and arrives ready.
//   - Automatic mode: the AvgLum 1×1 target now holds the METERED EV100 (LumAverage.hlsl); this pass turns it
//     into the multiplier with the same formula, so the EV dials/limits in the Exposure volume drive DX12.

cbuffer CompositeConstants : register(b0) {
    float ExposureMul;    // resolved multiplier for Manual/Fixed (and the legacy manual override)
    float BloomIntensity; // 0 = no bloom
    float AutoExposure;   // > 0.5 = derive the multiplier from the metered-EV target (Automatic mode)
    float LegacyMul;      // PostProcessSettings.Exposure (raw manual multiplier on top of EV; 1 = untouched)
    float Compensation;   // exposure compensation in stops (Automatic mode applies it on top of the metered EV)
    float UseAo;          // > 0.5 = multiply by the SSAO texture
    float2 _pad2;
};

Texture2D HdrColor : register(t0);
Texture2D BloomTex : register(t1);
Texture2D MeteredEv : register(t2);  // 1×1 metered EV100 (auto-exposure); Automatic mode only
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

    // Exposure multiplier: resolved CPU-side for Manual/Fixed; from the metered EV100 for Automatic.
    float exposure = ExposureMul;
    if (AutoExposure > 0.5) {
        float ev = MeteredEv.SampleLevel(LinearClamp, float2(0.5, 0.5), 0).r;
        exposure = LegacyMul / (1.2 * exp2(ev - Compensation));   // == PostProcessSettings.ExposureMultiplier
    }
    float3 mapped = ACESFilm(hdr * exposure);
    return float4(pow(mapped, 1.0 / 2.2), 1.0);   // sRGB-encode for the UNORM backbuffer/BMP
}
