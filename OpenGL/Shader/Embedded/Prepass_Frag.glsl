#version 330 core
in vec2 texCoord;
uniform bool AlphaCutout;
uniform sampler2D Diffuse;

void main() {
    if (AlphaCutout && texture(Diffuse, texCoord).a < 0.5)
        discard;
}
