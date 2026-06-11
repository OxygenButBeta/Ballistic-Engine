#version 330 core

in vec2 TexCoords;
out vec4 FragColor; // rgb = reflected scene color, a = blend strength

// View-space screen-space reflections: march the reflection ray against the depth
// buffer; where it hits, the scene color replaces the (sky-only) IBL reflection.

uniform sampler2D colorTexture;
uniform sampler2D depthTexture;
uniform sampler2D normalTexture;  // world normal (0..1) + roughness

uniform mat4 Projection;          // unjittered camera projection
uniform mat4 InvProjection;
uniform mat4 ViewMatrix;
uniform float Intensity;

const int MARCH_STEPS = 32;
const int REFINE_STEPS = 5;
const float MAX_DISTANCE = 60.0;
const float MAX_ROUGHNESS = 0.6;

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

void main() {
    float depth = texture(depthTexture, TexCoords).r;
    vec4 nr = texture(normalTexture, TexCoords);
    // Alpha packs roughness + 2.0 when the surface is metallic.
    bool isMetal = nr.a >= 1.5;
    float roughness = nr.a - (isMetal ? 2.0 : 0.0);
    vec3 worldN = nr.rgb * 2.0 - 1.0;

    // Sky, un-shaded pixels, or rough surfaces: no SSR.
    if (depth >= 1.0 || dot(worldN, worldN) < 0.1 || roughness > MAX_ROUGHNESS) {
        FragColor = vec4(0.0);
        return;
    }

    vec3 P = ViewPos(TexCoords);
    vec3 N = normalize(mat3(ViewMatrix) * worldN);
    vec3 Vdir = normalize(P); // from camera toward the point
    vec3 R = normalize(reflect(Vdir, N));

    // March in view space.
    float stepLength = MAX_DISTANCE / float(MARCH_STEPS);
    vec3 rayPos = P + N * 0.05;
    vec3 prevPos = rayPos;
    float hit = 0.0;
    vec2 hitUV = vec2(0.0);

    for (int i = 0; i < MARCH_STEPS; i++) {
        prevPos = rayPos;
        rayPos += R * stepLength;

        float w;
        vec2 uv = ToUV(rayPos, w);
        if (w <= 0.0 || uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
            break;

        float sceneZ = ViewPos(uv).z;
        float thickness = stepLength * 2.0 + 0.3;
        if (sceneZ > rayPos.z + 0.01 && sceneZ - rayPos.z < thickness) {
            // Binary refine between prevPos and rayPos.
            vec3 lo = prevPos;
            vec3 hi = rayPos;
            for (int r = 0; r < REFINE_STEPS; r++) {
                vec3 mid = (lo + hi) * 0.5;
                vec2 midUV = ToUV(mid, w);
                if (ViewPos(midUV).z > mid.z + 0.01)
                    hi = mid;
                else
                    lo = mid;
            }
            float dummy;
            hitUV = ToUV(hi, dummy);
            hit = 1.0;
            break;
        }
    }

    if (hit < 0.5) {
        FragColor = vec4(0.0);
        return;
    }

    // Fades: screen edges and the roughness tail.
    vec2 edge = min(hitUV, 1.0 - hitUV);
    float edgeFade = smoothstep(0.0, 0.08, min(edge.x, edge.y));
    float roughFade = 1.0 - smoothstep(0.3, MAX_ROUGHNESS, roughness);

    // Physical Schlick weight: dielectrics reflect ~4% except at grazing angles, metals
    // strongly. Anything more turns every street into a rained-on mirror.
    float F0 = isMetal ? 0.6 : 0.04;
    float NdotV = max(dot(N, -Vdir), 0.0);
    float fresnel = F0 + (1.0 - F0) * pow(1.0 - NdotV, 5.0);

    // --- Grazing x roughness suppression. The raw Schlick term spikes toward 1.0 at grazing
    // angles even on a matte floor (NdotV -> 0), which is exactly the shallow ground viewing
    // angle - it made the rough tile floor read as a wet mirror once SSGI lifted the shadows
    // enough to reveal it. A rough surface should NOT get a sharp grazing mirror, so fold the
    // grazing Fresnel boost down by roughness: smooth surfaces keep their grazing reflection,
    // rough ones lose it and only the head-on ~4% survives. ---
    float grazing = fresnel - F0;                       // the angle-dependent part
    float grazeKeep = 1.0 - smoothstep(0.05, 0.45, roughness);
    fresnel = F0 + grazing * grazeKeep;

    // --- Energy gate: a reflection should never be brighter than what it reflects relative to
    // the surface itself. On a dark/shadowed floor the reflected sunlit geometry would pop as
    // an out-of-place bright streak. Damp the reflection where the underlying surface is dark
    // so SSR can't out-shine the shadow it sits in. ---
    vec3 reflected = texture(colorTexture, hitUV).rgb;
    float surfaceLum = dot(texture(colorTexture, TexCoords).rgb, vec3(0.2126, 0.7152, 0.0722));
    float lowLightDamp = smoothstep(0.0, 0.08, surfaceLum);   // ~0 in deep shadow, 1 when lit

    float strength = clamp(fresnel * Intensity, 0.0, 1.0) * edgeFade * roughFade * lowLightDamp;
    FragColor = vec4(reflected, strength);
}
