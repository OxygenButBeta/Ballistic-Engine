#version 330 core

in vec2 TexCoords;
out vec4 FragColor; // rgb = scene + upsampled scatter

// Depth-aware (bilateral) upsample of the half-res volumetric scatter to full res, then
// additively composite over the lit scene. Bilateral weighting stops the low-res scatter
// from bleeding across depth edges (the classic half-res god-ray halo around objects).

uniform sampler2D sceneTexture;    // full-res lit HDR color
uniform sampler2D scatterTexture;  // half-res accumulated scatter
uniform sampler2D depthTexture;    // full-res scene depth
uniform sampler2D scatterDepth;    // NOTE: same depth, sampled at the half-res tap UVs

uniform mat4  InvProjection;
uniform float Intensity;           // overall strength multiplier
uniform vec3  Tint;                // color grade for the shafts

// Linearize hardware depth to view-space distance for the bilateral weight.
float LinearDepth(float depth) {
    vec4 ndc = vec4(0.0, 0.0, depth * 2.0 - 1.0, 1.0);
    vec4 viewPos = InvProjection * ndc;
    return -(viewPos.z / viewPos.w);
}

void main() {
    vec3 scene = texture(sceneTexture, TexCoords).rgb;

    float fullDepth = texture(depthTexture, TexCoords).r;
    float centerZ = LinearDepth(fullDepth);

    // 2x2 bilateral tap of the half-res scatter, weighted by depth similarity.
    vec2 texel = 1.0 / vec2(textureSize(scatterTexture, 0));
    vec2 offs[4] = vec2[4](
        vec2(-0.5, -0.5), vec2(0.5, -0.5), vec2(-0.5, 0.5), vec2(0.5, 0.5)
    );

    vec3 scatter = vec3(0.0);
    float wsum = 0.0;
    for (int i = 0; i < 4; i++) {
        vec2 uv = TexCoords + offs[i] * texel;
        float lz = LinearDepth(texture(scatterDepth, uv).r);
        float w = 1.0 / (abs(lz - centerZ) * 0.5 + 1e-3);
        scatter += texture(scatterTexture, uv).rgb * w;
        wsum += w;
    }
    scatter /= max(wsum, 1e-4);
    scatter = max(scatter, vec3(0.0)) * Intensity * Tint;

    // The march produces a bounded scatter (lit fraction in [0,1] * phase shape in [0,1] *
    // Scattering), so a straight additive composite is stable here - no division, no rolloff
    // tricks that can themselves blow up. The downstream ACES tonemap handles the highlights.
    // Shafts add light to the air; they read as beams because the march already concentrated
    // the energy where the air is lit and sun-facing, not as a flat screen-wide lift.
    vec3 result = scene + scatter;

    FragColor = vec4(result, 1.0);
}
