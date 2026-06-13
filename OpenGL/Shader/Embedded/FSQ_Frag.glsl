#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D hdrTexture;
uniform sampler2D bloomTexture;
uniform sampler2D aoTexture;

uniform float Exposure;          // physical exposure multiplier: 1/(1.2 * 2^EV100), computed CPU-side
uniform float BloomIntensity;
uniform bool ApplyAO;
uniform float AoStrength;
uniform float Contrast;
uniform float Saturation;
uniform float VignetteStrength;
uniform float VignetteRoundness;   // 0 = follows aspect (oval), 1 = circular
uniform vec3  VignetteColor;        // tint the darkened edge toward this colour (usually black)
uniform float Aspect;              // width / height, for aspect-correct vignette roundness
uniform float FilmGrain;
uniform float Sharpen;

// Lens artefacts (a real camera is never edge-to-edge perfect-focus-perfect-colour):
//   ChromaticAberration - lateral RGB split that grows toward the frame edge
//   LensDistortion       - barrel(+)/pincushion(-) UV warp
uniform float ChromaticAberration;
uniform float LensDistortion;

// ACES RRT+ODT fit (Stephen Hill). Unlike the per-channel Narkowicz curve this runs in the
// ACES working space, so bright saturated colors desaturate toward white the way film does
// instead of skewing hue (orange sunsets stayed orange, not yellow). Matrices are column-major
// (transposed from the HLSL row-major originals).
const mat3 ACESInputMat = mat3(
    0.59719, 0.07600, 0.02840,
    0.35458, 0.90834, 0.13383,
    0.04823, 0.01566, 0.83777);

const mat3 ACESOutputMat = mat3(
     1.60475, -0.10208, -0.00327,
    -0.53108,  1.10813, -0.07276,
    -0.07367, -0.00605,  1.07602);

vec3 RRTAndODTFit(vec3 v) {
    vec3 a = v * (v + 0.0245786) - 0.000090537;
    vec3 b = v * (0.983729 * v + 0.4329510) + 0.238081;
    return a / b;
}

vec3 ACESFilm(vec3 x) {
    x = ACESInputMat * x;
    x = RRTAndODTFit(x);
    return clamp(ACESOutputMat * x, 0.0, 1.0);
}

// Exact sRGB OETF (the 1/2.2 power approximation crushes shadow detail slightly).
vec3 LinearToSrgb(vec3 c) {
    c = max(c, 0.0);
    vec3 lo = c * 12.92;
    vec3 hi = 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055;
    return mix(lo, hi, step(vec3(0.0031308), c));
}

float rand(vec2 co) {
    return fract(sin(dot(co, vec2(12.9898,78.233))) * 43758.5453);
}

// Barrel/pincushion lens warp around the frame centre. Positive = barrel (edges pull in),
// negative = pincushion. Returns the resampled UV; identity when LensDistortion == 0.
vec2 DistortUV(vec2 uv) {
    if (LensDistortion == 0.0)
        return uv;
    vec2 c = uv - 0.5;
    float r2 = dot(c, c);
    return 0.5 + c * (1.0 + LensDistortion * r2);
}

// HDR scene value at uv: scene color, screen-space AO, plus bloom, pre-tonemap.
// AO is applied here (not just in the forward ambient) because the screen-space SSAO buffer
// isn't available during forward shading - it's derived from the depth this pass produced.
// To avoid the old "double-darken + wrongly occlude direct sun" bug it's applied GENTLY:
// AoStrength lerps between no-AO and full-AO, and we bias toward darkening dimmer pixels
// (ambient-dominated contact areas) far more than bright directly-lit ones.
vec3 SceneHDR(vec2 uv) {
    vec3 hdr = texture(hdrTexture, uv).rgb;
    if (ApplyAO) {
        float ssao = texture(aoTexture, uv).r;
        // Brighter (directly-lit) pixels resist AO; dim (ambient-only) pixels take it fully.
        float luma = dot(hdr, vec3(0.2126, 0.7152, 0.0722));
        float litResist = clamp(luma * 2.0, 0.0, 1.0);
        float ao = mix(ssao, 1.0, litResist);
        hdr *= mix(1.0, ao, AoStrength);
    }
    hdr += texture(bloomTexture, uv).rgb * BloomIntensity;
    return hdr;
}

