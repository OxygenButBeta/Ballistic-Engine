#version 330 core
in vec2 uv;

uniform bool AlphaCutout;
uniform sampler2D Diffuse;

void main() {
    if (AlphaCutout && texture(Diffuse, uv).a < 0.5)
        discard;
}
