// Lumen — SVGF spatio-temporal denoiser (Schied et al. 2017, "Spatiotemporal Variance-Guided Filtering").
//
// REPLACES the old LumenTemporal.CSTemporal (rate-limit + loose AABB clamp + [0,1]-depth soft trust) and the
// fixed-kernel LumenGi.CSDenoise. Those could not converge in dark areas (proven by analysis):
//   - rate-limit step floor 1e-4 was BELOW the dark noise floor 1e-3 → random-walked, never settled;
//   - depthAgree used NON-LINEAR [0,1] depth → sub-pixel reproject kept trust<1 on every gradient → 20-27% fresh
//     noise injected every static frame;
//   - the AABB clamp band was ~10x the signal in high-variance dark regions → stale history leaked as blobs.
//
// SVGF fixes all three structurally:
//   1) CSSvgfTemporal: per-pixel 1/N adaptive-alpha EMA + luminance moment tracking → variance estimate. Binary
//      disocclusion test in WORLD-space distance + normal. On reset, bootstrap variance SPATIALLY so the reset
//      pixel gets HEAVY a-trous blur instead of raw single-sample noise.
//   2) CSSvgfAtrous: 5 iterations of the variance-guided a-trous wavelet. The luminance edge-stop weight uses the
//      (spatially pre-filtered) variance, so it blurs HARD where noisy (dark) and preserves edges where clean.
//      Iteration 0's output feeds back as next frame's history (the SVGF feedback detail).

// ===================== Shared bindings / CB =====================
cbuffer SvgfConstants : register(b0) {
    float4x4 InvViewProj;     // world pos reconstruction from [0,1] depth (HLSL column-major: mul(clip, InvViewProj))
    float3 CameraPos;         float Pad0;
    float2 Texel;             // 1 / E-res
    uint   W, H;
    float  HistoryValid;      // 0 -> first frame / resize: reset all pixels (N=1, variance=spatial bootstrap)
    float  AlphaMin;          // color EMA floor (0.05) -> converged history weight = 1-AlphaMin
    float  AlphaMinMoments;   // moments EMA floor (0.2)
    float  NMax;              // history-length cap (32)
    float  TauZ;              // world-distance relative disocclusion tolerance (0.05)
    float  EpsZ;              // world-distance absolute floor, metres (0.05)
    float  CosTauN;           // normal agreement cos threshold (cos 25 deg = 0.906)
    float  StepSize;          // a-trous hole size this iteration (1,2,4,8,16) - atrous pass only
    float  SigmaL;            // luminance edge-stop sigma (4.0) - atrous pass only
    float  SigmaN;            // normal edge-stop power (64) - atrous pass only
    float  Pad1, Pad2;
};
SamplerState LinearClamp : register(s0);

static const float3 LUMW = float3(0.2126, 0.7152, 0.0722);
float3 San(float3 v) { return float3(isnan(v.x)||isinf(v.x)?0:v.x, isnan(v.y)||isinf(v.y)?0:v.y, isnan(v.z)||isinf(v.z)?0:v.z); }
float  Lum(float3 c) { return dot(c, LUMW); }

// World position from uv + [0,1] depth. The SVGF disocclusion key is the camera distance of this world point —
// scale-invariant under a relative tolerance, and (unlike raw [0,1] depth) it has uniform precision in metres.
float3 WorldFromUvDepth(float2 uv, float rawDepth) {
    float4 clip = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, rawDepth, 1.0);
    float4 wp = mul(clip, InvViewProj);
    return wp.xyz / wp.w;
}
float CamDist(float3 worldPos) { return distance(worldPos, CameraPos); }
float3 UnpackNormal(float4 n) { return n.rgb * 2.0 - 1.0; }

