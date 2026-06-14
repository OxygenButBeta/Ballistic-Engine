#version 330 core

in vec2 TexCoords;
layout(location = 0) out vec4 FragColor;  // rgb = accumulated reflection, a = strength (kept)
layout(location = 1) out float ViewDepth; // current view-space linear depth -> next frame's history

// Temporal accumulation for SSR. The raw march hit-point wobbles frame-to-frame: TAA jitters the
// depth buffer, so the 32-step march + binary refine lands on a slightly different texel each frame
// and the reflected colour shimmers (the user's "reflections flicker a little"). Reproject last
// frame's accumulated reflection by this pixel's world position, reject disoccluded history by
// view-depth (the SSGI temporal pattern), and EMA-blend so the reflection converges to a stable
// image while still tracking real motion. Reflections are higher-frequency than diffuse GI, so the
// disocclusion tolerance is tighter and a colour-clamp guards against ghosting on moving content.

uniform sampler2D currentSSR;     // this frame's raw march (rgb = reflection, a = strength)
uniform sampler2D historySSR;     // last frame's accumulated result (rgb) + strength (a)
uniform sampler2D historyDepth;   // last frame's view-space linear depth (for disocclusion)
uniform sampler2D depthTexture;   // current full-res depth (sampled at half-res UVs)

uniform mat4 InvProjection;       // current (clip -> view)
uniform mat4 InvViewMatrix;       // current (view -> world)
uniform mat4 PrevViewProjection;  // last frame's world -> clip (unjittered)
uniform bool HasHistory;          // false on the first frame / after a resize
uniform float MaxHistory;         // cap on accumulated frames (higher = smoother, laggier)

vec3 ViewPos(vec2 uv) {
    float depth = texture(depthTexture, uv).r;
    vec4 ndc = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 view = InvProjection * ndc;
    return view.xyz / view.w;
}

vec3 WorldPos(vec2 uv) {
    return (InvViewMatrix * vec4(ViewPos(uv), 1.0)).xyz;
}

vec3 Sanitize(vec3 v) {
    return vec3(isnan(v.x) || isinf(v.x) ? 0.0 : v.x,
                isnan(v.y) || isinf(v.y) ? 0.0 : v.y,
                isnan(v.z) || isinf(v.z) ? 0.0 : v.z);
}

void main() {
    vec4 current = texture(currentSSR, TexCoords);
    current.rgb = Sanitize(current.rgb);
    float depth = texture(depthTexture, TexCoords).r;

    float viewZ = ViewPos(TexCoords).z;
    ViewDepth = viewZ;

    // Sky / no surface: nothing to accumulate; pass through.
    if (depth >= 1.0) {
        FragColor = current;
        return;
    }

    // Reproject this pixel into last frame's screen via its world position.
    vec3 worldPos = WorldPos(TexCoords);
    vec4 prevClip = PrevViewProjection * vec4(worldPos, 1.0);
    vec2 prevUV = prevClip.xy / prevClip.w * 0.5 + 0.5;

    bool valid = HasHistory && prevClip.w > 0.0 &&
                 prevUV.x >= 0.0 && prevUV.x <= 1.0 && prevUV.y >= 0.0 && prevUV.y <= 1.0;

    // Depth disocclusion: if the surface that sat at prevUV last frame was at a different depth,
    // prevUV showed a DIFFERENT surface (a silhouette under motion) — reject and start fresh.
    if (valid) {
        float expectedPrevZ = -prevClip.w;                 // clip.w = -viewZ for perspective
        float storedPrevZ = texture(historyDepth, prevUV).r;
        float tol = max(0.03 * abs(expectedPrevZ), 0.03);  // tighter than GI (reflections are sharp)
        if (abs(storedPrevZ - expectedPrevZ) > tol)
            valid = false;
    }

    if (!valid) {
        FragColor = current;
        return;
    }

    vec4 history = texture(historySSR, prevUV);
    history.rgb = Sanitize(history.rgb);

    // Neighbourhood colour clamp (TAA-style) so a moving reflection doesn't ghost: clamp the
    // reprojected history to the local 3x3 colour box of the current march. Reflections are a
    // higher-frequency signal than diffuse GI, so a standard (un-widened) box is correct here —
    // it kills ghosting of a reflection sliding across the floor as the camera moves.
    vec2 texel = 1.0 / vec2(textureSize(currentSSR, 0));
    vec3 lo = current.rgb, hi = current.rgb;
    for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++) {
            vec3 c = Sanitize(texture(currentSSR, TexCoords + vec2(x, y) * texel).rgb);
            lo = min(lo, c);
            hi = max(hi, c);
        }
    vec3 clampedHistory = clamp(history.rgb, lo, hi);

    // EMA. history.a here reuses the strength channel's high bits is NOT done — we track frame
    // count separately is overkill for SSR; a fixed blend converges fast enough and stays simple.
    // Blend toward the current march by 1/MaxHistory; the clamp already rejects stale ghosts.
    float alpha = 1.0 / max(MaxHistory, 1.0);
    vec3 accumulated = mix(clampedHistory, current.rgb, alpha);

    // Strength: take the CURRENT frame's (it encodes this-frame fresnel/edge/rough fades, which
    // are view-dependent and shouldn't lag); only the reflected COLOUR is temporally smoothed.
    FragColor = vec4(Sanitize(accumulated), current.a);
}
