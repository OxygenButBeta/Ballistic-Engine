#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D aoTexture;

void main() {
    vec2 t = 1.0 / vec2(textureSize(aoTexture, 0));
    float sum = 0.0;
    for (int x = -2; x < 2; x++)
        for (int y = -2; y < 2; y++)
            sum += texture(aoTexture, TexCoords + vec2(float(x) + 0.5, float(y) + 0.5) * t).r;

    FragColor = vec4(vec3(sum / 16.0), 1.0);
}
