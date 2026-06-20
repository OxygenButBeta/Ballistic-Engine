// Histogram-based auto-exposure metering for the DX12 backend (ExposureMode.AutomaticHistogram). The
// production / Frostbite / Unreal eye-adaptation equivalent: instead of a single geometric-mean of luminance
// (LumAverage.hlsl, the Automatic path), this builds a 256-bin histogram of LOG-luminance over the HDR scene,
// rejects the bottom HistogramFilterMin% and top HistogramFilterMax% of WEIGHTED samples (percentile clip),
// and averages only the surviving middle band → a target EV robust to a bright window or a dark corner that
// would otherwise drag a plain average. A temporal EMA (the same stops/sec asymmetric ease as LumAverage)
// settles the adapted EV.
//
// THREE compute kernels, run in order (CPU records all three onto the frame list):
//   CSClear     — zero the 256-uint histogram buffer (one thread per bin).
//   CSBuild     — one thread per source pixel of a downsampled grid: compute weighted log-luminance, find its
//                 bin, atomic-add the metering weight into that bin (fixed-point — see WEIGHT_SCALE).
//   CSResolve   — single thread: percentile-clip the histogram, average the surviving log-luminance, convert
//                 to a metered EV (same LuxMeterAnchor as LumAverage so the two Automatic paths agree), then
//                 EMA toward it (reads prev adapted EV, writes this frame's adapted EV to a 1×1 R16F UAV).
//
// ABSOLUTE LUMINANCE: the HDR buffer is PRE-EXPOSED only when the composite reads it — at metering time the
// scene color target holds RAW radiance (the LumAverage path samples the same HdrColor and treats it as
// absolute, "geometric mean luminance (absolute, raw radiance)"). So there is no multiplier to divide out
// here: the meter sees raw radiance directly, identical to LumAverage. (The class comment on the buffer is
// "the buffer is pre-exposed; the meter divides the frame's multiplier back out" — but in THIS engine the
// metering input is the un-exposed HDR target, so the divisor is 1; we replicate LumAverage exactly.)

cbuffer HistConstants : register(b0) {
    uint  SrcWidth;        // downsample grid width  (CSBuild dispatch coverage)
    uint  SrcHeight;       // downsample grid height
    float MinLogLum;       // log2 luminance mapped to bin 0
    float InvLogLumRange;  // 1 / (MaxLogLum - MinLogLum)  (bin = (log2(lum)-MinLogLum) * range * 255)

    float MeteringMode;    // 0 = Average (uniform), 1 = CenterWeighted (gaussian), 2 = Spot (center circle)
    float LuxMeterAnchor;  // EV100 = avgLogLum2 + anchor  (matches LumAverage's +8 lux anchor)
    float LimitMin;        // EV floor (AutoExposureLimitMin)
    float LimitMax;        // EV ceiling (AutoExposureLimitMax)

    float FilterMin;       // reject the bottom FilterMin% of weighted samples (0..100)
    float FilterMax;       // keep up to FilterMax% (reject above it) (0..100)
    float DeltaTime;       // frame delta seconds (stops/sec ease)
    float SpeedDarkToLight;// stops/sec when the scene brightens (metered EV rises) — fast

    float SpeedLightToDark;// stops/sec when the scene darkens (metered EV falls) — slow
    float Reset;           // > 0.5 = snap to metered EV (first frame / deterministic), no temporal ease
    float _pad0;
    float _pad1;
};

// Fixed-point weight accumulation: InterlockedAdd is integer-only, so the per-pixel float metering weight is
// scaled to a uint. 4096 keeps plenty of headroom under a 256-grid build (max ~1024*4096 ≈ 4.2M << 2^32).
static const float WEIGHT_SCALE = 4096.0;
static const uint  BIN_COUNT    = 256;

RWByteAddressBuffer Histogram : register(u0);   // 256 uints: weighted sample count per log-lum bin
Texture2D<float4>   HdrColor   : register(t0);  // raw HDR scene radiance (un-exposed at metering time)
Texture2D<float>    PrevAdaptedEv : register(t1); // 1×1 history — last frame's adapted EV (ping-ponged, no readback)
RWTexture2D<float>  AdaptedEvOut  : register(u1); // 1×1 — this frame's adapted EV (composite reads it)
SamplerState        LinearClamp   : register(s0); // static clamp sampler (root-sig static sampler)