// ===================== CSSvgfTemporal =====================
// Reads fresh E + previous history (color+moments) + G-buffer (depth, normal, motion). Writes accumulated color,
// the variance, and the updated moments/history-length. Disocclusion -> reset with spatial variance bootstrap.
Texture2D<float4>   InE         : register(t0);   // fresh single-frame E (rgb), depth in .a from the integrate
Texture2D<float4>   HistColor   : register(t1);   // prev: .rgb accumulated color C (.a is the à-trous-clobbered 1.0 — UNUSED)
Texture2D<float4>   HistMoments : register(t2);   // prev: .r=m2, .g=N, .b=prevCamDist, .a=m1 (luminance moment 1) — see BUG#2 fix
Texture2D<float>    Depth       : register(t3);   // current full-res depth [0,1]
Texture2D<float4>   Normal      : register(t4);   // current world normal (packed N*0.5+0.5)
Texture2D<float2>   Motion      : register(t5);   // RT4 = prevUV - currUV (unjittered)
RWTexture2D<float4> OutColor    : register(u0);   // .rgb accumulated color (.a unused — the à-trous overwrites it)
RWTexture2D<float4> OutMoments  : register(u1);   // .r=m2, .g=N, .b=camDist, .a=m1 (the moment store SURVIVES — never touched by à-trous)
RWTexture2D<float4> OutVariance : register(u2);   // .r = scalar variance handed straight to the à-trous VarIn (RGBA16F.r)

// Spatial variance bootstrap (SVGF 4.2): when history is missing/young, estimate variance from a 7x7 bilateral
// neighbourhood of the CURRENT luminance so the reset pixel is heavily blurred, not shown as raw noise.
float SpatialVarianceBootstrap(uint2 px, float2 uv, float zc, float3 nc) {
    float m1 = 0, m2 = 0, wsum = 0;
    [loop] for (int dy = -3; dy <= 3; dy++)
    [loop] for (int dx = -3; dx <= 3; dx++) {
        int2 q = int2(px) + int2(dx, dy);
        if (q.x < 0 || q.y < 0 || q.x >= (int)W || q.y >= (int)H) continue;
        float2 quv = (float2(q) + 0.5) * Texel;
        float dq = Depth.SampleLevel(LinearClamp, quv, 0).r;
        if (dq >= 1.0) continue;
        float3 nq = UnpackNormal(Normal.SampleLevel(LinearClamp, quv, 0));
        float zq = CamDist(WorldFromUvDepth(quv, dq));
        float wz = exp(-abs(zq - zc) / max(TauZ * zc + EpsZ, 1e-4));
        float wn = pow(saturate(dot(nq, nc)), SigmaN);
        float w = wz * wn + 1e-4;
        float l = Lum(San(InE[q].rgb));
        m1 += l * w; m2 += l * l * w; wsum += w;
    }
    if (wsum < 1e-5) return 0.0;
    m1 /= wsum; m2 /= wsum;
    return max(m2 - m1 * m1, 0.0);
}

