// Auto-exposure metering for the DX12 backend. A single fullscreen pass into a 1×1 R16F target that holds
// the metered exposure EV100 (NOT a raw luminance anymore): sample a coarse grid of the HDR scene, geometric-
// mean the luminance (log space — the standard exposure metering, robust to a few bright pixels), convert
// that to a metered EV100 and clamp it to the auto limits. The composite reads this 1×1 EV and builds the
// exposure multiplier 1/(1.2 * 2^EV) — exactly the PostProcessSettings.ExposureMultiplier formula.
//
// CRITICAL (DX12 vs GL): the DX12 HDR scene target holds RAW physical radiance (it is NOT pre-exposed, unlike
// the GL path that multiplies the light uniforms CPU-side). So the meter reads ABSOLUTE luminance directly —
// it does NOT divide a pre-exposure back out. EV100 = -log2(lum) + LuminanceToEV, no preExposure term, or the
// EV would shift ~16 stops. Eye-adaptation EMA + metering-weight modes / histogram are a follow-up; this is
// the geometric mean (MeteringMode.Average), which is plenty for the common case.

cbuffer LumConstants : register(b0) {
    float LimitMin;       // EV floor the meter may adapt to (AutoExposureLimitMin)
    float LimitMax;       // EV ceiling (AutoExposureLimitMax)
    float2 _padLum;
};

Texture2D HdrColor : register(t0);
SamplerState LinearClamp : register(s0);

static const float LuminanceToEV = 3.0;   // log2(100/12.5) — the S/K photometric constant (matches the GL path)
static const float PleasingBias  = 1.0;   // +1 stop toward brighter (skies read less dull) — GL parity

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float Luminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

float4 PSMain(VSOut i) : SV_Target {
    const int GRID = 32;                       // 32×32 = 1024 samples across the frame
    float logSum = 0.0; int n = 0;
    [loop] for (int y = 0; y < GRID; y++) {
        [loop] for (int x = 0; x < GRID; x++) {
            float2 uv = (float2(x, y) + 0.5) / GRID;
            float3 hdr = HdrColor.SampleLevel(LinearClamp, uv, 0).rgb;
            float lum = max(Luminance(hdr), 1e-4);
            logSum += log(lum);
            n++;
        }
    }
    float avgLum = exp(logSum / max(n, 1));     // geometric mean luminance (absolute, raw radiance)

    // Metered EV100 from absolute scene luminance: EV100 = log2(L * S/K), S/K=100/12.5 → +LuminanceToEV.
    // Brighter scene → HIGHER EV → smaller multiplier (darker image), the photographic convention. NO
    // preExposure term (the DX12 buffer is raw radiance) — see header. PleasingBias lifts +1 stop.
    float meteredEv = log2(max(avgLum, 1e-6)) + LuminanceToEV - PleasingBias;
    meteredEv = clamp(meteredEv, LimitMin, LimitMax);
    return float4(meteredEv, meteredEv, meteredEv, 1.0);
}