float Luminance(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

// Metering weight for a normalized screen position (0..1). Average = 1 everywhere; CenterWeighted = gaussian
// falloff from center; Spot = 1 inside a small center circle, 0 outside. Mirrors MeteringMode.
float MeterWeight(float2 uv) {
    if (MeteringMode < 0.5) return 1.0;                       // Average
    float2 d = uv - 0.5;
    float r2 = dot(d, d);
    if (MeteringMode < 1.5)                                   // CenterWeighted (gaussian, sigma ~0.28)
        return exp(-r2 / (2.0 * 0.08));
    return (r2 < 0.15 * 0.15) ? 1.0 : 0.0;                    // Spot (center circle radius 0.15)
}

// ---- CSClear: zero the histogram buffer (one thread per bin) ------------------------------------------------
[numthreads(256, 1, 1)]
void CSClear(uint3 id : SV_DispatchThreadID) {
    if (id.x < BIN_COUNT) Histogram.Store(id.x * 4, 0);
}

// ---- CSBuild: scatter weighted log-luminance into bins (one thread per downsample-grid texel) ---------------
[numthreads(16, 16, 1)]
void CSBuild(uint3 id : SV_DispatchThreadID) {
    if (id.x >= SrcWidth || id.y >= SrcHeight) return;
    float2 uv = (float2(id.xy) + 0.5) / float2(SrcWidth, SrcHeight);
    float weight = MeterWeight(uv);
    if (weight <= 0.0) return;

    float3 hdr = HdrColor.SampleLevel(LinearClamp, uv, 0).rgb;
    float lum = max(Luminance(hdr), 1e-4);                    // clamp > 0 before log (NaN gotcha)
    float logLum = log2(lum);
    // Map log-luminance to [0,255]; pixels below the range floor go to bin 0 (the black/empty band the
    // percentile clip is meant to reject anyway).
    float t = saturate((logLum - MinLogLum) * InvLogLumRange);
    uint bin = (uint)(t * 255.0 + 0.5);
    Histogram.InterlockedAdd(bin * 4, (uint)(weight * WEIGHT_SCALE + 0.5));
}

// ---- CSResolve: percentile-clip → average log-lum → metered EV → temporal EMA (single thread) ---------------
[numthreads(1, 1, 1)]
void CSResolve(uint3 id : SV_DispatchThreadID) {
    if (id.x != 0) return;

    // Total weighted samples (for the percentile thresholds).
    float total = 0.0;
    [loop] for (uint b = 0; b < BIN_COUNT; b++)
        total += (float)Histogram.Load(b * 4);
    total /= WEIGHT_SCALE;

    // Percentile band: reject the bottom FilterMin% and everything above FilterMax%. Walk the cumulative
    // weighted count, accumulating the bin-center log-luminance ONLY for samples whose cumulative percentile
    // falls inside (FilterMin, FilterMax]. This is the robustness win: a few very bright / very dark pixels
    // sit outside the band and never touch the average.
    float lo = total * saturate(FilterMin * 0.01);
    float hi = total * saturate(FilterMax * 0.01);
    float logLumRange = 1.0 / max(InvLogLumRange, 1e-8);

    float weightedLogSum = 0.0;
    float acceptedWeight = 0.0;
    float cumulative = 0.0;
    [loop] for (uint i = 0; i < BIN_COUNT; i++) {
        float w = (float)Histogram.Load(i * 4) / WEIGHT_SCALE;
        if (w <= 0.0) continue;
        float binStart = cumulative;
        float binEnd = cumulative + w;
        cumulative = binEnd;
        // Overlap of [binStart,binEnd] with the accepted band (lo,hi].
        float a = max(binStart, lo);
        float c = min(binEnd, hi);
        float accepted = max(c - a, 0.0);
        if (accepted <= 0.0) continue;
        float binLogLum = MinLogLum + ((float)i / 255.0) * logLumRange;  // bin-center log2 luminance
        weightedLogSum += binLogLum * accepted;
        acceptedWeight += accepted;
    }

    // Average log-luminance of the surviving band. If nothing survived (empty / all-rejected frame) fall back
    // to the band-center log-luminance so the meter is well-defined (a SELECT, never an arithmetic blend).
    float avgLogLum = (acceptedWeight > 1e-5)
        ? (weightedLogSum / acceptedWeight)
        : (MinLogLum + 0.5 * logLumRange);

    // Metered EV100 — same lux anchor as LumAverage so AutomaticHistogram and Automatic agree on calibration.
    float meteredEv = avgLogLum + LuxMeterAnchor;
    meteredEv = clamp(meteredEv, LimitMin, LimitMax);

    // Temporal EMA: ease from last frame's adapted EV toward this frame's metered EV. Frame-rate independent
    // (alpha = 1 - exp(-dt*speed)); brighten fast (SpeedDarkToLight), darken slow (SpeedLightToDark). Reset
    // snaps (first frame / deterministic capture) so paused captures stay byte-identical to the instantaneous
    // meter. Endpoints are post-clamp → eased EV stays in [LimitMin, LimitMax].
    float prevEv = PrevAdaptedEv.Load(int3(0, 0, 0));
    float adaptedEv = meteredEv;
    if (Reset <= 0.5) {
        float speed = (meteredEv > prevEv) ? SpeedDarkToLight : SpeedLightToDark;
        float alpha = saturate(1.0 - exp(-max(DeltaTime, 0.0) * max(speed, 0.0)));
        adaptedEv = prevEv + (meteredEv - prevEv) * alpha;
    }
    AdaptedEvOut[int2(0, 0)] = adaptedEv;
}