[numthreads(8, 8, 1)]
void CSSvgfTemporal(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    if (px.x >= W || px.y >= H) return;
    float2 uv = (float2(px) + 0.5) * Texel;

    float3 E = San(InE[px].rgb);
    float depth = Depth.SampleLevel(LinearClamp, uv, 0).r;
    float lum = Lum(E);

    // Sky / invalid → passthrough, mark reset (N=1) so it never feeds a stale history. Moments .a = m1 = lum.
    if (depth >= 1.0) {
        OutColor[px]   = float4(E, 0.0);
        OutMoments[px] = float4(lum * lum, 1.0, 1e6, lum);
        OutVariance[px] = float4(0, 0, 0, 0);
        return;
    }
    float3 nc = UnpackNormal(Normal.SampleLevel(LinearClamp, uv, 0));
    if (dot(nc, nc) < 0.1) { OutColor[px] = float4(E, 0.0); OutMoments[px] = float4(lum*lum, 1.0, 1e6, lum); OutVariance[px] = float4(0,0,0,0); return; }
    nc = normalize(nc);
    float zc = CamDist(WorldFromUvDepth(uv, depth));

    // ---- Reproject with the real motion vector ----
    bool consistent = false;
    float3 Cprev = 0; float m1prev = 0, m2prev = 0, Nprev = 0;
    [branch] if (HistoryValid > 0.5) {
        float2 motion = Motion.SampleLevel(LinearClamp, uv, 0).rg;
        float2 prevUv = uv + motion;
        if (all(prevUv >= 0.0) && all(prevUv <= 1.0)) {
            // Disocclusion test in WORLD distance (relative) + normal. prevCamDist is stored in HistMoments.b.
            float4 hc = HistColor.SampleLevel(LinearClamp, prevUv, 0);
            float4 hm = HistMoments.SampleLevel(LinearClamp, prevUv, 0);
            float zPrev = hm.b;
            bool acceptZ = abs(zc - zPrev) < (TauZ * zc + EpsZ);
            // Normal stored implicitly: we don't keep prev normal; use the depth/world test + the variance-guided
            // a-trous to catch silhouettes. (A prev-normal channel can be added later if needed.)
            if (acceptZ) {
                consistent = true;
                // BUG#2 fix: m1 is read from the MOMENTS buffer (hm.a), NOT the color buffer (hc.a). The color
                // buffer's .a is clobbered to 1.0 by the à-trous feedback, so reading m1 from there collapsed the
                // variance estimate (m2-m1² → negative → 0 → à-trous stopped denoising). Color comes from hc.rgb.
                Cprev = San(hc.rgb); m1prev = hm.a; m2prev = hm.r; Nprev = hm.g;
            }
        }
    }

    float N, variance;
    float3 C; float m1, m2;
    if (consistent) {
        N = min(Nprev + 1.0, NMax);
        float alpha  = max(1.0 / N, AlphaMin);
        float alphaM = max(1.0 / N, AlphaMinMoments);
        C  = lerp(Cprev,  E,       alpha);
        m1 = lerp(m1prev, lum,     alphaM);
        m2 = lerp(m2prev, lum*lum, alphaM);
        // Temporal variance once history is long enough; bootstrap spatially while young (N<4).
        float tvar = max(m2 - m1 * m1, 0.0);
        variance = (N >= 4.0) ? tvar : max(tvar, SpatialVarianceBootstrap(px, uv, zc, nc));
    } else {
        // Disocclusion / first frame: take fresh, seed moments, bootstrap variance spatially → heavy blur, no raw noise.
        N = 1.0; C = E; m1 = lum; m2 = lum * lum;
        variance = SpatialVarianceBootstrap(px, uv, zc, nc);
    }

    // BUG#2 fix: m1 now lives in the MOMENTS buffer's .a (it survives — à-trous never touches svgfMoments). The
    // colour buffer's .a is free (the à-trous overwrites it with 1.0; harmless). Variance goes to its own buffer.
    OutColor[px]    = float4(San(C), 0.0);
    OutMoments[px]  = float4(m2, N, zc, m1);
    OutVariance[px] = float4(max(variance, 0.0), 0, 0, 0);
}

// ===================== CSSvgfAtrous =====================
// One iteration of the variance-guided a-trous wavelet. 5x5 B3-spline kernel at hole size StepSize. Edge-stops:
//   w_z (world-distance relative), w_n (dot^SigmaN), w_l (luminance / (SigmaL*sqrt(prefiltered variance))).
// Variance is carried in AtrColorIn.a's sibling AtrVarIn; filtered with SQUARED weights. The 3x3 variance pre-blur
// (SVGF 4.3) drives w_l so single-sample variance noise doesn't blotch the denoise.
Texture2D<float4>   AtrColorIn  : register(t0);   // .rgb color, .a unused
Texture2D<float4>   AtrVarIn    : register(t1);   // .r = variance (RGBA16F.r)
Texture2D<float>    AtrDepth    : register(t2);   // current depth [0,1]
Texture2D<float4>   AtrNormal   : register(t3);   // current world normal (packed)
RWTexture2D<float4> AtrColorOut : register(u0);   // .rgb color, .a unused
RWTexture2D<float4> AtrVarOut   : register(u1);   // .r = filtered variance

static const float KERN[5] = { 1.0/16.0, 4.0/16.0, 6.0/16.0, 4.0/16.0, 1.0/16.0 };

