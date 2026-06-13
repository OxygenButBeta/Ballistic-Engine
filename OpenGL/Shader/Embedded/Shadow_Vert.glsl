#version 330 core
layout(location = 0) in vec3 position;
layout(location = 1) in vec2 aTexCoord;

out vec2 uv;

uniform mat4 model;
uniform mat4 lightSpaceMatrix;

void main() {
    uv = aTexCoord;
    gl_Position = lightSpaceMatrix * model * vec4(position, 1.0);
}
