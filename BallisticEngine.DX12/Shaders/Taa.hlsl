// Temporal anti-aliasing for the DX12 deferred renderer, ported from the GL TAA_Frag. Reprojects last
// frame's accumulated image using the G-buffer MOTION vectors (prevUV - currUV, written by the geometry
// pass) and blends it with the jittered current frame. YCoCg variance clipping + Catmull-Rom history
// resample + luma-adaptive feedback (same quality features as the GL path). Operates on the HDR scene
// color before tonemap.
//
// Jitter is applied to the camera PROJECTION on the CPU (the whole frame is jittered); the motion vectors
// are jitter-free (computed from the UNJITTERED view*proj), so reprojection is a stable per-pixel add.
// Volume-driven feedback (PostFX.TaaFeedback). Motion-based reprojection also tracks dynamic geometry,
// unlike the old depth+matrix camera-only reprojection.

cbuffer TaaConstants : register(b0) {
    float    Feedback;         // history weight (0..0.97)
    float    ValidHistory;     // >0.5 = blend, else passthrough (first frame / camera cut)
    float2   TexelSize;        // 1 / render size
};

Texture2D CurrentTex : register(t0);
Texture2D HistoryTex : register(t1);
Texture2D MotionTex  : register(t2);   // RG = screen-space motion (prevUV - currUV)
Texture2D DepthTex   : register(t3);   // C5: scene depth (R32F) for closest-depth velocity dilation
SamplerState LinearClamp : register(s0);

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

// NaN/Inf scrub as a true component SELECT (mix(v,0,flag) keeps NaN: NaN*0==NaN). TAA is a feedback loop —
// a single poisoned pixel re-blends every frame and spreads through the history resample.
float3 Sanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}
float3 RGBToYCoCg(float3 c) {
    return float3(0.25 * c.r + 0.5 * c.g + 0.25 * c.b,
                  0.5 * c.r - 0.5 * c.b,
                 -0.25 * c.r + 0.5 * c.g - 0.25 * c.b);
}
float3 YCoCgToRGB(float3 c) {
    return float3(c.x + c.y - c.z, c.x + c.z, c.x - c.y - c.z);
}

// 9-tap Catmull-Rom (Karis/Jimenez): sharp history resample without ringing blowups.
float3 SampleHistoryCatmullRom(float2 uv, float2 texSize) {
    float2 samplePos = uv * texSize;
    float2 texPos1 = floor(samplePos - 0.5) + 0.5;
    float2 f = samplePos - texPos1;
    float2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    float2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    float2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    float2 w3 = f * f * (-0.5 + 0.5 * f);
    float2 w12 = w1 + w2;
    float2 offset12 = w2 / max(w12, 1e-5);
    float2 texPos0 = (texPos1 - 1.0) / texSize;
    float2 texPos3 = (texPos1 + 2.0) / texSize;
    float2 texPos12 = (texPos1 + offset12) / texSize;
    float3 r =
        HistoryTex.SampleLevel(LinearClamp, float2(texPos0.x,  texPos0.y), 0).rgb  * (w0.x  * w0.y) +
        HistoryTex.SampleLevel(LinearClamp, float2(texPos12.x, texPos0.y), 0).rgb  * (w12.x * w0.y) +
        HistoryTex.SampleLevel(LinearClamp, float2(texPos3.x,  texPos0.y), 0).rgb  * (w3.x  * w0.y) +
        HistoryTex.SampleLevel(LinearClamp, float2(texPos0.x,  texPos12.y), 0).rgb * (w0.x  * w12.y) +
        HistoryTex.SampleLevel(LinearClamp, float2(texPos12.x, texPos12.y), 0).rgb * (w12.x * w12.y) +
        HistoryTex.SampleLevel(LinearClamp, float2(texPos3.x,  texPos12.y), 0).rgb * (w3.x  * w12.y) +
        HistoryTex.SampleLevel(LinearClamp, float2(texPos0.x,  texPos3.y), 0).rgb  * (w0.x  * w3.y) +
        HistoryTex.SampleLevel(LinearClamp, float2(texPos12.x, texPos3.y), 0).rgb  * (w12.x * w3.y) +
        HistoryTex.SampleLevel(LinearClamp, float2(texPos3.x,  texPos3.y), 0).rgb  * (w3.x  * w3.y);
    return max(r, 0.0.xxx);
}

