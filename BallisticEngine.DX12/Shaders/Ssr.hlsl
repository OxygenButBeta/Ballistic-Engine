// Screen-space reflections for the DX12 deferred renderer, ported from the GL SSR_Frag/SSR_Combine.
// Reads the lit HDR scene color + the G-buffer (depth, world normal, metallic/roughness) and marches the
// reflection ray in view space; where it hits, the scene color replaces the (sky-IBL) reflection. Two
// passes: PSMarch (half-res → rgb reflection + a strength) and PSCombine (depth-aware upsample + lerp into
// the full-res HDR color). Intensity/enable come from the ScreenSpaceReflections VOLUME override.
//
// CONVENTIONS (locked): row-major System.Numerics → transposed on upload; DX NDC z in [0,1].

cbuffer SsrConstants : register(b0) {
    float4x4 Projection;     // unjittered camera projection (transposed)
    float4x4 InvProjection;  // its inverse (transposed)
    float4x4 ViewMatrix;     // world → view (transposed) — rotate the G-buffer world normal to view space
    float    Intensity;      // SsrIntensity (volume)
    float3   Pad;
    float2   TexelSize;      // 1 / SSR-buffer size (half-res)
    float2   Pad2;
};

Texture2D ColorTex    : register(t0);   // lit HDR scene color (full-res)
Texture2D DepthTex    : register(t1);   // G-buffer depth (R32_Float)
Texture2D NormalTex   : register(t2);   // G-buffer world normal (packed [0,1])
Texture2D MaterialTex : register(t3);   // G-buffer metallic/roughness/ao/flags
Texture2D SsrTex      : register(t4);   // half-res SSR result (combine pass)
SamplerState LinearClamp : register(s0);

static const int MARCH_STEPS = 32;
static const int REFINE_STEPS = 5;
static const float MAX_DISTANCE = 60.0;
static const float MAX_ROUGHNESS = 0.6;

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

float3 ViewPos(float2 uv) {
    float depth = DepthTex.SampleLevel(LinearClamp, uv, 0).r;
    // DX NDC: xy [-1,1] (y flipped), z = depth [0,1].
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 v = mul(ndc, InvProjection);
    return v.xyz / v.w;
}

float2 ToUV(float3 viewPos, out float w) {
    float4 clip = mul(float4(viewPos, 1.0), Projection);
    w = clip.w;
    float2 uv = clip.xy / clip.w;
    return float2(uv.x * 0.5 + 0.5, 0.5 - uv.y * 0.5);   // clip → uv (y flip)
}

