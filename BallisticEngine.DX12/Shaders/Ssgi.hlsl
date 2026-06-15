// Screen-space global illumination for the DX12 deferred renderer — horizon-slice sector-bitmask gather
// (SSILVB, the technique behind the GL SSGI_Frag.glsl), plus a composite that adds the one-bounce light
// into the lit HDR scene. Ported faithfully from the GL path with DX conventions (depth z in [0,1], UV
// y-flip on reconstruction, System.Numerics matrices uploaded transposed → mul(rowVec, M)).
//
// Reads the G-buffer (depth R32F + world-normal RT1 [0,1]-packed) + the lit HDR scene color (the bounce
// source). Gather runs HALF-res; combine runs full-res and upsamples by the linear sampler. Temporal
// accumulation (via the motion buffer) + OIDN denoise are wired in the renderer around these passes.

cbuffer SsgiConstants : register(b0) {
    float4x4 Projection;     // jittered, transposed
    float4x4 InvProjection;  // jittered, transposed
    float4x4 ViewMatrix;     // transposed (world dir -> view)
    float4 Params0;  // x=RayLength y=Falloff z=Thickness w=MultiBounce
    float4 Params1;  // x=BounceBoost y=RayCount z=FrameIndex w=(unused)
    float4 Params2;  // xy = gather texel size (1/halfRes), z = preExposure, w = 1/preExposure
    float4 Combine0; // x=Intensity y=Look z=Saturation w=OcclusionPower
    float4 Tint;     // xyz = bounce tint
    float4 Params3;  // x=HasHistory y=MaxHistory z/w=(unused) — temporal
};

Texture2D ColorTex  : register(t0);   // gather: lit HDR scene  | combine: scene
Texture2D DepthTex   : register(t1);  // gather: depth          | combine: ssgi GI
Texture2D NormalTex : register(t2);   // gather: world normal   | combine: unused
SamplerState LinearClamp : register(s0);

