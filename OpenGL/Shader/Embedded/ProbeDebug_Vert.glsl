#version 330 core

layout(location = 0) in vec3 aPosition; // unit sphere; position doubles as the normal

uniform mat4 view;
uniform mat4 projection;
uniform vec3 Center;
uniform float Radius;

out vec3 normal;

void main() {
    normal = aPosition;
    vec3 world = Center + aPosition * Radius;
    gl_Position = projection * view * vec4(world, 1.0);
}
