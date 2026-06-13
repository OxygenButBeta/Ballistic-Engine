#version 330 core

in vec2 TexCoords;
out vec4 FragColor; // rgb = fogged scene

// Depth-aware (bilateral) upsample of the half-res volumetric fog to full res, then
// composite over the lit scene: scene * transmittance + in-scatter. The extinction term is
// what makes it READ as fog - distant geometry and the horizon sink into the airlight
// instead of glowing through it. Bilateral weighting stops the low-res fog from bleeding
// across depth edges (the classic half-res god-ray halo around objects).

uniform sampler2D sceneTexture;    // full-res lit HDR color
uniform sampler2D scatterTexture;  // half-res accumulated fog (rgb scatter, a transmittance)
uniform sampler2D depthTexture;    // full-res scene depth
uniform sampler2D scatterDepth;    // NOTE: same depth, sampled at the half-res tap UVs

uniform mat4  InvProjection;
uniform float Intensity;           // master strength: scales scatter, eases extinction below 1
uniform vec3  Tint;                // color grade for the in-scatter (not the extinction)
uniform bool  Extinguish;          // true = real fog (scene * transmittance); false = additive shafts only

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

    // 2x2 bilateral tap of the half-res fog, weighted by depth similarity.
    vec2 texel = 1.0 / vec2(textureSize(scatterTexture, 0));
    vec2 offs[4] = vec2[4](
        vec2(-0.5, -0.5), vec2(0.5, -0.5), vec2(-0.5, 0.5), vec2(0.5, 0.5)
    );

    vec4 fog = vec4(0.0);
    float wsum = 0.0;
    for (int i = 0; i < 4; i++) {
        vec2 uv = TexCoords + offs[i] * texel;
        float lz = LinearDepth(texture(scatterDepth, uv).r);
        float w = 1.0 / (abs(lz - centerZ) * 0.5 + 1e-3);
        fog += texture(scatterTexture, uv) * w;
        wsum += w;
    }
    fog /= max(wsum, 1e-4);

    vec3 scatter = max(fog.rgb, vec3(0.0)) * Intensity * Tint;
    // Intensity < 1 backs the extinction off toward "no fog" in step with the scatter, so
    // the master dial fades the WHOLE effect; above 1 it only boosts the glow (extinguishing
    // more than the physical transmittance would punch black halos into the scene).
    // Shafts-only mode (Extinguish == false) keeps the scene fully visible and just ADDS the
    // beams — they're a glow, not a medium that hides geometry.
    float transmittance = Extinguish
        ? mix(1.0, clamp(fog.a, 0.0, 1.0), clamp(Intensity, 0.0, 1.0))
        : 1.0;

    // Energy-conserving fog composite. The march already bounded the scatter (it can never
    // exceed the source radiance), so no rolloff tricks; ACES downstream handles highlights.
    FragColor = vec4(scene * transmittance + scatter, 1.0);
}