// --- March pass: half-res, outputs rgb reflection + a strength. ---
float4 PSMarch(VSOut i) : SV_Target {
    float depth = DepthTex.SampleLevel(LinearClamp, i.Uv, 0).r;
    if (depth >= 1.0) return 0.0.xxxx;          // sky
    float4 mat = MaterialTex.SampleLevel(LinearClamp, i.Uv, 0);
    float metallic = mat.r;
    float roughness = mat.g;
    float3 worldN = NormalTex.SampleLevel(LinearClamp, i.Uv, 0).rgb * 2.0 - 1.0;
    if (dot(worldN, worldN) < 0.1 || roughness > MAX_ROUGHNESS) return 0.0.xxxx;

    float3 P = ViewPos(i.Uv);
    float3 N = normalize(mul(float4(worldN, 0.0), ViewMatrix).xyz);
    float3 Vdir = normalize(P);                 // camera → point (view space, eye at origin)
    float3 R = normalize(reflect(Vdir, N));

    float stepLength = MAX_DISTANCE / (float)MARCH_STEPS;
    float3 rayPos = P + N * 0.05;
    float3 prevPos = rayPos;
    float hit = 0.0;
    float2 hitUV = 0.0.xx;

    // STABILITY: scale the depth-compare bias by the surface view depth. A fixed +0.01 is too tight for
    // distant/thin geometry (the refine bisection lands on a different sub-texel each frame → the hit UV
    // jitters); a depth-proportional bias keeps the comparison consistent across distance, so the resolved
    // hit point is stable frame to frame on thin/grazing surfaces.
    float zBias = max(0.01, abs(P.z) * 0.0025);

    [loop] for (int s = 0; s < MARCH_STEPS; s++) {
        prevPos = rayPos;
        rayPos += R * stepLength;
        float w;
        float2 uv = ToUV(rayPos, w);
        if (w <= 0.0 || uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) break;
        float sceneZ = ViewPos(uv).z;
        float thickness = stepLength * 2.0 + 0.3;
        // View Z is negative ahead; a hit is where the scene surface is in front of the ray within thickness.
        if (sceneZ > rayPos.z + zBias && sceneZ - rayPos.z < thickness) {
            float3 lo = prevPos, hi = rayPos;
            [loop] for (int r = 0; r < REFINE_STEPS; r++) {
                float3 mid = (lo + hi) * 0.5;
                float wm; float2 midUV = ToUV(mid, wm);
                if (ViewPos(midUV).z > mid.z + zBias) hi = mid; else lo = mid;
            }
            float wd; hitUV = ToUV(hi, wd);
            hit = 1.0; break;
        }
    }
    if (hit < 0.5) return 0.0.xxxx;

    // Wider screen-edge fade (0.12 vs 0.08): thin surfaces whose reflection ray exits the screen pop in/out
    // binarily right at the border, reading as edge jitter. A softer ramp dissolves that flicker.
    float2 edge = min(hitUV, 1.0 - hitUV);
    float edgeFade = smoothstep(0.0, 0.12, min(edge.x, edge.y));
    float roughFade = 1.0 - smoothstep(0.3, MAX_ROUGHNESS, roughness);

    bool isMetal = metallic >= 0.5;
    float F0 = isMetal ? 0.6 : 0.04;
    float NdotV = max(dot(N, -Vdir), 0.0);
    float fresnel = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);
    // Grazing × roughness suppression (a rough surface shouldn't get a sharp grazing mirror).
    float grazing = fresnel - F0;
    float grazeKeep = 1.0 - smoothstep(0.05, 0.45, roughness);
    fresnel = F0 + grazing * grazeKeep;

    // GRAZING STABILITY: at extreme grazing angles (NdotV→0) the Fresnel term explodes and a sub-pixel normal
    // wobble on a thin/edge-on surface swings the reflection violently — the dominant SSR shimmer on thin
    // geometry. Fade strength out below ~25° grazing so the most unstable angles contribute softly, not as a
    // razor-sharp mirror. The mid-range and head-on reflections (where SSR is stable) are untouched.
    float grazeStable = smoothstep(0.05, 0.35, NdotV);

    // The scene-color texture has no mip chain, so a single point tap on thin/high-contrast reflected geometry
    // aliases (the dominant SSR shimmer source besides grazing). A small fixed 4-tap box around the hit UV,
    // widened with roughness, pre-filters that aliasing in-shader — a cheap stand-in for a roughness mip
    // (physically correct too: rougher = blurrier reflection). Head-on sharp metals (roughness≈0) get a tight
    // 1-texel kernel; rougher surfaces blur more.
    float blurR = (0.75 + roughness * 3.0);
    float2 t = TexelSize * blurR;
    float3 reflected = (ColorTex.SampleLevel(LinearClamp, hitUV + float2( t.x,  t.y), 0).rgb
                      + ColorTex.SampleLevel(LinearClamp, hitUV + float2(-t.x,  t.y), 0).rgb
                      + ColorTex.SampleLevel(LinearClamp, hitUV + float2( t.x, -t.y), 0).rgb
                      + ColorTex.SampleLevel(LinearClamp, hitUV + float2(-t.x, -t.y), 0).rgb) * 0.25;
    float surfaceLum = dot(ColorTex.SampleLevel(LinearClamp, i.Uv, 0).rgb, float3(0.2126, 0.7152, 0.0722));
    float lowLightDamp = smoothstep(0.0, 0.08, surfaceLum);

    float strength = saturate(fresnel * Intensity) * edgeFade * roughFade * lowLightDamp * grazeStable;
    return float4(reflected, strength);
}

float LinearDepth(float d) {
    float4 v = mul(float4(0.0, 0.0, d, 1.0), InvProjection);
    return v.z / v.w;
}

// Component-SELECT NaN/Inf scrub (never mix(v,0,flag): NaN*0==NaN, Inf*0==NaN — the proven AMD leak,
// [[ssgi-nan-mix-scrub]]). Defense-in-depth on the SSR read side (the SSGI combine already does this on its
// read): a reflection-hit shader is capped at the source, but this guards any future unbounded write from
// becoming a screen-eating Inf/NaN field via the bilinear acc + lerp + bloom downsample.
float4 SanitizeSsr(float4 v) {
    return float4(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                  isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                  isnan(v.z) || isinf(v.z) ? 0.0 : v.z,
                  isnan(v.w) || isinf(v.w) ? 0.0 : v.w);
}