float4 PSMain(VSOut i) : SV_Target {
    float3 current = Sanitize(CurrentTex.SampleLevel(LinearClamp, i.Uv, 0).rgb);
    if (ValidHistory < 0.5)
        return float4(current, 1.0);

    // C5 — DILATED closest-depth velocity. Sampling motion at the pixel CENTRE makes AA'd silhouette edges pick
    // the BACKGROUND motion (the edge pixel is a blend of fg+bg, its centre depth/motion may be either) → moving
    // object edges ghost/smear. kajiya dilates: search the 3x3 neighbourhood for the CLOSEST-depth pixel and use
    // ITS motion — the nearer surface (the foreground object) wins, so its edge reprojects correctly. Cheap (9
    // depth taps), no history, deterministic.
    float2 closestOff = 0.0.xx;
    float closestDepth = DepthTex.SampleLevel(LinearClamp, i.Uv, 0).r;
    [unroll] for (int dx = -1; dx <= 1; dx++)
    [unroll] for (int dy = -1; dy <= 1; dy++) {
        if (dx == 0 && dy == 0) continue;
        float2 off = float2(dx, dy) * TexelSize;
        float d = DepthTex.SampleLevel(LinearClamp, i.Uv + off, 0).r;
        if (d < closestDepth) { closestDepth = d; closestOff = off; }   // smaller depth = nearer (DX z[0,1])
    }
    float2 motion = MotionTex.SampleLevel(LinearClamp, i.Uv + closestOff, 0).rg;
    float2 prevUV = i.Uv + motion;
    if (prevUV.x < 0.0 || prevUV.x > 1.0 || prevUV.y < 0.0 || prevUV.y > 1.0)
        return float4(current, 1.0);

    float2 texSize = 1.0 / TexelSize;
    float3 history = RGBToYCoCg(Sanitize(SampleHistoryCatmullRom(prevUV, texSize)));

    // 3x3 neighborhood moments in YCoCg + the neighborhood luma max (C6 firefly clamp source).
    float3 m1 = 0.0.xxx, m2 = 0.0.xxx;
    float neighMaxLuma = 0.0;
    [unroll] for (int x = -1; x <= 1; x++)
    [unroll] for (int y = -1; y <= 1; y++) {
        float3 c = RGBToYCoCg(Sanitize(CurrentTex.SampleLevel(LinearClamp, i.Uv + float2(x, y) * TexelSize, 0).rgb));
        m1 += c; m2 += c * c;
        if (!(x == 0 && y == 0)) neighMaxLuma = max(neighMaxLuma, c.x);   // brightest NEIGHBOUR (exclude centre)
    }
    float3 mean = m1 / 9.0;
    float3 sigma = sqrt(max(m2 / 9.0 - mean * mean, 0.0.xxx));

    float3 currYCoCg = RGBToYCoCg(current);
    float3 currRaw = currYCoCg;                                           // pre-firefly (for honest disagreement)

    // C6 — firefly-clamped TAA input. RT reflections + Lumen/DDGI card churn enter the HDR BEFORE TAA with no
    // other denoiser, so a lone bright pixel (finite but huge) would poison the feedback loop and crawl. If the
    // centre luma far exceeds its brightest neighbour, it's likely a firefly — pull its luma down (chroma kept).
    // Bug-hunt #4 F3: a SINGLE-pixel REAL highlight (star/glint) has dark neighbours too, so a tight cap would
    // crush it. Use a generous 4× headroom + a soft pull (only fireflies >> neighbours bite hard; a real glint
    // within a few× survives), preserving legitimate sub-pixel highlights TAA is meant to resolve.
    float fireflyCap = neighMaxLuma * 4.0 + 0.05;
    if (currYCoCg.x > fireflyCap) currYCoCg.x = lerp(currYCoCg.x, fireflyCap, 0.85);

    // C4 — confidence-WIDENED clamp box. A fixed Gamma over-clamps where current and history genuinely agree AND
    // the pixel is static (eroding accumulated detail → blur). Bug-hunt #4 F1/F2 fixes:
    //  - confidence is gated by MOTION (a moving pixel never widens the box → no slow bright-trail smear), and
    //    measured from the FULL YCoCg vector on the PRE-firefly current (so a firefly can't fake high confidence,
    //    F3 second-order), not luma-only.
    //  - the box only widens the LUMA extent; CHROMA stays at a fixed tight Gamma (F2: a luma-only confidence must
    //    not loosen the chroma box → no equal-luma coloured ghosts).
    //  - Gamma cap pulled in to 1.6 and the soft knee tightened to smoothstep(1,2) so full clip arrives by ~2·box
    //    (F1: history is fully rejected by ~3.2σ, not ~7.2σ).
    float2 motionPx = motion * texSize;
    float motionMag = length(motionPx);
    float still = saturate(1.0 - motionMag * 0.75);                      // 0 once the pixel moves ~1.3px
    float3 vdiff = abs(currRaw - history);
    float chromaDisagree = (vdiff.y + vdiff.z) / max(max(currRaw.x, history.x), 0.2);
    float lumaDiffPre = abs(currRaw.x - history.x) / max(max(currRaw.x, history.x), 0.2);
    float confidence = saturate(1.0 - max(lumaDiffPre, chromaDisagree)) * still;
    float lumaGamma = lerp(0.9, 1.6, confidence);
    float chromaGamma = 0.9;                                             // chroma box never widens

    // C1 — SOFT colour clamp (kajiya inc/soft_color_clamp), replacing the hard YCoCg clip. The hard clip snaps the
    // moment maxUnit crosses 1; the soft form pulls history toward the box as it gets statistically far (1→2 unit
    // ramp), so ghosting bleeds out smoothly without the hard-knee shimmer. Direction-preserving.
    float3 extents = float3(lumaGamma, chromaGamma, chromaGamma) * sigma + 1e-5;
    float3 delta = history - mean;
    float maxUnit = max(abs(delta.x / extents.x), max(abs(delta.y / extents.y), abs(delta.z / extents.z)));
    float3 clipped = mean + delta / max(maxUnit, 1.0);                    // the hard-clip target
    history = lerp(history, clipped, smoothstep(1.0, 2.0, maxUnit));      // soft ramp into it

    // Luma-adaptive feedback: agreement keeps full history; disagreement drops it (re-converge fast).
    float lumaDiff = abs(currYCoCg.x - history.x) / max(max(currYCoCg.x, history.x), 0.2);
    float agreement = 1.0 - lumaDiff;
    float feedback = lerp(0.5, clamp(Feedback, 0.0, 0.97), saturate(agreement * agreement));

    float3 blended = YCoCgToRGB(lerp(currYCoCg, history, feedback));
    return float4(max(blended, 0.0.xxx), 1.0);
}
