#version 460 core

in vec2 TexCoords;
out vec4 FragColor; // rgb = gathered one-bounce irradiance, a = confidence (edge fade)

// Screen-space global illumination - HORIZON GATHER WITH SECTOR VISIBILITY BITMASKS
// (SSILVB, Therrien et al. 2023 - the technique class behind HTrace-style Unity GI).
//
// Instead of shooting a few stochastic hemisphere rays (noisy 1-spp signal, "first hit
// wins", no occlusion ordering), each pixel integrates a small set of HORIZON SLICES:
// for every slice direction we march the depth buffer both ways and maintain a 32-bit
// occlusion bitmask over the hemisphere arc. Each sample occupies the angular sector
// between its front face and an assumed back face (Thickness metres behind it); only
// sectors it NEWLY occludes contribute its radiance. The result per slice is an ordered,
// noise-free arc integral:
//  - near occluders correctly block light from surfaces behind them (no scene-average veil)
//  - thin geometry occludes thin sectors instead of everything behind it
//  - the CLEAR sectors at the end are exactly the visible sky fraction, so the sky
//    fallback is occlusion- and direction-aware instead of a flat exposure-like lift.
// The temporal accumulation + a-trous denoise after this pass stay unchanged - they just
// have far less noise to clean.

uniform sampler2D colorTexture;   // lit HDR scene (the bounce source)
uniform sampler2D depthTexture;
uniform sampler2D normalTexture;  // world normal (0..1) + roughness/metal flag in alpha
uniform sampler2D historyColor;   // last frame's COMBINED GI fill, for the multi-bounce feed

// Sky for the UNOCCLUDED part of the hemisphere (the clear bitmask sectors). 0 = off (the
// IBL ambient already counts the sky; raise only in closed interiors with openings). Unlike
// the old per-missed-ray version this is occlusion-weighted, so it can never become a flat
// frame-wide veil: a pixel staring at a wall gets none.
uniform samplerCube EnvironmentMap;
uniform mat4 SkyRotation;         // same rotation the skybox/IBL sampling uses
uniform float SkyExposure;        // sky luminance scale x camera pre-exposure
uniform float SkyFallback;        // 0..1 blend of the visible-sky contribution
uniform float MaxEnvMip;          // roughest prefiltered mip (diffuse-ish cone)

uniform mat4 Projection;          // jittered camera projection (matches the depth buffer)
uniform mat4 InvProjection;
uniform mat4 ViewMatrix;
uniform int FrameIndex;           // rotates the slice set each frame
uniform int RayCount;             // horizon slices per pixel (<= MAX_SLICES)

// Artistic / quality controls (same dials as the old march).
uniform float RayLength;          // max gather distance in metres (near vs far bounce)
uniform float Falloff;            // distance falloff exponent; >0 favours nearby bounce
uniform float Thickness;          // assumed occluder thickness in metres (sector back face)
uniform float MultiBounce;        // 0..1: how much of last frame's GI re-bounces this frame
uniform float BounceBoost;        // amplify bright hits for a richer "final gather" feel

const int MAX_SLICES = 8;         // compile-time loop bound; RayCount gates it at runtime
const int STEPS = 8;              // depth samples per slice direction (16 per slice)
const int SECTORS = 32;           // bitmask resolution over the hemisphere arc
const float PI = 3.14159265359;
const float HALF_PI = 1.57079632679;
const float FIREFLY_KNEE = 6.0;   // per-sample radiance luma cap (hits AND sky)

vec3 ViewPosFromDepth(vec2 uv, float depth) {
    vec4 ndc = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 view = InvProjection * ndc;
    return view.xyz / view.w;
}

vec3 ViewPos(vec2 uv) {
    return ViewPosFromDepth(uv, texture(depthTexture, uv).r);
}

