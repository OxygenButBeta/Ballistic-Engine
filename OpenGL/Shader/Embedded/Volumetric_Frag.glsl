#version 330 core

in vec2 TexCoords;
out vec4 FragColor; // rgb = in-scattered radiance (pre-exposed HDR), a = transmittance to the surface

// Physically-structured volumetric height fog + sun shafts, tied to the sky.
//
// The medium is an exponential height fog (sigma_t falls off with altitude, saturating at
// full density below BaseHeight). Each march step in-scatters two real light sources:
//   - the ATMOSPHERE-ATTENUATED sun (SunRadiance arrives pre-attenuated by the procedural
//     sky's transmittance, so dusk shafts go golden/red exactly like the sky's clouds and
//     the whole effect fades out when the sun sets), gated by the cascaded shadow map;
//   - the baked sky's average radiance as isotropic skylight (overcast clouds => gray fog,
//     clear dusk => dim warm fog: sky, clouds and fog share one energy source).
// Transmittance rides in alpha so the combine pass EXTINGUISHES the scene behind the fog -
// real fog hides things, it doesn't just add glow. Past MaxDistance (no shadow data) the
// fog continues ANALYTICALLY: closed-form optical depth, sun treated as lit (the same
// convention the lit shader uses outside every cascade), so distant hills and the horizon
// sky still sink into the fog instead of popping clear at the march boundary.

uniform sampler2D depthTexture;            // full-res scene depth (DepthComponent24, 0..1)
uniform sampler2DArrayShadow shadowMap;    // cascaded directional depth, hardware PCF compare

uniform mat4 InvProjection;            // unjittered camera inverse projection
uniform mat4 InvViewMatrix;            // camera view^-1 (view-space -> world)
const int MAX_CASCADES = 4;
uniform mat4 CascadeMatrices[MAX_CASCADES]; // world -> light clip per cascade
uniform vec4 CascadeBias;                   // compare-space bias per cascade
uniform int CascadeCount;

uniform vec3 SunDirectionWorld;        // normalized, points TOWARD the light (LightDirection)
uniform vec3 SunColor;                 // sun radiance: pre-exposed AND atmosphere-attenuated
uniform vec3 SkyAmbient;               // pre-exposed average radiance of the baked sky cubemap
uniform vec3 CameraPosWorld;

uniform int   StepCount;               // shadowed-march samples (cost vs banding)
uniform float Anisotropy;              // Henyey-Greenstein g, [0, 0.95]; higher = tighter shafts
uniform float Density;                 // extinction sigma_t at BaseHeight (1/m)
uniform float HeightFalloff;           // 1/m: how fast the fog thins with altitude (0 = uniform)
uniform float BaseHeight;              // world Y below which the fog is at full density
uniform float Scattering;              // sun in-scatter multiplier (1 = physical balance)
uniform float AmbientScatter;          // skylight in-scatter multiplier (1 = physical balance)
uniform float SunGlow;                 // extra boost concentrated around the sun disk
uniform float SunGlowSharpness;        // how tight the sun-disk glow is (higher = smaller, hotter)
uniform int   FrameIndex;              // animates the dither so TAA can resolve it
uniform float MaxDistance;             // far clamp for the SHADOWED march (analytic fog continues)

const float PI = 3.14159265359;
const float ALBEDO = 0.92;             // single-scatter albedo of the aerosol
const float SKY_TAIL = 20000.0;        // how far the analytic fog integrates on sky pixels

// Reconstruct world position from screen UV + sampled depth.
vec3 WorldPos(vec2 uv, float depth) {
    vec4 ndc = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 viewPos = InvProjection * ndc;
    viewPos /= viewPos.w;
    vec4 world = InvViewMatrix * viewPos;
    return world.xyz;
}

// True (un-normalized) Henyey-Greenstein: the directional contrast between looking into
// the sun and across the shafts is the physical one; the skylight term keeps off-sun fog
// visible, so no artificial phase floor is needed any more.
float HG(float mu, float g) {
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * PI * pow(max(1.0 + g2 - 2.0 * g * mu, 1e-4), 1.5));
}

// Fog extinction at a world height: exponential falloff above BaseHeight, saturating to
// full density below it (fog pools at the ground, it doesn't keep densifying downward).
float SigmaT(float y) {
    float f = HeightFalloff <= 0.0 ? 1.0
            : exp(min(-HeightFalloff * (y - BaseHeight), 0.0));
    return Density * f;
}

// Closed-form optical depth of the exponential medium along [t0, t1] - used for the
// analytic tail past the shadowed march. Ignores the below-base saturation (the exponent
// clamp keeps it from exploding; the tail is distant air where the difference is haze).
float OpticalDepth(vec3 o, vec3 d, float t0, float t1) {
    float len = max(t1 - t0, 0.0);
    if (len <= 0.0)
        return 0.0;
    if (HeightFalloff <= 1e-5)
        return Density * len;
    float s0 = Density * exp(min(-HeightFalloff * (o.y + d.y * t0 - BaseHeight), 0.0));
    float kdy = HeightFalloff * d.y;
    if (abs(kdy) < 1e-5)
        return s0 * len;
    return min(s0 * (1.0 - exp(-kdy * len)) / kdy, 40.0);
}

