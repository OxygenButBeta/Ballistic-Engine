#version 330 core

in vec2 TexCoords;
out vec4 FragColor; // rgb = accumulated in-scatter radiance (HDR), a = unused

// Half-res raymarched volumetric sun scattering (god-rays / light shafts).
// March the camera->scene ray and, at each step, add the sun light scattered toward the
// camera by the (lit) air at that point, attenuated by how much medium the light has
// already travelled through (Beer-Lambert). Long lit air paths => bright shafts; short
// paths and shadowed air stay dark. A tight sun-disk glow blazes when looking at the sun.
//
// The result is an additive HDR layer; Volumetric_Combine upsamples + composites it.

uniform sampler2D depthTexture;            // full-res scene depth (DepthComponent24, 0..1)
uniform sampler2DArrayShadow shadowMap;    // cascaded directional depth, hardware PCF compare

uniform mat4 InvProjection;            // unjittered camera inverse projection
uniform mat4 InvViewMatrix;            // camera view^-1 (view-space -> world)
const int MAX_CASCADES = 4;
uniform mat4 CascadeMatrices[MAX_CASCADES]; // world -> light clip per cascade
uniform vec4 CascadeBias;                   // compare-space bias per cascade
uniform int CascadeCount;

uniform vec3 SunDirectionWorld;        // normalized, points TOWARD the light (LightDirection)
uniform vec3 SunColor;                 // sun radiance (HDR)
uniform vec3 CameraPosWorld;

uniform int   StepCount;               // marching samples (cost vs banding)
uniform float Anisotropy;              // Henyey-Greenstein g, [-0.95, 0.95]; higher = tighter shafts
uniform float Density;                 // medium thickness: extinction + scatter probability per metre
uniform float Scattering;              // in-scatter strength (how much light the air returns)
uniform float SunGlow;                 // extra boost concentrated around the sun disk
uniform float SunGlowSharpness;        // how tight the sun-disk glow is (higher = smaller, hotter)
uniform float AmbientFloor;            // [0..1] min phase so shafts read when NOT looking at the sun
uniform int   FrameIndex;              // animates the dither so TAA can resolve it
uniform float MaxDistance;             // far clamp for the march (world units)

const float PI = 3.14159265359;

// Reconstruct world position from screen UV + sampled depth.
vec3 WorldPos(vec2 uv, float depth) {
    vec4 ndc = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 viewPos = InvProjection * ndc;
    viewPos /= viewPos.w;
    vec4 world = InvViewMatrix * viewPos;
    return world.xyz;
}

