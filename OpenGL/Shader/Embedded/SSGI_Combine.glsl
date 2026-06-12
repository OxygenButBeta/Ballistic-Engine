#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

// SSGI composite - now a BOUNDED REFINEMENT on a physical IBL base, not the primary fill.
//
// The ambient base is real measured environment light (split-sum IBL irradiance, applied in
// the forward shader), so shadows are already lifted correctly before this pass runs. SSGI's
// only remaining job is to add the *local* one-bounce colour the distant IBL can't know about:
// the red marble throwing red onto the floor beside it, the sunlit slab lifting the wall it
// faces. That's a small additive term, so this shader is deliberately minimal - no ambient
// floor (the IBL is the floor), no bounce boost, no frame-wide regrade. All the artefacts those
// hacks caused (grey wash, blue/cyan shadow sparkles, desaturation) are gone with them.
//
// `Look` survives only as a gentle strength/warmth on the local bounce, energy-weighted so it
// can never colour a near-black pixel.

uniform sampler2D sceneTexture;
uniform sampler2D ssgiTexture;    // denoised one-bounce GI (rgb)
uniform sampler2D aoTexture;      // r = ambient occlusion (1 = unoccluded)
uniform sampler2D normalTexture;

uniform bool ApplyAO;
uniform bool DebugView;           // show ONLY the graded bounce (10x), not scene+bounce
uniform float Look;               // gentle look strength on the LOCAL bounce (0..1)
uniform float Intensity;          // master strength of the SSGI bounce (advanced)
uniform vec3 Tint;                // bounce colour multiplier (advanced)
uniform float Saturation;         // bounce colour punch (advanced)
uniform float OcclusionPower;     // how hard AO bites the bounce (advanced)
uniform float EdgeFade;           // screen-edge confidence (carried from the gather)
uniform float AmbientFloor;       // retained uniform (unused now IBL is the base); kept for ABI
uniform float MultiBounceUnused;  // (placeholder - combine takes no multibounce)

const vec3 LUMA = vec3(0.2126, 0.7152, 0.0722);
const vec3 WARM = vec3(1.05, 1.00, 0.92);

void main() {
    vec3 scene = texture(sceneTexture, TexCoords).rgb;
    // NaN-safe: max(NaN, 0) is implementation-defined (often NaN), and a NaN here paints a black/
    // white speckle that scene+add carries — the final gate against the "weirdly noisy" output.
    // True component SELECT - the old mix(gi, 0, flag) form was arithmetic
    // (gi*(1-flag) + 0*flag) and NaN*0.0 == NaN, so it never actually scrubbed.
    vec3 gi = texture(ssgiTexture, TexCoords).rgb;
    gi = vec3(isnan(gi.x) || isinf(gi.x) ? 0.0 : gi.x,
              isnan(gi.y) || isinf(gi.y) ? 0.0 : gi.y,
              isnan(gi.z) || isinf(gi.z) ? 0.0 : gi.z);
    gi = max(gi, 0.0);

    float look = clamp(Look, 0.0, 1.0);
    float ao = ApplyAO ? clamp(texture(aoTexture, TexCoords).r, 0.0, 1.0) : 1.0;
    float giLuma = dot(gi, LUMA);

    // Energy gate: everything coloured below is weighted by how much real bounce exists, so a
    // noisy near-black pixel stays neutral (no blue/cyan sparkle, no grey wash).
    float energy = smoothstep(0.02, 0.25, giLuma);

    // Gentle saturation + warmth on the local bounce, energy-gated.
    float sat = mix(1.0, Saturation * (1.0 + 0.3 * look), energy);
    gi = mix(vec3(giLuma), gi, clamp(sat, 0.0, 2.0));
    gi = mix(gi, gi * WARM, energy * look * 0.4);
    gi *= Tint * Intensity;

    // Occlusion bites the bounce (it's exactly the indirect light AO removes from creases).
    gi *= pow(ao, OcclusionPower);

    // Add the bounce on top of the already IBL-lit scene. Clamp so one bright denoised tap
    // can't pop as a firefly. No ambient floor: the physical IBL is the base fill now.
    vec3 add = clamp(gi, 0.0, 8.0) * EdgeFade;

    // Debug: the bounce alone, brightened 10x so even a faint contribution reads. Black means
    // SSGI gathered nothing here (rays missed every lit surface, or the source is off-screen).
    if (DebugView) {
        FragColor = vec4(add * 10.0, 1.0);
        return;
    }

    FragColor = vec4(scene + add, 1.0);
}
