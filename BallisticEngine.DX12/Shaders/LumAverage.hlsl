// Auto-exposure metering for the DX12 backend. A single fullscreen pass into a 1×1 R16F target that holds
// the metered exposure EV100 (NOT a raw luminance anymore): sample a coarse grid of the HDR scene, geometric-
// mean the luminance (log space — the standard exposure metering, robust to a few bright pixels), convert
// that to a metered EV100 and clamp it to the auto limits. The composite reads this 1×1 EV and builds the
// exposure multiplier 1/(1.2 * 2^EV) — exactly the PostProcessSettings.ExposureMultiplier formula.
//
// V1 CALIBRATION FIX (Calibrated == 1, default): the metered EV uses the standard EV100 metering form
//   EV100 = log2(avgLum) + MeterAnchor
// but with MeterAnchor re-derived for the DX12 LUX-SCALED radiance instead of the photometric-cd/m² constant.
// MEASURED (BALLISTIC_DX12_EXPOSURE_DEBUG readback over the test matrix): the geomean luminance of a correctly-
// exposed dim interior is ~324, and the engine's documented correct exposure there is M = 1/(1.2*2^EV) ≈ 1e-5
// (EV ≈ 16.35). So MeterAnchor = 16.35 - log2(324) ≈ +8.0. The OLD constant (LuminanceToEV 3 - PleasingBias 1
// = +2) assumed cd/m² and under-shot the EV by ~6 stops → BistroInterior metered EV≈10.5 → M≈5.5e-4 (~55× too
// bright) → the milky white-out. With +8 the meter lands the dim interior at M≈1e-5 (correct) and only mildly
// stops down brighter scenes (lux-PRESERVING, not grey-normalizing) — Automatic now agrees with the Fixed EV
// path on a lux-calibrated scene (Manual≈Auto, the V1 gate), while still adapting for genuinely dark/bright
// lighting via the [LimitMin,LimitMax] clamp.
//
// LEGACY (Calibrated == 0, BALLISTIC_DX12_EXPOSURE_CALIB=0 kill-switch): the old cd/m² photometric anchor (+2),
// kept for A/B and to prove the byte-identical pre-V1 fallback. EV100 = log2(lum) + LuminanceToEV - PleasingBias.
//
// Eye-adaptation EMA (temporal smoothing of the metered EV so Automatic doesn't flicker in motion) plus
// metering-weight modes / histogram percentile rejection are still a follow-up (V1b) — this is the
// instantaneous geometric mean, which is correct + stable for the static deterministic-capture path.

cbuffer LumConstants : register(b0) {
    float LimitMin;       // EV floor the meter may adapt to (AutoExposureLimitMin)
    float LimitMax;       // EV ceiling (AutoExposureLimitMax)
    float Calibrated;     // > 0.5 = lux-anchored EV (V1 fix); 0 = legacy cd/m² EV; > 1.5 = DEBUG emit avgLum
    float _padLum;
};

// Lux-scale meter anchor (see header). avgLum≈324 on a correctly-exposed dim interior → EV≈16.35 → M≈1e-5.
static const float LuxMeterAnchor = 8.0;

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

    // Metered EV100. Two anchors (see header):
    //  - Calibrated (default): EV = log2(avgLum) + LuxMeterAnchor(+8), the EV100 form re-anchored for the
    //    lux-scaled DX12 radiance so a correctly-exposed dim interior (avgLum~324) meters to EV~16.35 → M~1e-5.
    //  - Legacy: the absolute cd/m² photometric formula (~6 stops too low on the lux-scaled DX12 buffer).
    // Brighter scene → HIGHER EV → smaller multiplier (darker image), the photographic convention either way.
    if (Calibrated > 1.5)                        // DEBUG: emit raw geomean luminance for CPU readback (V1 calibration)
        return float4(avgLum, avgLum, avgLum, 1.0);
    float meteredEv = (Calibrated > 0.5)
        ? log2(max(avgLum, 1e-8)) + LuxMeterAnchor                // lux-anchored EV100 (V1 fix)
        : log2(max(avgLum, 1e-6)) + LuminanceToEV - PleasingBias; // legacy cd/m² photometric (kill-switch)
    meteredEv = clamp(meteredEv, LimitMin, LimitMax);
    return float4(meteredEv, meteredEv, meteredEv, 1.0);
}