// HDR -> display: PHYSICAL exposure + ACES. `Exposure` is the photographic exposure
// multiplier 1/(1.2 * 2^EV100) computed CPU-side from the scene EV (see PostProcessSettings),
// so the scene radiance arriving here is in real relative-luminance units and a single EV dial
// balances sun + IBL + punctual lights the way a camera does - no per-effect brightness fudges.
// Every sample that feeds later math MUST go through this first; mixing raw HDR values with
// tonemapped ones explodes around very bright pixels (sun in an EXR sky) and produces NaN holes.
vec3 Tonemap(vec3 hdr) {
    return ACESFilm(hdr * Exposure);
}

// The full tonemap + unsharp + contrast + saturation grade at one UV, returned in linear
// (pre-sRGB) space. Factored out of main() so chromatic aberration can grade each colour
// channel at its own UV without re-implementing the chain — every sample stays internally
// consistent (tonemapped-with-tonemapped), never mixing raw HDR with tonemapped values.
vec3 GradeAt(vec2 uv) {
    vec3 color = Tonemap(SceneHDR(uv));

    // Optional unsharp mask (tonemapped samples; raw HDR here would create negatives/NaN).
    if (Sharpen > 0.0) {
        vec2 texel = 1.0 / vec2(textureSize(hdrTexture, 0));
        vec3 blur =
            Tonemap(SceneHDR(uv + vec2(-texel.x, 0.0))) +
            Tonemap(SceneHDR(uv + vec2( texel.x, 0.0))) +
            Tonemap(SceneHDR(uv + vec2(0.0, -texel.y))) +
            Tonemap(SceneHDR(uv + vec2(0.0,  texel.y)));
        blur *= 0.25;
        color = clamp(mix(blur, color, 1.0 + Sharpen), 0.0, 1.0);
    }

    // Contrast pivots around mid-grey for midtone punch. SHADOW-PRESERVING: a hard linear pivot at
    // 0.5 pushes any pixel below mid-grey toward black, which CRUSHES underexposed scenes to nothing
    // (Emerald Day went mean 18 -> 2). So fade the contrast strength toward 1.0 (no-op) in the deep
    // shadows: bright/mid pixels get the full punch, near-black pixels are left alone. Keeps the look
    // for normally-exposed scenes without destroying dim ones.
    if (Contrast != 1.0) {
        float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
        // Only the DEEPEST shadows (< ~0.08) ease off, so a normally-exposed scene keeps full
        // shadow punch while a near-black underexposed scene isn't crushed to nothing.
        float shadowKeep = smoothstep(0.015, 0.09, luma);
        float c = mix(1.0, Contrast, shadowKeep);
        color = clamp(mix(vec3(0.5), color, c), 0.0, 1.0);
    }

    if (Saturation != 1.0) {
        float gray = dot(color, vec3(0.299, 0.587, 0.114));
        color = mix(vec3(gray), color, Saturation);
    }
    return color;
}

void main()
{
    vec2 uv = DistortUV(TexCoords);

    vec3 color;
    if (ChromaticAberration > 0.0) {
        // Lateral CA: the red and blue channels sample at slightly larger/smaller radii from
        // the frame centre, the split growing with distance to the edge (zero at centre). This
        // is the dispersion a real lens shows and reads as a strong "photographed" cue.
        vec2 dir = uv - 0.5;
        vec2 offset = dir * ChromaticAberration * 0.01;
        color.r = GradeAt(uv + offset).r;
        color.g = GradeAt(uv).g;
        color.b = GradeAt(uv - offset).b;
    } else {
        color = GradeAt(uv);
    }

    // Natural vignette: a smooth radial falloff toward VignetteColor. Roundness blends between
    // an aspect-following oval (0) and a circle (1); the multiply darkens, the mix tints.
    if (VignetteStrength > 0.0) {
        // Roundness 1 = circular; 0 = stretch x by the aspect so the falloff follows the frame
        // shape (a real lens vignettes the corners of a wide frame, not a centred circle).
        vec2 c = uv - 0.5;
        c.x *= mix(Aspect, 1.0, VignetteRoundness);
        float dist = length(c);
        float v = mix(1.0, smoothstep(0.8, 0.35, dist), VignetteStrength);
        color = mix(VignetteColor, color, v);
    }

    color = LinearToSrgb(color);

    // Grain is a display-referred effect: add it after encoding so its amplitude is
    // perceptually uniform instead of exploding in the shadows.
    if (FilmGrain > 0.0)
        color += (rand(uv * 1280.0) - 0.5) * FilmGrain;

    FragColor = vec4(color, 1.0);
}