float Hash(vec2 p) {
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

// MUST be a true component SELECT, never arithmetic on the bad value: mix(v, 0, flag)
// expands to v*(1-flag) + 0*flag, and NaN*0.0 == NaN / Inf*0.0 == NaN in IEEE, so that
// form passes the poison straight through (proven on AMD RX 9070 XT). With the temporal
// EMA + multi-bounce feedback one bad pixel grows into a screen-eating black-noise field.
vec3 Sanitize(vec3 v) {
    return vec3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

// Set the bitmask sectors covered by the angular interval [a0, a1] (radians, measured from
// the view vector, clamped to the [-PI/2, +PI/2] hemisphere window).
uint OccludeSectors(float a0, float a1) {
    float lo = clamp((min(a0, a1) + HALF_PI) / PI, 0.0, 1.0);
    float hi = clamp((max(a0, a1) + HALF_PI) / PI, 0.0, 1.0);
    int b0 = int(lo * float(SECTORS));
    int count = clamp(int(ceil(hi * float(SECTORS))) - b0, 0, SECTORS);
    if (count <= 0)
        return 0u;
    uint mask = count >= 32 ? 0xFFFFFFFFu : ((1u << uint(count)) - 1u);
    return mask << uint(b0);
}

void main() {
    float depth = texture(depthTexture, TexCoords).r;
    vec4 nr = texture(normalTexture, TexCoords);
    vec3 worldN = nr.rgb * 2.0 - 1.0;

    // Sky or un-shaded pixels: nothing to receive bounce here.
    if (depth >= 1.0 || dot(worldN, worldN) < 0.1) {
        FragColor = vec4(0.0);
        return;
    }

    vec3 P = ViewPosFromDepth(TexCoords, depth);
    vec3 N = normalize(mat3(ViewMatrix) * worldN);
    vec3 V = -normalize(P); // toward the camera

    float rayLength = max(RayLength, 0.1);

    // Screen-space radius of the gather: rayLength metres projected at this pixel's depth.
    // Clamped so an extreme close-up doesn't march the whole frame in 8 giant steps.
    vec2 uvRadius = min(rayLength * 0.5 * vec2(Projection[0][0], Projection[1][1])
                        / max(-P.z, 0.05), vec2(0.5));

    // Per-pixel rotation of the slice set, advanced each frame so temporal can resolve it.
    float noise = Hash(TexCoords * vec2(textureSize(depthTexture, 0)) + float(FrameIndex) * 1.618);
    float stepNoise = Hash(TexCoords * 911.0 + float(FrameIndex) * 2.71);

    int slices = clamp(RayCount, 1, MAX_SLICES);
    vec3 bounce = vec3(0.0);
    float skyVisible = 0.0;

    for (int i = 0; i < MAX_SLICES; i++) {
        if (i >= slices)
            break;
        float phi = PI * (float(i) + noise) / float(slices);
        vec2 dir2 = vec2(cos(phi), sin(phi));

        // Slice tangent in view space: the screen direction lifted into the plane
        // perpendicular to V, so sample angles are measured in a consistent frame.
        vec3 sliceDir = vec3(dir2, 0.0);
        vec3 T = normalize(sliceDir - V * dot(sliceDir, V));

        uint bits = 0u;

        for (int j = 0; j < 2; j++) {
            float side = j == 0 ? 1.0 : -1.0;
            for (int s = 1; s <= STEPS; s++) {
                // Quadratic step distribution: dense near field (where bounce matters
                // most), sparse far field. Jitter breaks banding; temporal resolves it.
                float t = (float(s) - 0.5 + (stepNoise - 0.5)) / float(STEPS);
                t = t * t;
                vec2 uv = TexCoords + side * dir2 * (t * uvRadius);
                if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
                    break;

                float sd = texture(depthTexture, uv).r;
                if (sd >= 1.0)
                    continue; // sky sample: occludes nothing

                vec3 S = ViewPosFromDepth(uv, sd);
                vec3 delta = S - P;
                float dist = length(delta);
                if (dist < 1e-4)
                    continue;
                vec3 w = delta / dist;

                // Angles in the slice plane, measured from V (signed toward T). The sample
                // occupies the sectors between its front face and an assumed back face
                // Thickness metres further from the camera - thin geometry occludes thin
                // sectors, so light correctly leaks past a railing but not past a wall.
                vec3 deltaBack = delta + normalize(S) * max(Thickness, 0.01); // away from camera
                float aFront = atan(dot(delta, T), dot(delta, V));
                float aBack = atan(dot(deltaBack, T), dot(deltaBack, V));

                uint sampleBits = OccludeSectors(aFront, aBack) & ~bits;
                if (sampleBits != 0u) {
                    float newFrac = float(bitCount(sampleBits)) / float(SECTORS);

                    // Cosine-weighted: bounce arriving along w onto the surface around N.
                    float cosW = clamp(dot(N, w), 0.0, 1.0);
                    if (cosW > 0.0) {
                        // Distance falloff keeps GI local (Falloff shapes the curve).
                        float fade = pow(clamp(1.0 - dist / rayLength, 0.0, 1.0),
                                         max(Falloff, 0.0));

                        // Incoming radiance = lit color at the sample + a fraction of last
                        // frame's GI there (cheap multi-bounce compounding).
                        vec3 radiance = Sanitize(texture(colorTexture, uv).rgb)
                                      + Sanitize(texture(historyColor, uv).rgb) * MultiBounce;
                        radiance *= 1.0 + BounceBoost * dot(radiance, vec3(0.333));

                        // Firefly clamp - outliers only; normal bright bounce stays intact.
                        float lum = dot(radiance, vec3(0.2126, 0.7152, 0.0722));
                        if (lum > FIREFLY_KNEE)
                            radiance *= FIREFLY_KNEE / lum;

                        // x2 calibrates a fully-enclosed lit arc (avg cos ~0.5) to ~L,
                        // matching the old march's all-rays-hit magnitude so existing
                        // profile Intensity values keep their meaning.
                        bounce += radiance * (newFrac * 2.0) * cosW * fade;
                    }
                    bits |= sampleBits;
                }
                // Early-out: once every sector is occluded, no later sample on this slice can
                // contribute (sampleBits = OccludeSectors(..) & ~bits would be 0), so the rest of
                // this slice's marching is wasted work. Bit-identical result, fewer texture fetches.
                if (bits == 0xFFFFFFFFu)
                    break;
            }
            if (bits == 0xFFFFFFFFu)
                break; // both sides of this slice are fully occluded — skip the other side too
        }

        // The sectors no sample occluded are open sky for this slice.
        skyVisible += 1.0 - float(bitCount(bits)) / float(SECTORS);
    }

    bounce /= float(slices);
    skyVisible /= float(slices);

    // Sky through the visible fraction of the hemisphere: a diffuse-cone env sample along
    // the surface normal, scaled by how much of the horizon is actually open. A pixel
    // facing a wall gets ~zero - this can no longer read as a global exposure lift.
    if (SkyFallback > 0.0) {
        vec3 skyDir = transpose(mat3(SkyRotation)) * worldN;
        vec3 sky = Sanitize(textureLod(EnvironmentMap, skyDir, MaxEnvMip).rgb) * SkyExposure;
        float skyLum = dot(sky, vec3(0.2126, 0.7152, 0.0722));
        if (skyLum > FIREFLY_KNEE)
            sky *= FIREFLY_KNEE / skyLum;
        bounce += sky * (SkyFallback * skyVisible);
    }

    vec2 edge = min(TexCoords, 1.0 - TexCoords);
    float edgeFade = smoothstep(0.0, 0.06, min(edge.x, edge.y));

    FragColor = vec4(Sanitize(bounce), edgeFade);
}