// Henyey-Greenstein phase, NORMALIZED so its peak (looking straight at the sun) is 1.0 and
// it falls off to a small value off-sun. The raw HG value spikes to a huge number at the
// forward peak for high g, which makes the shafts blinding on-sun and invisible just off it
// (impossible to balance). Dividing by the peak keeps it a unitless [~0..1] SHAPE factor, so
// Scattering alone controls brightness and the knobs stay gentle.
float PhaseShape(float cosTheta, float g) {
    float g2 = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cosTheta;
    float hg = (1.0 - g2) / max(pow(denom, 1.5), 1e-4);
    // Peak occurs at cosTheta = 1: denom = (1-g)^2, hg_peak = (1+g)/(1-g)^2.
    float peak = (1.0 + g) / max((1.0 - g) * (1.0 - g), 1e-4);
    return clamp(hg / peak, 0.0, 1.0);
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

    // March endpoint: the scene surface, or the far clamp for sky pixels so shafts still
    // form across open sky between buildings.
    vec3 rayStart = CameraPosWorld;
    vec3 endPos = WorldPos(TexCoords, min(depth, 0.99999));
    vec3 toEnd = endPos - rayStart;
    float marchDist = length(toEnd);
    if (depth >= 1.0)
        marchDist = MaxDistance;            // sky: march a fixed slab of air
    marchDist = min(marchDist, MaxDistance);
    if (marchDist < 1e-3) {
        FragColor = vec4(0.0);
        return;
    }
    vec3 rayDir = toEnd / max(length(toEnd), 1e-4);

    int steps = clamp(StepCount, 8, 256);
    float stepLen = marchDist / float(steps);

    // Dither the first sample within one step so banding becomes per-pixel noise (TAA-friendly).
    vec2 pix = gl_FragCoord.xy + float(FrameIndex & 63);
    float jitter = InterleavedGradientNoise(pix);

    // Phase: directional light, so the view-ray-to-sun angle is constant along the ray.
    // Normalized to a [0..1] SHAPE: 1 looking at the sun, falling off to the sides. It sets
    // WHERE shafts are bright, never HOW bright (that's Scattering) - so it can't swing the
    // magnitude across the screen.
    vec3 sunDir = normalize(SunDirectionWorld);
    float cosTheta = dot(rayDir, sunDir);
    // Phase shape peaks toward the sun, but lift it by AmbientFloor so beams are still visible
    // when looking ACROSS them (not only into the sun) - otherwise the effect appears to "turn
    // off" the moment the sun leaves the view. mix(floor, 1, shape) keeps the sun-facing peak.
    float phase = mix(AmbientFloor, 1.0, PhaseShape(cosTheta, clamp(Anisotropy, -0.95, 0.95)));

    // Use the sun's HUE, not its raw magnitude (LightIntensity is an unrelated 0..20 knob).
    vec3 sunTint = SunColor / max(max(SunColor.r, max(SunColor.g, SunColor.b)), 1e-3);

    // Accumulate the DENSITY-WEIGHTED LIT FRACTION along the ray - a pure [0..1] quantity:
    // how much of the (depth-weighted) air the camera sees through is sunlit. Density only
    // shapes the depth falloff (near air weighted more than far), it does NOT scale
    // brightness. Because litFraction is bounded to [0,1], the final scatter is bounded too,
    // so Scattering is a gentle linear brightness with no cliff.
    float density = max(Density, 1e-4);
    float litWeighted = 0.0;
    float weightSum = 0.0;
    float transmittance = 1.0;
    for (int i = 0; i < steps; i++) {
        float t = (float(i) + jitter) * stepLen;
        vec3 samplePos = rayStart + rayDir * t;

        float vis = SampleSunVisibility(samplePos);
        float w = transmittance;           // near air contributes more than distant air
        litWeighted += vis * w;
        weightSum += w;

        transmittance *= exp(-density * stepLen);
        if (transmittance < 0.003)
            break;
    }
    float litFraction = litWeighted / max(weightSum, 1e-4); // [0,1]

    // Final shaft radiance: lit fraction * phase shape * brightness, tinted by the sun.
    // Bounded and predictable: at Scattering=1 the brightest shaft is ~1.0.
    vec3 scatter = sunTint * (litFraction * phase * Scattering);

    // Sun-disk glow: a tight forward lobe added on top of the shafts so looking near the sun
    // blazes. Only valid through open air (sky) or thin geometry; gate it by the same march
    // transmittance so it doesn't punch through solid walls. Raised to a sharp power for a
    // small, hot disk rather than a broad wash.
    float sunFacing = max(cosTheta, 0.0);
    float glow = pow(sunFacing, max(SunGlowSharpness, 1.0)) * SunGlow;
    // Fade the glow in over distance so a wall right in front of the camera occludes it.
    float glowReach = clamp(marchDist / max(MaxDistance * 0.5, 1.0), 0.0, 1.0);
    scatter += sunTint * glow * glowReach;

    // Guard against any NaN/Inf leaking from an Inf sun color (EXR sun gotcha).
    if (any(isnan(scatter)) || any(isinf(scatter)))
        scatter = vec3(0.0);

    FragColor = vec4(max(scatter, vec3(0.0)), 1.0);
}
