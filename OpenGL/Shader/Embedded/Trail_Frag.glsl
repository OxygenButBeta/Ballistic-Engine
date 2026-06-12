#version 330 core

in vec2 uv;
in vec4 color;

out vec4 fragColor;

uniform sampler2D Trail;
uniform bool HasTexture;

void main() {
    vec4 tex = HasTexture ? texture(Trail, uv) : vec4(1.0);
    fragColor = tex * color;
    if (fragColor.a < 0.003)
        discard;
}