static const int MAX_SLICES = 8;
static const int STEPS = 8;
static const int SECTORS = 32;
static const float PI = 3.14159265359;
static const float HALF_PI = 1.57079632679;
static const float FIREFLY_KNEE = 6.0;

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float3 Sanitize(float3 v) {
    return float3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

// View-space position from a depth sample (DX: NDC z = depth in [0,1], y flipped on reconstruct).
float3 ViewPosFromDepth(float2 uv, float depth) {
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 view = mul(ndc, InvProjection);
    return view.xyz / view.w;
}

float Hash(float2 p) { return frac(sin(dot(p, float2(12.9898, 78.233))) * 43758.5453); }

// Set the bitmask sectors covered by the angular interval [a0,a1] over the [-PI/2,+PI/2] hemisphere arc.
uint OccludeSectors(float a0, float a1) {
    float lo = saturate((min(a0, a1) + HALF_PI) / PI);
    float hi = saturate((max(a0, a1) + HALF_PI) / PI);
    int b0 = int(lo * float(SECTORS));
    int count = clamp(int(ceil(hi * float(SECTORS))) - b0, 0, SECTORS);
    if (count <= 0) return 0u;
    uint mask = count >= 32 ? 0xFFFFFFFFu : ((1u << uint(count)) - 1u);
    return mask << uint(b0);
}

float4 PSGather(VSOut input) : SV_Target {
    float2 uv0 = input.Uv;
    float RayLength = Params0.x, Falloff = Params0.y, Thickness = Params0.z, MultiBounce = Params0.w;
    float BounceBoost = Params1.x;
    int RayCount = (int)Params1.y;
    int FrameIndex = (int)Params1.z;

    float depth = DepthTex.SampleLevel(LinearClamp, uv0, 0).r;
    float3 worldN = NormalTex.SampleLevel(LinearClamp, uv0, 0).rgb * 2.0 - 1.0;
    if (depth >= 1.0 || dot(worldN, worldN) < 0.1)
        return float4(0, 0, 0, 0);   // sky / un-shaded: receives no bounce

    float3 P = ViewPosFromDepth(uv0, depth);
    float3 N = normalize(mul(float4(worldN, 0.0), ViewMatrix).xyz);
    float3 V = -normalize(P);

    float rayLength = max(RayLength, 0.1);
    float2 uvRadius = min(rayLength * 0.5 * float2(Projection[0][0], Projection[1][1]) / max(-P.z, 0.05), float2(0.5, 0.5));

    float2 screen = 1.0 / max(Params2.xy, 1e-6);
    float noise = Hash(uv0 * screen + float(FrameIndex) * 1.618);
    float stepNoise = Hash(uv0 * 911.0 + float(FrameIndex) * 2.71);

    int slices = clamp(RayCount, 1, MAX_SLICES);
    float3 bounce = 0.0.xxx;

    [loop] for (int i = 0; i < MAX_SLICES; i++) {
        if (i >= slices) break;
        float phi = PI * (float(i) + noise) / float(slices);
        float2 dir2 = float2(cos(phi), sin(phi));
        float3 sliceDir = float3(dir2, 0.0);
        float3 T = normalize(sliceDir - V * dot(sliceDir, V));
        uint bits = 0u;

        [loop] for (int j = 0; j < 2; j++) {
            float side = j == 0 ? 1.0 : -1.0;
            [loop] for (int s = 1; s <= STEPS; s++) {
                float t = (float(s) - 0.5 + (stepNoise - 0.5)) / float(STEPS);
                t = t * t;
                float2 uv = uv0 + side * dir2 * (t * uvRadius);
                if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) break;

                float sd = DepthTex.SampleLevel(LinearClamp, uv, 0).r;
                if (sd >= 1.0) continue;

                float3 S = ViewPosFromDepth(uv, sd);
                float3 delta = S - P;
                float dist = length(delta);
                if (dist < 1e-4) continue;
                float3 w = delta / dist;

                float3 deltaBack = delta + normalize(S) * max(Thickness, 0.01);
                float aFront = atan2(dot(delta, T), dot(delta, V));
                float aBack = atan2(dot(deltaBack, T), dot(deltaBack, V));

                uint sampleBits = OccludeSectors(aFront, aBack) & ~bits;
                if (sampleBits != 0u) {
                    float newFrac = float(countbits(sampleBits)) / float(SECTORS);
                    float cosW = saturate(dot(N, w));
                    if (cosW > 0.0) {
                        float fade = pow(saturate(1.0 - dist / rayLength), max(Falloff, 0.0));
                        // DX12 keeps RAW HDR scene radiance (~1e5); pre-expose to the GL-style viewable
                        // range so FIREFLY_KNEE + the gather magnitude match the ported tuning. The combine
                        // converts the resulting bounce back to raw HDR before adding it to the scene.
                        float3 radiance = Sanitize(ColorTex.SampleLevel(LinearClamp, uv, 0).rgb) * Params2.z;
                        // (multi-bounce history fed in step C; MultiBounce=0 here folds it out.)
                        radiance *= 1.0 + BounceBoost * dot(radiance, 0.333.xxx);
                        float lum = dot(radiance, float3(0.2126, 0.7152, 0.0722));
                        if (lum > FIREFLY_KNEE) radiance *= FIREFLY_KNEE / lum;
                        bounce += radiance * (newFrac * 2.0) * cosW * fade;
                    }
                    bits |= sampleBits;
                }
                if (bits == 0xFFFFFFFFu) break;
            }
            if (bits == 0xFFFFFFFFu) break;
        }
    }

    bounce /= float(slices);

    float2 edge = min(uv0, 1.0 - uv0);
    float edgeFade = smoothstep(0.0, 0.06, min(edge.x, edge.y));
    return float4(Sanitize(bounce), edgeFade);
}

