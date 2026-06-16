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
    float Tonemap;        // 0 = AgX (default), 1 = ACES (BALLISTIC_DX12_TONEMAP=aces A/B door)
    float _pad2;
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

// ===== AgX (Troy Sobotka minimal fit, the Blender 4.x default) ==============================================
// AgX desaturates extreme highlights gracefully toward white instead of skewing hue like ACES (the saturated
// blue-sky / orange-sun skew that read as "çiğ"). It transforms into a wide-gamut working space, sigmoid-
// compresses in log2, then transforms back. Default-contrast 6th-order polynomial approximation (Filament).
static const float3x3 AGX_IN = float3x3(
    0.842479062253094,  0.0423282422610123, 0.0423756549057051,
    0.0784335999999992, 0.878468636469772,  0.0784336,
    0.0792237451477643, 0.0791661274605434, 0.879142973793104);
static const float3x3 AGX_OUT = float3x3(
     1.19687900512017,  -0.0528968517574562, -0.0529716355144438,
    -0.0980208811401368, 1.15190312990417,   -0.0980434501171241,
    -0.0990297440797205,-0.0989611768448433,  1.15107367264116);

float3 AgxDefaultContrastApprox(float3 x) {
    float3 x2 = x * x;
    float3 x4 = x2 * x2;
    return  + 15.5     * x4 * x2
            - 40.14    * x4 * x
            + 31.96    * x4
            - 6.868    * x2 * x
            + 0.4298   * x2
            + 0.1191   * x
            - 0.00232;
}

float3 AgX(float3 col) {
    const float minEv = -12.47393, maxEv = 4.026069;   // log2 exposure range of the AgX sigmoid
    col = mul(AGX_IN, col);
    col = clamp(log2(max(col, 1e-10)), minEv, maxEv);
    col = (col - minEv) / (maxEv - minEv);             // normalize to [0,1]
    col = AgxDefaultContrastApprox(col);               // sigmoid in log space
    // AgX "base" look: the default Blender look applies a mild punch (offset/slope/power/saturation). Keep it
    // gentle so the calibrated PBR output isn't pushed around — a small slope+saturation lift reads filmic.
    const float3 lw = float3(0.2126, 0.7152, 0.0722);
    float luma = dot(col, lw);
    col = luma + 1.05 * (col - luma);                  // saturation 1.05
    col = mul(AGX_OUT, col);
    return saturate(col);                              // AGX_OUT output is already ~display-linear sRGB
}

// Exact piecewise sRGB OETF (linear → display), not the pow(1/2.2) approximation.
float3 LinearToSrgb(float3 c) {
    c = saturate(c);
    float3 lo = c * 12.92;
    float3 hi = 1.055 * pow(c, 1.0 / 2.4) - 0.055;
    return float3(c.x < 0.0031308 ? lo.x : hi.x,
                  c.y < 0.0031308 ? lo.y : hi.y,
                  c.z < 0.0031308 ? lo.z : hi.z);
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
    float3 exposed = max(hdr * exposure, 0.0);   // tonemappers want non-negative input (AgX log2, ACES)

    // AgX (default) desaturates highlights toward white — far less "çiğ" than ACES on saturated sky/sun.
    // ACES stays available via BALLISTIC_DX12_TONEMAP=aces for A/B.
    float3 mapped = (Tonemap > 0.5) ? ACESFilm(exposed) : AgX(exposed);
    return float4(LinearToSrgb(mapped), 1.0);    // exact piecewise sRGB OETF for the UNORM backbuffer/BMP
}