// Sun visibility at a world point via the cascade that covers it (0 = shadowed, 1 = lit).
float SampleSunVisibility(vec3 worldPos) {
    for (int c = 0; c < CascadeCount && c < MAX_CASCADES; c++) {
        vec4 clip = CascadeMatrices[c] * vec4(worldPos, 1.0);
        float edge = max(abs(clip.x), abs(clip.y));
        vec3 proj = clip.xyz * 0.5 + 0.5; // ortho: w == 1
        if (edge > 1.0 || proj.z > 1.0 || proj.z < 0.0)
            continue;
        return texture(shadowMap, vec4(proj.xy, float(c), proj.z - CascadeBias[c]));
    }
    // Outside every cascade: treat as lit (matches the lit shader).
    return 1.0;
}

// Interleaved-gradient noise: a cheap per-pixel dither for the march start offset.
float InterleavedGradientNoise(vec2 pix) {
    return fract(52.9829189 * fract(dot(pix, vec2(0.06711056, 0.00583715))));
}

void main() {
    float depth = texture(depthTexture, TexCoords).r;

    vec3 rayStart = CameraPosWorld;
    vec3 endPos = WorldPos(TexCoords, min(depth, 0.99999));
    vec3 toEnd = endPos - rayStart;
    float surfaceDist = length(toEnd);
    vec3 rayDir = toEnd / max(surfaceDist, 1e-4);
    bool isSky = depth >= 1.0;
    if (isSky)
        surfaceDist = SKY_TAIL;            // fog integrates analytically out to the horizon

    // The shadowed march covers the air with real shadow data; the rest is analytic.
    float marchDist = min(surfaceDist, MaxDistance);
    if (surfaceDist < 1e-3) {
        FragColor = vec4(0.0, 0.0, 0.0, 1.0);
        return;
    }

    int steps = clamp(StepCount, 8, 256);
    float stepLen = marchDist / float(steps);

    // Dither the first sample within one step so banding becomes per-pixel noise (TAA-friendly).
    vec2 pix = gl_FragCoord.xy + float(FrameIndex & 63);
    float jitter = InterleavedGradientNoise(pix);

    // Dual-lobe phase: a strong forward lobe (shafts blaze toward the sun) plus a soft
    // backscatter lobe (real aerosols are not one-sided). Directional light => constant
    // along the ray.
    vec3 sunDir = normalize(SunDirectionWorld);
    float mu = dot(rayDir, sunDir);
    float g = clamp(Anisotropy, 0.0, 0.95);
    float phaseSun = mix(HG(mu, -0.2), HG(mu, g), 0.82);

    // Per-step source terms (radiance per unit optical depth, Frostbite-style analytic
    // integration below keeps the result energy-conserving and bounded).
    vec3 sunSource = SunColor * (phaseSun * Scattering);
    // Isotropic ambient: integrating any phase over an isotropic radiance field gives the
    // field itself, so the skylight term is just SkyAmbient (no 1/4pi).
    vec3 ambSource = SkyAmbient * AmbientScatter;

    vec3 scatter = vec3(0.0);
    float transmittance = 1.0;
    for (int i = 0; i < steps; i++) {
        float t = (float(i) + jitter) * stepLen;
        vec3 samplePos = rayStart + rayDir * t;

        float sigma = SigmaT(samplePos.y);
        if (sigma > 1e-6) {
            float stepT = exp(-sigma * stepLen);
            float vis = SampleSunVisibility(samplePos);
            vec3 source = ALBEDO * (sunSource * vis + ambSource);
            scatter += source * (transmittance * (1.0 - stepT));
            transmittance *= stepT;
            if (transmittance < 0.002)
                break;
        }
    }

    // Analytic tail: the fog between the march end and the surface (or the sky horizon).
    // No shadow data out there, so the sun counts as lit - same as every lit-pass sample
    // outside the cascades - and the closed-form optical depth costs one exp.
    if (transmittance > 0.002 && surfaceDist > marchDist) {
        float tau = OpticalDepth(rayStart, rayDir, marchDist, surfaceDist);
        float tailT = exp(-tau);
        vec3 source = ALBEDO * (sunSource + ambSource);
        scatter += source * (transmittance * (1.0 - tailT));
        transmittance *= tailT;
    }

    // Sun-disk glow: a tight forward lobe so looking near the sun through mist blazes.
    // Scales with the REAL attenuated sun radiance (red and dim at dusk, gone at night)
    // and with how much fog the view actually crosses (1 - transmittance): no fog, no glow;
    // a wall right in front of the camera leaves no fog to glow.
    float glow = pow(max(mu, 0.0), max(SunGlowSharpness, 1.0)) * SunGlow;
    scatter += SunColor * (glow * (1.0 - transmittance));

    // Guard against any NaN/Inf leaking from an Inf sun color (EXR sun gotcha). Component
    // SELECT, never mix-by-flag (NaN*0 == NaN).
    if (any(isnan(scatter)) || any(isinf(scatter)))
        scatter = vec3(0.0);

    FragColor = vec4(max(scatter, vec3(0.0)), clamp(transmittance, 0.0, 1.0));
}