// --- Combine pass: depth-aware upsample of the half-res SSR + lerp into the full-res scene color. ---
float4 PSCombine(VSOut i) : SV_Target {
    float3 scene = ColorTex.SampleLevel(LinearClamp, i.Uv, 0).rgb;

    float2 ssrSize = 1.0 / TexelSize;
    float2 pos = i.Uv * ssrSize - 0.5;
    float2 baseUV = (floor(pos) + 0.5) * TexelSize;
    float2 f = frac(pos);
    float centerZ = LinearDepth(DepthTex.SampleLevel(LinearClamp, i.Uv, 0).r);

    float4 acc = 0.0.xxxx; float wSum = 0.0;
    [unroll] for (int k = 0; k < 4; k++) {
        float2 corner = float2(k & 1, k >> 1);
        float2 uv = baseUV + corner * TexelSize;
        float wBilinear = (corner.x > 0.5 ? f.x : 1.0 - f.x) * (corner.y > 0.5 ? f.y : 1.0 - f.y);
        float tapZ = LinearDepth(DepthTex.SampleLevel(LinearClamp, uv, 0).r);
        float wDepth = 1.0 / (1.0 + abs(tapZ - centerZ) * 2.0);
        float w = wBilinear * wDepth + 1e-5;
        acc += SanitizeSsr(SsrTex.SampleLevel(LinearClamp, uv, 0)) * w;
        wSum += w;
    }
    float4 ssr = acc / wSum;
    // Lerp (not add) — the SSR hit replaces the sky-IBL reflection baked into the scene color.
    return float4(lerp(scene, ssr.rgb, ssr.a), 1.0);
}

// --- TEMPORAL pass (SSR + RT): motion-reprojected EMA over the half-res reflection target. RT mirror rays read
// the Lumen card cache, which churns frame to frame; SSR's half-res march + Fresnel edges flicker on a static
// view the same way — and reflections run BEFORE TAA with no other denoiser — so both feed this pass. Reproject
// last frame's
// reflection via the motion buffer and EMA-blend with this frame's, with a neighbourhood clamp + a disocclusion
// reset so a moving mirror / uncovered surface doesn't smear. The .a channel is the reflection STRENGTH (carried
// straight through, not accumulated). For this pass: ColorTex(t0)=current reflection, DepthTex(t1)=history,
// NormalTex(t2)=motion (G-buffer RG). Params: Intensity.x reused as hasHistory (0 on first frame / resize).
float4 PSTemporal(VSOut i) : SV_Target {
    float2 uv = i.Uv;
    float4 current = SanitizeSsr(ColorTex.SampleLevel(LinearClamp, uv, 0));

    float hasHistory = Intensity;   // SsrConstants.Intensity is repurposed as the hasHistory flag for this pass
    float2 motion = NormalTex.SampleLevel(LinearClamp, uv, 0).rg;   // prevUV - currUV (G-buffer motion)
    float2 prevUV = uv + motion;
    bool valid = hasHistory > 0.5 && all(prevUV >= 0.0) && all(prevUV <= 1.0);
    if (!valid) return current;

    // CRITICAL for mirrors: the G-buffer motion buffer tracks the REFLECTIVE SURFACE, NOT the reflected image
    // (the mirrored content moves at a DIFFERENT screen velocity than the surface). So surface-motion
    // reprojection is only valid when the surface is (nearly) STATIC on screen — under any real motion it pulls
    // the wrong reflected pixel and the temporal blend smears/erases the reflection (the "reflections vanished"
    // bug). Gate the temporal HARD on screen motion: ~0 motion → full denoise (kills the static-camera DDGI
    // churn, the actual goal); any real motion → pass the current reflection straight through (sharp, no erase).
    float motionPx = length(motion / max(TexelSize, 1e-6.xx));
    float motionTrust = saturate(1.0 - motionPx * 0.5);   // >~2 texels of motion → 0 (pure current frame)
    if (motionTrust < 0.01) return current;

    float4 history = SanitizeSsr(DepthTex.SampleLevel(LinearClamp, prevUV, 0));

    // Neighbourhood clamp (variance) on the RGB so a disoccluded mirror flushes instead of smearing.
    float3 m1 = 0.0.xxx, m2 = 0.0.xxx;
    [unroll] for (int x = -1; x <= 1; x++)
    [unroll] for (int y = -1; y <= 1; y++) {
        float3 c = SanitizeSsr(ColorTex.SampleLevel(LinearClamp, uv + float2(x, y) * TexelSize, 0)).rgb;
        m1 += c; m2 += c * c;
    }
    m1 /= 9.0; m2 /= 9.0;
    float3 sigma = sqrt(max(m2 - m1 * m1, 0.0.xxx));
    float3 lo = m1 - sigma * 2.0 - 0.02.xxx;
    float3 hi = m1 + sigma * 2.0 + 0.02.xxx;
    float3 clamped = clamp(history.rgb, lo, hi);

    // EMA toward the (clamped) history, faded out by motionTrust so a static mirror converges (smooth) while any
    // motion keeps the live reflection. alpha is the blend toward CURRENT, so low alpha = more history = smoother.
    float alpha = lerp(1.0, 0.15, motionTrust);   // static → 0.15 (heavy smoothing); moving → 1.0 (current only)
    float3 outRgb = lerp(clamped, current.rgb, alpha);
    return float4(outRgb, current.a);   // strength = current (not accumulated)
}
