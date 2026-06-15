// Temporal anti-aliasing for the DX12 deferred renderer, ported from the GL TAA_Frag. Reprojects last
// frame's accumulated image with the camera matrices (depth-based, camera-motion reprojection) and blends
// it with the jittered current frame. YCoCg variance clipping + Catmull-Rom history resample + luma-
// adaptive feedback (same quality features as the GL path). Operates on the HDR scene color before tonemap.
//
// Jitter is applied to the camera PROJECTION on the CPU (the whole frame is jittered); the matrices here
// are UNJITTERED (reprojection must use stable matrices). Volume-driven feedback (PostFX.TaaFeedback).

cbuffer TaaConstants : register(b0) {
    float4x4 CurrInvViewProj;  // current frame, UNJITTERED (transposed)
    float4x4 PrevViewProj;     // previous frame, UNJITTERED (transposed)
    float    Feedback;         // history weight (0..0.97)
    float    ValidHistory;     // >0.5 = blend, else passthrough (first frame / camera cut)
    float2   TexelSize;        // 1 / render size
};

Texture2D CurrentTex : register(t0);
Texture2D HistoryTex : register(t1);
Texture2D DepthTex   : register(t2);
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

    // Reproject this pixel into last frame's screen space (DX NDC: z in [0,1], y flip).
    float depth = DepthTex.SampleLevel(LinearClamp, i.Uv, 0).r;
    float4 ndc = float4(i.Uv.x * 2.0 - 1.0, (1.0 - i.Uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 world = mul(ndc, CurrInvViewProj); world /= world.w;
    float4 prevClip = mul(world, PrevViewProj);
    float2 prevUV = prevClip.xy / prevClip.w;
    prevUV = float2(prevUV.x * 0.5 + 0.5, 0.5 - prevUV.y * 0.5);
    if (prevUV.x < 0.0 || prevUV.x > 1.0 || prevUV.y < 0.0 || prevUV.y > 1.0 || prevClip.w <= 0.0)
        return float4(current, 1.0);

    float2 texSize = 1.0 / TexelSize;
    float3 history = RGBToYCoCg(Sanitize(SampleHistoryCatmullRom(prevUV, texSize)));

    // 3x3 neighborhood moments in YCoCg.
    float3 m1 = 0.0.xxx, m2 = 0.0.xxx;
    [unroll] for (int x = -1; x <= 1; x++)
    [unroll] for (int y = -1; y <= 1; y++) {
        float3 c = RGBToYCoCg(Sanitize(CurrentTex.SampleLevel(LinearClamp, i.Uv + float2(x, y) * TexelSize, 0).rgb));
        m1 += c; m2 += c * c;
    }
    float3 mean = m1 / 9.0;
    float3 sigma = sqrt(max(m2 / 9.0 - mean * mean, 0.0.xxx));

    // Clip (not clamp) the history toward the neighborhood mean (preserves color direction).
    const float Gamma = 1.0;
    float3 extents = Gamma * sigma + 1e-5;
    float3 delta = history - mean;
    float maxUnit = max(abs(delta.x / extents.x), max(abs(delta.y / extents.y), abs(delta.z / extents.z)));
    if (maxUnit > 1.0) history = mean + delta / maxUnit;

    // Luma-adaptive feedback: agreement keeps full history; disagreement drops it (re-converge fast).
    float3 currYCoCg = RGBToYCoCg(current);
    float lumaDiff = abs(currYCoCg.x - history.x) / max(max(currYCoCg.x, history.x), 0.2);
    float agreement = 1.0 - lumaDiff;
    float feedback = lerp(0.5, clamp(Feedback, 0.0, 0.97), saturate(agreement * agreement));

    float3 blended = YCoCgToRGB(lerp(currYCoCg, history, feedback));
    return float4(max(blended, 0.0.xxx), 1.0);
}
