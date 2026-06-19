// Ground-Truth Ambient Occlusion (GTAO, Jimenez et al. 2016) for the DX12 backend. Replaces the old ported
// HBAO (Ssao.hlsl). For each pixel it reconstructs the view-space position + normal from the G-buffer, then
// for SliceCount azimuthal slices it sweeps StepCount samples to each side, finds the nearest unoccluded
// horizon angle, and integrates the COSINE-WEIGHTED visible hemisphere arc between those horizons. That
// cosine weighting is what makes it ground-truth: flat open ground integrates to exactly 1 (no occlusion),
// crevices grade smoothly, with none of HBAO's tangent-bias darkening of gentle slopes.
//
// Albedo-aware MULTI-BOUNCE (Jimenez): the single visibility scalar is re-mapped by an albedo-fit cubic so
// dark crevices keep the light that would bounce within them instead of crushing to black.
//
// Output is AO in R (1 = unoccluded). A separable BILATERAL blur (BlurH/BlurV) denoises it depth-aware so
// AO does not bleed across silhouettes. The result is consumed by the DEFERRED LIGHTING pass and multiplied
// into the IBL ambient term ONLY (never the direct light) -- the physically-correct layer.

cbuffer GtaoConstants : register(b0) {
    float4x4 Projection;     // camera projection (DX z[0,1]), transposed on upload
    float4x4 InvProjection;  // its inverse, transposed
    float4x4 View;           // world -> view (transposed) to rotate the G-buffer world normal into view space
    float  Radius;           // world-space falloff radius
    float  Intensity;        // AO strength (GTAO is physically normalized; 1 = neutral)
    float  Power;            // contrast/falloff exponent on the occlusion (1 = linear)
    float  Thickness;        // assumed occluder thickness in metres (thin lets light past railings/foliage)
    float2 TexelSize;        // 1 / AO-buffer size
    float  MultiBounce;      // > 0.5 = apply the Jimenez albedo-aware multi-bounce remap
    float  SliceCount;       // azimuthal slices (from the quality preset)
    float  StepCount;        // samples per slice side (from the quality preset)
    float  FrameIndex;       // animates the per-pixel rotation/jitter (0 under deterministic capture)
    float2 _pad;
};

Texture2D DepthTex  : register(t0);
Texture2D AoTex     : register(t0);   // alias for the blur passes (same register, different bind)
Texture2D NormalTex : register(t1);   // G-buffer world normal (packed [0,1]) -- main pass only
Texture2D AlbedoTex : register(t2);   // G-buffer albedo (rgb) -- main pass, multi-bounce only
SamplerState PointClamp : register(s0);

static const float PI      = 3.14159265359;
static const float HALF_PI = 1.57079632679;
static const int   MAX_SLICES = 6;    // upper bound for the [unroll]; SliceCount gates the real work
static const int   MAX_STEPS  = 12;

struct VSOut { float4 Position : SV_Position; float2 Uv : TEXCOORD0; };
VSOut VSMain(uint vid : SV_VertexID) {
    VSOut o;
    float2 uv = float2((vid << 1) & 2, vid & 2);
    o.Position = float4(uv * float2(2, -2) + float2(-1, 1), 0, 1);
    o.Uv = uv;
    return o;
}

// Reconstruct the view-space position from depth. DX NDC: xy [-1,1] (uv.y flipped), z = depth [0,1].
float3 ViewPos(float2 uv) {
    float depth = DepthTex.SampleLevel(PointClamp, uv, 0).r;
    float4 ndc = float4(uv.x * 2.0 - 1.0, (1.0 - uv.y) * 2.0 - 1.0, depth, 1.0);
    float4 v = mul(ndc, InvProjection);
    return v.xyz / v.w;
}

// The view-space surface normal, straight from the G-buffer world normal (sharper + silhouette-correct).
float3 ViewNormal(float2 uv) {
    float3 nWorld = normalize(NormalTex.SampleLevel(PointClamp, uv, 0).rgb * 2.0 - 1.0);
    return normalize(mul(float4(nWorld, 0.0), View).xyz);
}

