#version 330 core

in vec2 TexCoords;
out vec4 FragColor; // rgb = accumulated GI, a = history length (frames, for the denoiser)

// Temporal accumulation: the single biggest noise win. This frame's raw gather is a noisy
// 1-spp-ish estimate; over many frames the noise averages to the true value. We reproject
// last frame's accumulated GI to this frame using the previous view-projection matrix and
// the current depth, reject samples that don't belong (disocclusion / fast motion) via a
// neighborhood colour clamp, then blend with an exponential moving average. Result: clean
// GI from very few rays, because the effective sample count grows with the history length.

uniform sampler2D currentGI;      // this frame's raw gather (noisy)
uniform sampler2D historyGI;      // last frame's accumulated result (rgb) + history len (a)
uniform sampler2D depthTexture;
uniform sampler2D normalTexture;

uniform mat4 InvProjection;       // current
uniform mat4 InvViewMatrix;       // current (view -> world)
uniform mat4 PrevViewProjection;  // last frame's world -> clip
uniform bool HasHistory;          // false on the first frame / after a resize
uniform float MaxHistory;         // cap on accumulated frames (higher = smoother, laggier)

vec3 WorldPos(vec2 uv) {
    float depth = texture(depthTexture, uv).r;
    vec4 ndc = vec4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
    vec4 view = InvProjection * ndc;
    view /= view.w;
    return (InvViewMatrix * vec4(view.xyz, 1.0)).xyz;
}

// NaN/Inf -> 0. A bad value in the accumulated history is the worst kind: the EMA carries it
// every frame, so one contaminated tap becomes a permanent black/white speckle. Scrub both the
// incoming gather and the reprojected history so the accumulation can never hold a bad pixel.
vec3 Sanitize(vec3 v) {
    return mix(v, vec3(0.0), vec3(isnan(v.x) || isinf(v.x),
                                  isnan(v.y) || isinf(v.y),
                                  isnan(v.z) || isinf(v.z)));
}

void main() {
    vec4 current = texture(currentGI, TexCoords);
    current.rgb = Sanitize(current.rgb);
    float depth = texture(depthTexture, TexCoords).r;

    // Sky: nothing to accumulate.
    if (depth >= 1.0) {
        FragColor = vec4(current.rgb, 1.0);
        return;
    }

    // Reproject this pixel into last frame's screen via its world position.
    vec3 worldPos = WorldPos(TexCoords);
    vec4 prevClip = PrevViewProjection * vec4(worldPos, 1.0);
    vec2 prevUV = prevClip.xy / prevClip.w * 0.5 + 0.5;

    // Off-screen last frame, or no history yet: start fresh.
    bool valid = HasHistory && prevClip.w > 0.0 &&
                 prevUV.x >= 0.0 && prevUV.x <= 1.0 && prevUV.y >= 0.0 && prevUV.y <= 1.0;

    if (!valid) {
        FragColor = vec4(current.rgb, 1.0);
        return;
    }

    vec4 history = texture(historyGI, prevUV);
    history.rgb = Sanitize(history.rgb);

    // Neighborhood clamp - LOOSENED for a sparse 1-spp signal. A TAA-style hard clamp to the
    // raw 3x3 box is BISTABLE for GI: with 4 rays at half res, many pixels have frames where
    // all nine taps miss, the box collapses to [0,0], and clamp() ZEROES the accumulated
    // history outright. Two attractors result: "history survives -> converges to the full
    // bright GI image" vs "history gets murdered every frame -> only raw speckles remain" -
    // the maddening sometimes-works-sometimes-pitch-black behaviour. Expanding the box around
    // its centre (3x) with an epsilon floor lets history RIDE THROUGH miss-frames - the EMA
    // then decays it gently (~1/histLen per frame) instead of executing it - while a genuinely
    // disoccluded surface still drifts outside even the widened box and resets via the drift
    // test below.
    vec2 texel = 1.0 / vec2(textureSize(currentGI, 0));
    vec3 lo = current.rgb;
    vec3 hi = current.rgb;
    for (int x = -1; x <= 1; x++)
        for (int y = -1; y <= 1; y++) {
            vec3 c = texture(currentGI, TexCoords + vec2(x, y) * texel).rgb;
            lo = min(lo, c);
            hi = max(hi, c);
        }
    vec3 boxCenter = (lo + hi) * 0.5;
    vec3 boxExtent = (hi - lo) * 0.5 * 3.0 + vec3(0.03);
    lo = max(boxCenter - boxExtent, 0.0);
    hi = boxCenter + boxExtent;
    vec3 clampedHistory = clamp(history.rgb, lo, hi);

    // How far the history drifted from the colour box tells us how stale it is. We use this to
    // shorten accumulation on a genuine disocclusion - but ONLY then.
    //
    // The trap (this caused the "SSGI changes every frame / no accumulation" flicker): in a dim
    // region the noisy gather makes the 3x3 box (hi-lo) tiny, so dividing by it turns ordinary
    // per-frame sample noise into a huge `drift`, which reset histLen to ~1 every frame -> the
    // EMA threw its history away each frame -> constant flicker. The neighbourhood CLAMP above
    // already rejects bad history; this drift term was double-jeopardy that misfired on noise.
    //
    // Fix: floor the box size so noise can't make the denominator vanish, and only let LARGE
    // drift (a real disocclusion, not sampling jitter) shorten history - and even then softly.
    float boxSize = max(length(hi - lo), 0.04);            // noise floor on the box
    float drift = length(history.rgb - clampedHistory) / boxSize;
    float reset = smoothstep(1.5, 4.0, drift);             // only real disocclusion resets
    float histLen = valid ? history.a : 1.0;
    histLen = mix(histLen, 1.0, reset);                    // shorten history only on big drift
    histLen = min(histLen + 1.0, MaxHistory);

    // Exponential moving average: blend weight = 1/histLen, so early frames trust the new
    // sample and later frames barely move (the accumulation converges and stays stable).
    float alpha = 1.0 / histLen;
    vec3 accumulated = mix(clampedHistory, current.rgb, alpha);

    // Firefly suppression on the ACCUMULATED value: even with the neighbourhood clamp a lone
    // hot tap can seed a bright speck that the history then carries for many frames (the cyan
    // sparkles in the dark). Cap the accumulated luma to a small multiple of the local
    // neighbourhood max so one pixel can't stay far brighter than its surroundings; rescale by
    // luma to preserve hue. This is a temporal-domain firefly kill the spatial denoiser can't do.
    float accLuma = dot(accumulated, vec3(0.2126, 0.7152, 0.0722));
    float maxLuma = dot(hi, vec3(0.2126, 0.7152, 0.0722)) * 1.5 + 0.05;
    if (accLuma > maxLuma)
        accumulated *= maxLuma / max(accLuma, 1e-4);

    FragColor = vec4(accumulated, histLen);
}
