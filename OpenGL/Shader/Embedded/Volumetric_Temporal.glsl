#version 330 core

in vec2 TexCoords;
out vec4 FragColor; // rgb = accumulated scatter, a = accumulated transmittance; next frame's history

// Temporal accumulation for the half-res volumetric scatter. The raymarch is dithered, so
// a single frame is noisy; reprojecting last frame's result and blending it in removes the
// noise the way SSGI_Temporal / TAA do. Volumetrics are low-frequency, so a gentle
// neighborhood clamp is enough to keep moving shafts from ghosting. The fog transmittance
// rides in alpha and is filtered exactly like the scatter (it is just as dithered).

uniform sampler2D currentScatter;  // this frame's noisy half-res march (rgb scatter, a trans)
uniform sampler2D historyScatter;  // last frame's accumulated half-res result
uniform sampler2D depthTexture;    // full-res scene depth (sampled at half-res UVs)

uniform mat4 InvProjection;
uniform mat4 InvViewMatrix;
uniform mat4 PrevViewProjection;   // last frame's view*projection (world -> prev clip)

uniform bool  HasHistory;          // false on the first frame / after a resize
uniform float Feedback;            // history weight, ~0.9 (higher = smoother, more lag)

vec3 WorldPos(vec2 uv, float depth) {
    vec4 ndc = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 viewPos = InvProjection * ndc;
    viewPos /= viewPos.w;
    vec4 world = InvViewMatrix * viewPos;
    return world.xyz;
}

void main() {
    vec4 current = texture(currentScatter, TexCoords);

    if (!HasHistory) {
        FragColor = current;
        return;
    }

    // Reproject this pixel into last frame using its world position.
    float depth = texture(depthTexture, TexCoords).r;
    vec3 worldPos = WorldPos(TexCoords, min(depth, 0.99999));
    vec4 prevClip = PrevViewProjection * vec4(worldPos, 1.0);
    vec2 prevUV = (prevClip.xy / prevClip.w) * 0.5 + 0.5;

    // Off-screen reprojection (disocclusion / camera turned): fall back to the current sample.
    if (prevClip.w <= 0.0 || any(lessThan(prevUV, vec2(0.0))) || any(greaterThan(prevUV, vec2(1.0)))) {
        FragColor = current;
        return;
    }

    vec4 history = texture(historyScatter, prevUV);

    // Soft neighborhood clamp: build a 3x3 min/max box of the current frame and clamp the
    // reprojected history into it (expanded a little so smooth gradients aren't flattened).
    vec4 lo = current;
    vec4 hi = current;
    vec2 texel = 1.0 / vec2(textureSize(currentScatter, 0));
    for (int x = -1; x <= 1; x++) {
        for (int y = -1; y <= 1; y++) {
            vec4 s = texture(currentScatter, TexCoords + vec2(x, y) * texel);
            lo = min(lo, s);
            hi = max(hi, s);
        }
    }
    vec4 ext = (hi - lo) * 0.5 + 1e-4;
    history = clamp(history, lo - ext, hi + ext);

    FragColor = mix(current, history, clamp(Feedback, 0.0, 0.98));
}
