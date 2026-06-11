#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D sceneTexture;
uniform sampler2D ssrTexture;   // HALF-RES reflections: rgb = reflection, a = strength
uniform sampler2D depthTexture; // full-res depth (same buffer the march tested against)
uniform mat4 InvProjection;

// Depth-aware upsample of the half-res SSR buffer: each of the 4 nearest half-res texels is
// weighted by its bilinear factor x depth similarity, so reflections don't bleed across
// silhouettes (the halo a plain bilinear upsample smears around edges).
float LinearDepth(float d) {
    vec4 v = InvProjection * vec4(0.0, 0.0, d * 2.0 - 1.0, 1.0);
    return v.z / v.w;
}

void main() {
    vec3 scene = texture(sceneTexture, TexCoords).rgb;

    vec2 ssrSize = vec2(textureSize(ssrTexture, 0));
    vec2 texel = 1.0 / ssrSize;
    vec2 pos = TexCoords * ssrSize - 0.5;
    vec2 base = (floor(pos) + 0.5) * texel;
    vec2 f = fract(pos);

    float centerZ = LinearDepth(texture(depthTexture, TexCoords).r);

    vec4 acc = vec4(0.0);
    float wSum = 0.0;
    for (int i = 0; i < 4; i++) {
        vec2 corner = vec2(float(i & 1), float(i >> 1));
        vec2 uv = base + corner * texel;
        float wBilinear = (corner.x > 0.5 ? f.x : 1.0 - f.x) * (corner.y > 0.5 ? f.y : 1.0 - f.y);
        float tapZ = LinearDepth(texture(depthTexture, uv).r);
        float wDepth = 1.0 / (1.0 + abs(tapZ - centerZ) * 2.0);
        float w = wBilinear * wDepth + 1e-5;
        acc += texture(ssrTexture, uv) * w;
        wSum += w;
    }
    vec4 ssr = acc / wSum;

    // The SSR hit replaces the sky-IBL reflection that's baked into the scene color;
    // lerping (rather than adding) avoids double-counting reflection energy.
    FragColor = vec4(mix(scene, ssr.rgb, ssr.a), 1.0);
}
