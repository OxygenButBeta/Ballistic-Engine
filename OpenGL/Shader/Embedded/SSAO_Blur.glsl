#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D aoTexture;
uniform sampler2D depthTexture;  // full-res depth (sampled at the AO's half-res UVs)
uniform mat4 InvProjection;

// DEPTH-AWARE (bilateral) blur. The old plain 4x4 box averaged across silhouettes, smearing AO over
// depth discontinuities (haloes around objects against the background). Weight each tap by how close
// its linear depth is to the centre's, so the blur denoises WITHIN a surface but stops at edges.
float LinearDepth(vec2 uv) {
    float d = texture(depthTexture, uv).r;
    vec4 v = InvProjection * vec4(0.0, 0.0, d * 2.0 - 1.0, 1.0);
    return v.z / v.w;
}

void main() {
    vec2 t = 1.0 / vec2(textureSize(aoTexture, 0));
    float centerZ = LinearDepth(TexCoords);

    float sum = 0.0;
    float wSum = 0.0;
    for (int x = -2; x <= 2; x++)
        for (int y = -2; y <= 2; y++) {
            vec2 uv = TexCoords + vec2(x, y) * t;
            float tapZ = LinearDepth(uv);
            // Depth-similarity weight: falls off fast past a small relative depth gap so the blur
            // never crosses a silhouette. Scaled by the centre depth so the tolerance is view-distance
            // relative (a 10cm gap matters up close, not at 50m).
            float w = exp(-abs(tapZ - centerZ) / max(0.05 * abs(centerZ), 0.02));
            sum += texture(aoTexture, uv).r * w;
            wSum += w;
        }

    FragColor = vec4(vec3(sum / max(wSum, 1e-4)), 1.0);
}
