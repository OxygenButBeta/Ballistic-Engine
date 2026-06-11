#version 330 core

in vec2 TexCoords;
out vec4 FragColor; // rgb = temporally accumulated scatter, becomes next frame's history

// Temporal accumulation for the half-res volumetric scatter. The raymarch is dithered, so
// a single frame is noisy; reprojecting last frame's result and blending it in removes the
// noise the way SSGI_Temporal / TAA do. Volumetrics are low-frequency, so a gentle
// neighborhood clamp is enough to keep moving shafts from ghosting.

uniform sampler2D currentScatter;  // this frame's noisy half-res march
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
    vec3 current = texture(currentScatter, TexCoords).rgb;

    if (!HasHistory) {
        FragColor = vec4(current, 1.0);
        return;
    }

    // Reproject this pixel into last frame using its world position.
    float depth = texture(depthTexture, TexCoords).r;
    vec3 worldPos = WorldPos(TexCoords, min(depth, 0.99999));
    vec4 prevClip = PrevViewProjection * vec4(worldPos, 1.0);
    vec2 prevUV = (prevClip.xy / prevClip.w) * 0.5 + 0.5;

    // Off-screen reprojection (disocclusion / camera turned): fall back to the current sample.
    if (prevClip.w <= 0.0 || any(lessThan(prevUV, vec2(0.0))) || any(greaterThan(prevUV, vec2(1.0)))) {
        FragColor = vec4(current, 1.0);
        return;
    }

    vec3 history = texture(historyScatter, prevUV).rgb;

    // Soft neighborhood clamp: build a 3x3 min/max box of the current frame and clamp the
    // reprojected history into it (expanded a little so smooth gradients aren't flattened).
    vec3 lo = current;
    vec3 hi = current;
    vec2 texel = 1.0 / vec2(textureSize(currentScatter, 0));
    for (int x = -1; x <= 1; x++) {
        for (int y = -1; y <= 1; y++) {
            vec3 s = texture(currentScatter, TexCoords + vec2(x, y) * texel).rgb;
            lo = min(lo, s);
            hi = max(hi, s);
        }
    }
    vec3 ext = (hi - lo) * 0.5 + 1e-4;
    history = clamp(history, lo - ext, hi + ext);

    vec3 blended = mix(current, history, clamp(Feedback, 0.0, 0.98));
    FragColor = vec4(blended, 1.0);
}
