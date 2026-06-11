#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

// Pre-exposed HDR scene color (post-TAA, pre-bloom).
uniform sampler2D sceneTexture;

// Auto-exposure meter downsample: each output texel of the small grid averages the LOG2
// luminance of a 4x4 tap pattern spread over its source cell (geometric mean = how exposure
// meters behave; a stray sun pixel shifts the result by its stops, not its raw magnitude).
// The CPU side reads this grid back, applies metering weights and converts to a target EV.
void main() {
    vec2 cellSize = 1.0 / vec2(64.0);

    float sum = 0.0;
    for (int y = 0; y < 4; y++)
    for (int x = 0; x < 4; x++) {
        vec2 offset = ((vec2(float(x), float(y)) + 0.5) / 4.0 - 0.5) * cellSize;
        vec3 color = texture(sceneTexture, TexCoords + offset).rgb;
        float lum = dot(color, vec3(0.2126, 0.7152, 0.0722));
        // Floor keeps log2 finite on black pixels (skybox-less void, vignetted corners).
        sum += log2(max(lum, 1e-5));
    }

    FragColor = vec4(sum / 16.0, 0.0, 0.0, 1.0);
}