// Temporal accumulation: the biggest noise win. Reproject last frame's accumulated GI via the MOTION
// buffer (prevUV = uv + motion — jitter-free, tracks dynamic geometry) and EMA-blend with this frame's
// raw gather. Neighborhood clamp + pre/post firefly clamps keep the history from holding outliers. Ported
// from GL SSGI_Temporal (depth+matrix reprojection swapped for the motion buffer). For this pass:
// ColorTex(t0)=currentGI, DepthTex(t1)=historyGI (rgb + history length in a), NormalTex(t2)=motion (rg).
float4 PSTemporal(VSOut input) : SV_Target {
    float2 uv = input.Uv;
    float2 texel = Params2.xy;
    float hasHistory = Params3.x, maxHistory = max(Params3.y, 1.0);

    float3 current = Sanitize(ColorTex.SampleLevel(LinearClamp, uv, 0).rgb);

    // Pre-EMA firefly clamp: rescale a lone bright pixel's luma toward its 3x3 neighbourhood mean.
    {
        float3 nb = 0.0.xxx;
        nb += Sanitize(ColorTex.SampleLevel(LinearClamp, uv + float2(-1,-1) * texel, 0).rgb);
        nb += Sanitize(ColorTex.SampleLevel(LinearClamp, uv + float2( 0,-1) * texel, 0).rgb);
        nb += Sanitize(ColorTex.SampleLevel(LinearClamp, uv + float2( 1,-1) * texel, 0).rgb);
        nb += Sanitize(ColorTex.SampleLevel(LinearClamp, uv + float2(-1, 0) * texel, 0).rgb);
        nb += Sanitize(ColorTex.SampleLevel(LinearClamp, uv + float2( 1, 0) * texel, 0).rgb);
        nb += Sanitize(ColorTex.SampleLevel(LinearClamp, uv + float2(-1, 1) * texel, 0).rgb);
        nb += Sanitize(ColorTex.SampleLevel(LinearClamp, uv + float2( 0, 1) * texel, 0).rgb);
        nb += Sanitize(ColorTex.SampleLevel(LinearClamp, uv + float2( 1, 1) * texel, 0).rgb);
        float nbLuma = dot(nb / 8.0, float3(0.2126, 0.7152, 0.0722));
        float curLuma = dot(current, float3(0.2126, 0.7152, 0.0722));
        float maxLuma = nbLuma * 4.0 + 0.02;
        if (curLuma > maxLuma) current *= maxLuma / max(curLuma, 1e-4);
    }

    float2 motion = NormalTex.SampleLevel(LinearClamp, uv, 0).rg;   // prevUV - currUV
    float2 prevUV = uv + motion;
    bool valid = hasHistory > 0.5 && prevUV.x >= 0.0 && prevUV.x <= 1.0 && prevUV.y >= 0.0 && prevUV.y <= 1.0;
    if (!valid)
        return float4(current, 1.0);

    float4 history = DepthTex.SampleLevel(LinearClamp, prevUV, 0);
    history.rgb = Sanitize(history.rgb);

    // Loosened neighbourhood clamp (3x box + epsilon floor) so history rides through miss-frames.
    float3 lo = current, hi = current;
    [unroll] for (int x = -1; x <= 1; x++)
    [unroll] for (int y = -1; y <= 1; y++) {
        float3 c = Sanitize(ColorTex.SampleLevel(LinearClamp, uv + float2(x, y) * texel, 0).rgb);
        lo = min(lo, c); hi = max(hi, c);
    }
    float3 boxCenter = (lo + hi) * 0.5;
    float3 boxExtent = (hi - lo) * 0.5 * 3.0 + 0.03.xxx;
    lo = max(boxCenter - boxExtent, 0.0.xxx);
    hi = boxCenter + boxExtent;
    float3 clampedHistory = clamp(history.rgb, lo, hi);

    float boxSize = max(length(hi - lo), 0.04);
    float drift = length(history.rgb - clampedHistory) / boxSize;
    float reset = smoothstep(1.5, 4.0, drift);
    float histLen = (isnan(history.a) || isinf(history.a)) ? 1.0 : history.a;
    histLen = lerp(histLen, 1.0, reset);
    histLen = min(histLen + 1.0, maxHistory);

    float alpha = 1.0 / histLen;
    float3 accumulated = lerp(clampedHistory, current, alpha);

    // Post-EMA firefly cap: keep the accumulated luma within a small multiple of the local box max.
    float accLuma = dot(accumulated, float3(0.2126, 0.7152, 0.0722));
    float capLuma = dot(hi, float3(0.2126, 0.7152, 0.0722)) * 1.5 + 0.05;
    if (accLuma > capLuma) accumulated *= capLuma / max(accLuma, 1e-4);

    return float4(accumulated, histLen);
}

// Composite: add the (denoised) one-bounce GI on top of the IBL-lit scene. Bounded refinement — energy-
// gated saturation/warmth so a noisy near-black pixel stays neutral. DepthTex slot = the GI texture here.
static const float3 LUMA = float3(0.2126, 0.7152, 0.0722);
static const float3 WARM = float3(1.05, 1.00, 0.92);

float4 PSCombine(VSOut input) : SV_Target {
    float Intensity = Combine0.x, Look = saturate(Combine0.y), Saturation = Combine0.z, OcclusionPower = Combine0.w;
    float3 scene = ColorTex.SampleLevel(LinearClamp, input.Uv, 0).rgb;
    float3 gi = Sanitize(DepthTex.SampleLevel(LinearClamp, input.Uv, 0).rgb);   // DepthTex slot = GI
    gi = max(gi, 0.0.xxx);
    float edgeFade = DepthTex.SampleLevel(LinearClamp, input.Uv, 0).a;

    float giLuma = dot(gi, LUMA);
    float energy = smoothstep(0.02, 0.25, giLuma);
    float sat = lerp(1.0, Saturation * (1.0 + 0.3 * Look), energy);
    gi = lerp(giLuma.xxx, gi, clamp(sat, 0.0, 2.0));
    gi = lerp(gi, gi * WARM, energy * Look * 0.4);
    gi *= Tint.xyz * Intensity;
    // Bounce is in pre-exposed (viewable) units; convert back to raw HDR before adding to the raw scene.
    float3 add = clamp(gi, 0.0, 8.0) * edgeFade * Params2.w;
    return float4(scene + add, 1.0);
}
