#version 330 core

in vec2 TexCoords;
out vec4 FragColor; // rgb = denoised GI, a = history length (passed through)

// Edge-aware spatial denoiser (SVGF-style a-trous wavelet, single wide pass). A plain blur
// would bleed indirect light across depth/normal discontinuities (light leaking under a
// table, across a wall corner). Here each tap is weighted by how similar its depth and
// normal are to the centre pixel, so the blur only smooths noise WITHIN a surface and stops
// hard at edges. The temporal pass handles cross-frame noise; this handles within-frame
// noise and the disocclusion fireflies temporal can't yet have cleaned.

uniform sampler2D giTexture;      // temporally-accumulated GI (rgb) + history len (a)
uniform sampler2D depthTexture;
uniform sampler2D normalTexture;

uniform mat4 InvProjection;
uniform float StepSize;           // a-trous tap spacing in texels (wider = smoother)
uniform float DepthSigma;         // depth edge sensitivity (smaller = sharper edges kept)
uniform float NormalSigma;        // normal edge sensitivity

// True component SELECT - the old mix(v, 0, flag) form was arithmetic
// (v*(1-flag) + 0*flag) and NaN*0.0 == NaN, so it never actually scrubbed.
vec3 Sanitize(vec3 v) {
    return vec3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

float ViewZ(vec2 uv) {
    float depth = texture(depthTexture, uv).r;
    vec4 ndc = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 view = InvProjection * ndc;
    return view.z / view.w;
}

void main() {
    float depth = texture(depthTexture, TexCoords).r;
    vec4 centre = texture(giTexture, TexCoords);

    if (depth >= 1.0) {
        FragColor = centre;
        return;
    }

    vec2 texel = 1.0 / vec2(textureSize(giTexture, 0));
    vec3 centreN = normalize(texture(normalTexture, TexCoords).rgb * 2.0 - 1.0);
    float centreZ = ViewZ(TexCoords);
    float centreLuma = dot(centre.rgb, vec3(0.2126, 0.7152, 0.0722));

    // 5x5 a-trous kernel weights (B3 spline): {1,4,6,4,1}/16 per axis.
    const float kernel[5] = float[](0.0625, 0.25, 0.375, 0.25, 0.0625);

    // A surface that has accumulated few frames (just disoccluded) is still noisy, so widen
    // the blur there and tighten it once temporal has converged.
    float histLen = max(centre.a, 1.0);
    float widen = mix(2.0, 1.0, clamp((histLen - 1.0) / 8.0, 0.0, 1.0));
    float step = max(StepSize, 1.0) * widen;

    vec3 sum = vec3(0.0);
    float wSum = 0.0;

    for (int x = -2; x <= 2; x++) {
        for (int y = -2; y <= 2; y++) {
            vec2 offset = vec2(x, y) * step * texel;
            vec2 uv = TexCoords + offset;

            // Per-tap scrub: sum += c*w would be NaN even at w == 0 (NaN*0 == NaN), so one
            // contaminated tap used to poison every pixel whose kernel touched it. CLAMP each tap to a
            // sane HDR ceiling too: the half-res GI is fp16 and the noisy 1-spp gather has firefly
            // pixels that, accumulated over 4 widening iterations, pushed a channel past the fp16 max
            // (65504) -> Inf -> the combine's Inf-scrub then ZEROED that channel (the "pure-red, green
            // gone" GI). A finite ceiling keeps the denoise stable without changing the converged image.
            vec3 c = min(Sanitize(texture(giTexture, uv).rgb), vec3(4096.0));
            vec3 n = normalize(texture(normalTexture, uv).rgb * 2.0 - 1.0);
            float z = ViewZ(uv);
            float l = dot(c, vec3(0.2126, 0.7152, 0.0722));

            // Edge-stopping weights: kernel * normal * depth * LUMA. The luma term is what
            // stops bright bounce (a sun shaft on the floor) from blurring across a shadow
            // boundary onto the same flat surface - depth/normal can't see that edge because
            // both sides are the same wall. Scaled by history length so a freshly-disoccluded
            // (noisy) pixel blurs more freely and a converged one keeps its lighting edges.
            float lumaSigma = 4.0 / max(histLen, 1.0);
            float wKernel = kernel[x + 2] * kernel[y + 2];
            float wNormal = pow(max(dot(centreN, n), 0.0), NormalSigma);
            // Depth tolerance scales with view distance (perspective makes equal world steps
            // span more depth up close), but CLAMPED: unclamped it grew without bound, so a
            // distant table edge blurred GI straight across the depth discontinuity (halos).
            float wDepth = exp(-abs(centreZ - z) / (DepthSigma * clamp(abs(centreZ), 1.0, 8.0) + 1e-3));
            float wLuma = exp(-abs(centreLuma - l) / (lumaSigma + 1e-3));

            float w = wKernel * wNormal * wDepth * wLuma;
            sum += c * w;
            wSum += w;
        }
    }

    vec3 denoised = wSum > 1e-4 ? sum / wSum : Sanitize(centre.rgb);
    // Final scrub so the speckle can't reach the combine (and the temporal history, which is
    // read pre-denoise, already sanitizes separately).
    FragColor = vec4(Sanitize(denoised), centre.a);
}
