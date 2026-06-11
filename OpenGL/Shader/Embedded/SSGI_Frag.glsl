#version 330 core

in vec2 TexCoords;
out vec4 FragColor; // rgb = gathered one-bounce irradiance, a = confidence (edge fade)

// Screen-space global illumination: a coarse diffuse gather. For each pixel we shoot a few
// cosine-weighted rays into the hemisphere around its normal and march them against the
// depth buffer (the same march SSR uses). Where a ray hits geometry, the already-lit scene
// color there is the radiance bouncing back toward this surface. The sunlit floor thus
// lifts the shadowed column bases nearby - the directional fill a flat ambient can't give.
//
// This pass only GATHERS; the noise it produces is cleaned by the temporal accumulation and
// spatial denoise passes that run after it. Screen-space only: off-screen and occluded
// surfaces contribute nothing, and the result fades out near the screen edges.

uniform sampler2D colorTexture;   // lit HDR scene (the bounce source)
uniform sampler2D depthTexture;
uniform sampler2D normalTexture;  // world normal (0..1) + roughness/metal flag in alpha
uniform sampler2D historyColor;   // last frame's COMBINED GI fill, for the multi-bounce feed

// Sky fallback for rays that miss every on-screen surface (or exit the screen). Without it a
// miss contributes ZERO, so GI collapses whenever the bright source scrolls off-screen - the
// "works in some positions, dead in others" problem. Sampling the prefiltered environment
// along the missed ray turns those rays into a directionally-OCCLUDED sky gather (rays that
// hit nearby dark geometry still correctly return that geometry's radiance), so SSGI degrades
// into bent-normal IBL instead of black. EnvMap radiance is pre-exposed via SkyExposure.
uniform samplerCube EnvironmentMap;
uniform mat4 SkyRotation;         // same rotation the skybox/IBL sampling uses
uniform float SkyExposure;        // sky luminance scale x camera pre-exposure
uniform float SkyFallback;        // 0..1 blend of the miss-ray sky contribution
uniform float MaxEnvMip;          // roughest prefiltered mip (diffuse-ish cone)

uniform mat4 Projection;          // unjittered camera projection
uniform mat4 InvProjection;
uniform mat4 ViewMatrix;
uniform int FrameIndex;           // rotates the sample pattern each frame
uniform int RayCount;             // active rays this frame (<= MAX_RAYS)

// Artistic / quality controls.
uniform float RayLength;          // max march distance in metres (near vs far bounce)
uniform float Falloff;            // distance falloff exponent; >0 favours nearby bounce
uniform float Thickness;          // depth-test tolerance (thin = strict, thick = forgiving)
uniform float MultiBounce;        // 0..1: how much of last frame's GI re-bounces this frame
uniform float BounceBoost;        // amplify bright hits for a richer "final gather" feel

const int MAX_RAYS = 16;          // compile-time loop bound; RayCount gates it at runtime
const int MARCH_STEPS = 16;
const int REFINE_STEPS = 4;
const float PI = 3.14159265359;
const float FIREFLY_KNEE = 6.0;   // per-ray radiance luma cap (hits AND sky misses)

vec3 ViewPos(vec2 uv) {
    float depth = texture(depthTexture, uv).r;
    vec4 ndc = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 view = InvProjection * ndc;
    return view.xyz / view.w;
}

vec2 ToUV(vec3 viewPos, out float w) {
    vec4 clip = Projection * vec4(viewPos, 1.0);
    w = clip.w;
    return clip.xy / clip.w * 0.5 + 0.5;
}

float Hash(vec2 p) {
    return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453);
}

// Replace any NaN/Inf component with 0. The lit HDR scene (and the multi-bounce history fed
// from it) can carry a NaN/Inf from an EXR sun or a degenerate specular highlight; gathered
// into `bounce` it is STICKY - min/max/clamp propagate it and the temporal EMA then carries
// the bad pixel forever, which is exactly the "weirdly noisy" black/white speckle and the
// occasional crash. Kill it at every HDR read so nothing downstream can spread it.
vec3 Sanitize(vec3 v) {
    return mix(v, vec3(0.0), vec3(isnan(v.x) || isinf(v.x),
                                  isnan(v.y) || isinf(v.y),
                                  isnan(v.z) || isinf(v.z)));
}

// Build an orthonormal basis around n (Duff et al., branchless).
mat3 BasisFromNormal(vec3 n) {
    float s = n.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (s + n.z);
    float b = n.x * n.y * a;
    vec3 t = vec3(1.0 + s * n.x * n.x * a, s * b, -s * n.x);
    vec3 bt = vec3(b, s + n.y * n.y * a, -n.y);
    return mat3(t, bt, n);
}