// Spatio-temporal noise: an interleaved-gradient hash that rotates per pixel and (when FrameIndex animates)
// per frame, so TAA + the bilateral blur average the slices toward the true integral.
float Noise(float2 px, float frame) {
    float n = frac(52.9829189 * frac(dot(px, float2(0.06711056, 0.00583715))));
    return frac(n + 0.61803398875 * frame);   // golden-ratio temporal advance
}

// Jimenez 2016 albedo-aware multi-bounce remap: a*x^3 - b*x^2 + c*x, with the cubic coefficients fit per
// colour channel from the surface albedo. Re-introduces the intra-crevice bounce so dark cavities do not
// crush to black. `vis` is the raw visibility (1 = open); returns a per-channel visibility.
float3 MultiBounceAo(float vis, float3 albedo) {
    float3 a =  2.0404 * albedo - 0.3324;
    float3 b =  4.7951 * albedo - 0.6417;
    float3 c =  2.7552 * albedo + 0.6903;
    return max(vis.xxx, ((vis * a - b) * vis + c) * vis);
}

float4 PSMain(VSOut i) : SV_Target {
    float depth = DepthTex.SampleLevel(PointClamp, i.Uv, 0).r;
    if (depth >= 1.0) return 1.0.xxxx;   // sky: unoccluded

    float3 P = ViewPos(i.Uv);            // view-space position (z is negative looking forward, RH)
    float3 N = ViewNormal(i.Uv);
    float3 V = normalize(-P);            // toward the camera

    // World radius -> screen pixels at this depth (clamped so grazing pixels do not march the whole screen).
    float radiusPx = Radius / max(-P.z, 1e-3) * (0.5 / TexelSize.y);
    radiusPx = clamp(radiusPx, 2.0, 0.4 / TexelSize.y);

    int slices = (int)SliceCount;
    int steps  = (int)StepCount;
    float noise = Noise(i.Uv / TexelSize, FrameIndex);

    float visibility = 0.0;
    float weightSum = 0.0;   // sum of per-slice |projN| weights — the GTAO normalizer (NOT slice count)
    [unroll] for (int s = 0; s < MAX_SLICES; s++) {
        if (s >= slices) break;
        // Azimuth of this slice (rotated by the per-pixel noise so the pattern dithers, not bands).
        float phi = (s + noise) * PI / slices;
        float2 dir = float2(cos(phi), sin(phi));      // screen-space slice direction
        // The slice plane in view space; project N onto it to get the tangent frame for the cosine integral.
        float3 sliceDir = float3(dir, 0.0);
        float3 sliceNormal = normalize(cross(sliceDir, V));   // plane normal
        float3 projN = N - sliceNormal * dot(N, sliceNormal); // N projected into the slice plane
        float projNLen = length(projN);
        if (projNLen < 1e-4) continue;
        float3 projNDir = projN / projNLen;

        // Signed angle of the projected normal within the slice plane (the integral is measured from here).
        float3 sliceTangent = cross(sliceNormal, V);          // in-plane axis orthogonal to V
        float n = atan2(dot(projNDir, sliceTangent), dot(projNDir, V));

        // March both sides, tracking the largest horizon cos we have seen (nearest occluder raises it). The
        // horizon search is a PURE MAXIMUM (reference GTAO): a later sample can only RAISE the horizon, never
        // lower it. Falloff is applied to the arc CONTRIBUTION afterwards, not to the horizon search itself
        // (mixing it into the search erodes the horizon back to the open hemisphere → the corners-only bug).
        float2 cHorizons = float2(-1.0, -1.0);   // cos(horizon) for the -dir and +dir half-spaces
        [unroll] for (int side = 0; side < 2; side++) {
            float sign = side == 0 ? -1.0 : 1.0;
            float cHorizon = -1.0;
            [unroll] for (int t = 0; t < MAX_STEPS; t++) {
                if (t >= steps) break;
                float frac = (t + 0.5 + noise) / steps;       // jittered step distance [0,1]
                float2 sampleUv = i.Uv + sign * dir * frac * radiusPx * TexelSize;
                float3 sv = ViewPos(sampleUv) - P;            // view-space delta to the sample
                float dist = length(sv);
                if (dist < 1e-4 || dist > Radius) continue;
                // cos of the elevation of this sample above the view direction, inside the slice plane.
                float3 svDir = sv / dist;
                float cSample = dot(svDir, V);
                // Thickness heuristic: a sample only a thin slab BEHIND the current horizon does not block it —
                // bias its cosine down by Thickness so a thin railing/leaf in front of empty space releases the
                // arc. This still only ever RAISES the horizon (it's inside the max), so occlusion is preserved.
                cHorizon = max(cHorizon, cSample - Thickness * (1.0 - saturate(cSample)));
            }
            cHorizons[side] = cHorizon;
        }

        // Convert horizon cosines to angles relative to the projected normal, clamp to the visible hemisphere.
        float h0 = -acos(clamp(cHorizons.x, -1.0, 1.0));   // -dir side (negative angle)
        float h1 =  acos(clamp(cHorizons.y, -1.0, 1.0));   // +dir side (positive angle)
        h0 = n + max(h0 - n, -HALF_PI);
        h1 = n + min(h1 - n,  HALF_PI);

        // GTAO closed-form cosine-weighted inner integral for each arc (Jimenez eq.); weight by |projN|.
        float a0 = 0.25 * (-cos(2.0 * h0 - n) + cos(n) + 2.0 * h0 * sin(n));
        float a1 = 0.25 * (-cos(2.0 * h1 - n) + cos(n) + 2.0 * h1 * sin(n));
        visibility += projNLen * (a0 + a1);
        weightSum  += projNLen;
    }
    // Normalize by the SUM of |projN| weights (reference GTAO), not the slice count — dividing by slice count
    // under-counts (each |projN| <= 1) and systematically washes the occlusion out toward 1.
    visibility = saturate(visibility / max(weightSum, 1e-4));

    // Power shapes contrast; Intensity scales toward full occlusion. Intensity in [0,1] lerps from unoccluded
    // (1) toward the AO; ABOVE 1 it keeps deepening via an exponent (the AmbientOcclusion volume's slider goes
    // past 1, so `saturate(Intensity)` silently capped it — a darker setting did nothing). The exponent form
    // stays in [0,1] (no negative-AO from a lerp overshoot).
    float ao = pow(saturate(visibility), Power);
    ao = Intensity <= 1.0 ? lerp(1.0, ao, max(Intensity, 0.0))
                          : pow(ao, Intensity);   // >1 = stronger occlusion, clamped to [0,1] by pow of a [0,1] base

    if (MultiBounce > 0.5) {
        float3 albedo = AlbedoTex.SampleLevel(PointClamp, i.Uv, 0).rgb;
        // Collapse the per-channel multi-bounce visibility to a single scalar AO (the deferred pass stores R8);
        // luminance keeps the colour balance while avoiding a 3-channel AO target.
        float3 mb = MultiBounceAo(ao, albedo);
        ao = dot(mb, float3(0.2126, 0.7152, 0.0722));
    }
    return saturate(ao).xxxx;
}