// 3x3 Gaussian pre-blur of the variance at p (drives the luminance edge-stop only).
float GaussVar(uint2 px) {
    static const float g[3] = { 0.25, 0.5, 0.25 };   // separable [1,2,1]/4
    float v = 0, wsum = 0;
    [unroll] for (int dy = -1; dy <= 1; dy++)
    [unroll] for (int dx = -1; dx <= 1; dx++) {
        int2 q = clamp(int2(px) + int2(dx, dy), int2(0,0), int2(W-1, H-1));
        float w = g[dx+1] * g[dy+1];
        v += AtrVarIn[q].r * w; wsum += w;
    }
    return v / max(wsum, 1e-5);
}

[numthreads(8, 8, 1)]
void CSSvgfAtrous(uint3 dtid : SV_DispatchThreadID) {
    uint2 px = dtid.xy;
    if (px.x >= W || px.y >= H) return;
    float2 uv = (float2(px) + 0.5) * Texel;
    float depthC = AtrDepth.SampleLevel(LinearClamp, uv, 0).r;
    float3 colC = San(AtrColorIn[px].rgb);
    float varC  = AtrVarIn[px].r;
    if (depthC >= 1.0) { AtrColorOut[px] = float4(colC, 1.0); AtrVarOut[px] = float4(varC,0,0,0); return; }

    float3 nC = UnpackNormal(AtrNormal.SampleLevel(LinearClamp, uv, 0));
    if (dot(nC, nC) < 0.1) { AtrColorOut[px] = float4(colC, 1.0); AtrVarOut[px] = float4(varC,0,0,0); return; }
    nC = normalize(nC);
    float zC = CamDist(WorldFromUvDepth(uv, depthC));
    float lumC = Lum(colC);
    // Luminance edge-stop denominator (variance-guided). The +1e-4 absolute floor was ~10x too small for HDR
    // irradiance: in a converged dark region GaussVar≈0 → sigLum≈1e-4 → wl=exp(-|dL|/1e-4) is a NEAR-BINARY edge
    // stop, so a single firefly pixel (|dL|>1e-3) is NEVER blurred into its dark neighbours and survives every
    // à-trous iteration as an isolated dot. A RELATIVE floor (fraction of the local luminance) keeps the edge-stop
    // meaningful at the scene's actual radiance scale so hot pixels can be pulled down. Pad2 carries the fraction
    // (set by C#; 0 → legacy 1e-4 absolute floor, byte-identical).
    float lumFloor = (Pad2 > 0.0) ? max(Pad2 * lumC, 1e-4) : 1e-4;
    float sigLum = SigmaL * sqrt(max(GaussVar(px), 0.0)) + lumFloor;

    int step = (int)StepSize;
    float3 sumC = 0; float sumW = 0, sumVar = 0;
    [unroll] for (int dy = -2; dy <= 2; dy++)
    [unroll] for (int dx = -2; dx <= 2; dx++) {
        int2 q = int2(px) + int2(dx, dy) * step;
        if (q.x < 0 || q.y < 0 || q.x >= (int)W || q.y >= (int)H) continue;
        float2 quv = (float2(q) + 0.5) * Texel;
        float dq = AtrDepth.SampleLevel(LinearClamp, quv, 0).r;
        if (dq >= 1.0) continue;
        float3 nq = UnpackNormal(AtrNormal.SampleLevel(LinearClamp, quv, 0));
        if (dot(nq, nq) < 0.1) continue;
        nq = normalize(nq);
        float3 colQ = San(AtrColorIn[q].rgb);
        float zq = CamDist(WorldFromUvDepth(quv, dq));

        float kw = KERN[dx + 2] * KERN[dy + 2];
        float wz = exp(-abs(zq - zC) / max(TauZ * zC + EpsZ, 1e-4));
        float wn = pow(saturate(dot(nq, nC)), SigmaN);
        float wl = exp(-abs(Lum(colQ) - lumC) / sigLum);
        float w = kw * wz * wn * wl;

        sumC   += colQ * w;
        sumW   += w;
        sumVar += w * w * AtrVarIn[q].r;   // variance filters with SQUARED weights
    }
    if (sumW < 1e-5) { AtrColorOut[px] = float4(colC, 1.0); AtrVarOut[px] = float4(varC,0,0,0); return; }
    AtrColorOut[px] = float4(San(sumC / sumW), 1.0);
    AtrVarOut[px]   = float4(sumVar / (sumW * sumW), 0, 0, 0);
}