// Cosine-weighted hemisphere sample in tangent space (concentric disk, lifted to z).
vec3 CosineSample(vec2 u) {
    float r = sqrt(u.x);
    float phi = 2.0 * PI * u.y;
    return vec3(r * cos(phi), r * sin(phi), sqrt(max(0.0, 1.0 - u.x)));
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

    vec3 P = ViewPos(TexCoords);
    vec3 N = normalize(mat3(ViewMatrix) * worldN);
    mat3 basis = BasisFromNormal(N);

    float rayLength = max(RayLength, 0.1);
    float stepLength = rayLength / float(MARCH_STEPS);

    // Per-pixel rotation of the sample set, advanced each frame so temporal can resolve it.
    float noise = Hash(TexCoords * vec2(textureSize(depthTexture, 0)) + float(FrameIndex) * 1.618);

    int rays = clamp(RayCount, 1, MAX_RAYS);
    vec3 bounce = vec3(0.0);
    float weightSum = 0.0;

    for (int i = 0; i < MAX_RAYS; i++) {
        if (i >= rays)
            break;
        vec2 u = vec2(
            fract((float(i) + 0.5) / float(rays) + noise),
            fract(noise * 1.7 + float(i) * 0.37));
        // Bias u.x off the extremes: at u.x ~= 1 CosineSample's z-> 0 and the disk radius -> 1,
        // so basis*sample can be ~0 and normalize() returns NaN (a speckle seed). Clamp keeps
        // every sample a well-defined hemisphere direction.
        u.x = clamp(u.x, 1e-3, 0.999);
        vec3 dir = normalize(basis * CosineSample(u));

        vec3 rayPos = P + N * 0.05;
        vec3 prevPos = rayPos;
        bool hit = false;
        vec2 hitUV = vec2(0.0);
        float hitDist = 0.0;

        for (int s = 0; s < MARCH_STEPS; s++) {
            prevPos = rayPos;
            rayPos += dir * stepLength;

            float w;
            vec2 uv = ToUV(rayPos, w);
            if (w <= 0.0 || uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
                break;

            float sceneZ = ViewPos(uv).z;
            // Tight thickness: the old (2*step + Thickness ~ 2m) window accepted any ray that
            // slid behind ANY visible surface, so almost every ray "hit" and gathered the
            // visible front's color — the whole frame got a scene-average GRAY VEIL instead of
            // distinct local bounce.
            float thick = Thickness + stepLength * 0.5;
            if (sceneZ > rayPos.z + 0.01 && sceneZ - rayPos.z < thick) {
                vec3 lo = prevPos;
                vec3 hi = rayPos;
                for (int r = 0; r < REFINE_STEPS; r++) {
                    vec3 mid = (lo + hi) * 0.5;
                    float mw;
                    vec2 midUV = ToUV(mid, mw);
                    if (ViewPos(midUV).z > mid.z + 0.01)
                        hi = mid;
                    else
                        lo = mid;
                }
                float dummy;
                vec2 candidateUV = ToUV(hi, dummy);

                // FRONT-FACE CHECK: a real bounce surface faces the incoming ray. A "hit" on a
                // surface pointing AWAY from the ray means we slid behind geometry the camera
                // sees (or grazed our own surface) — its on-screen color is the wrong radiance.
                // Reject and KEEP MARCHING: behind thin geometry the ray emerges and can still
                // hit something real further along.
                vec3 hitN = normalize(mat3(ViewMatrix) *
                    (texture(normalTexture, candidateUV).rgb * 2.0 - 1.0));
                if (dot(hitN, dir) <= -0.05) {
                    hitUV = candidateUV;
                    hitDist = length(hi - P);
                    hit = true;
                    break;
                }
            }
        }

        if (hit) {
            // Distance falloff: nearer bounce surfaces contribute more (Falloff shapes the
            // curve; 0 = no falloff). Keeps GI local and stops far walls flooding the frame.
            float fade = pow(clamp(1.0 - hitDist / rayLength, 0.0, 1.0), max(Falloff, 0.0));

            // Incoming radiance = the lit color at the hit, plus a fraction of the GI that
            // already accumulated there last frame (the cheap multi-bounce: light that
            // bounced once last frame bounces again this frame, compounding richness).
            vec3 radiance = Sanitize(texture(colorTexture, hitUV).rgb)
                          + Sanitize(texture(historyColor, hitUV).rgb) * MultiBounce;

            // Boost bright hits so strong indirect (a sunlit wall) reads richer, Lumen-like.
            radiance *= 1.0 + BounceBoost * dot(radiance, vec3(0.333));

            // FIREFLY CLAMP - only the OUTLIERS. Leave normal bright bounce intact and only
            // soft-cap genuine sparkle: a hard knee well above plausible diffuse radiance.
            float hitLum = dot(radiance, vec3(0.2126, 0.7152, 0.0722));
            if (hitLum > FIREFLY_KNEE)
                radiance *= FIREFLY_KNEE / hitLum;

            bounce += radiance * fade;
        }
        else if (SkyFallback > 0.0) {
            // MISS -> sky fallback. The ray saw no on-screen geometry, so the best estimate of
            // the incoming radiance is the environment along its direction (roughest prefiltered
            // mip ~ a diffuse cone), not zero. This is what keeps GI alive when the bright
            // source scrolls off-screen: the gather degrades into a directionally-occluded sky
            // integral instead of collapsing to black. Clamped by the same firefly knee so a
            // sun disk through a window can't speckle the accumulation.
            vec3 worldDir = transpose(mat3(ViewMatrix)) * dir;
            vec3 skyDir = transpose(mat3(SkyRotation)) * worldDir;
            vec3 sky = Sanitize(textureLod(EnvironmentMap, skyDir, MaxEnvMip).rgb) * SkyExposure;
            float skyLum = dot(sky, vec3(0.2126, 0.7152, 0.0722));
            if (skyLum > FIREFLY_KNEE)
                sky *= FIREFLY_KNEE / skyLum;
            bounce += sky * SkyFallback;
        }
        weightSum += 1.0;   // every ray contributes now (hit radiance or sky fallback)
    }

    // Pure per-ray mean: with the sky fallback there are no zero rays anymore, so the plain
    // cosine-weighted Monte-Carlo average is both unbiased AND position-stable - no hit-count
    // re-weighting hacks needed (those existed only to compensate for misses counting as 0).
    bounce /= max(weightSum, 1.0);

    vec2 edge = min(TexCoords, 1.0 - TexCoords);
    float edgeFade = smoothstep(0.0, 0.06, min(edge.x, edge.y));

    FragColor = vec4(Sanitize(bounce), edgeFade);
}