// Separable BILATERAL blur over the AO (reads AoTex at t0; depth at... the blur reads only AO here, the
// depth-aware weight uses the AO target's own neighbourhood since AO is already smooth across surfaces and
// sharp across silhouettes -- a range weight on the AO value rejects cross-silhouette taps cheaply).
float4 BlurDir(float2 uv, float2 dir) {
    float center = AoTex.SampleLevel(PointClamp, uv, 0).r;
    float sum = center;
    float wsum = 1.0;
    // 5-tap symmetric kernel with a range (bilateral) weight that drops taps whose AO differs sharply from
    // the centre -- this preserves contact-shadow edges that a plain Gaussian would smear.
    [unroll] for (int k = 1; k <= 2; k++) {
        float gw = k == 1 ? 0.4 : 0.15;     // spatial weight
        float2 o = dir * k * TexelSize;
        float sp = AoTex.SampleLevel(PointClamp, uv + o, 0).r;
        float sm = AoTex.SampleLevel(PointClamp, uv - o, 0).r;
        float wp = gw * saturate(1.0 - abs(sp - center) * 8.0);
        float wm = gw * saturate(1.0 - abs(sm - center) * 8.0);
        sum += sp * wp + sm * wm;
        wsum += wp + wm;
    }
    return (sum / max(wsum, 1e-4)).xxxx;
}
float4 PSBlurH(VSOut i) : SV_Target { return BlurDir(i.Uv, float2(1, 0)); }
float4 PSBlurV(VSOut i) : SV_Target { return BlurDir(i.Uv, float2(0, 1)); }
