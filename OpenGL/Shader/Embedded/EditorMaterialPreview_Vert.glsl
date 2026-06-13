#version 330 core
layout(location = 0) in vec3 pos;
layout(location = 1) in vec3 normal;
layout(location = 2) in vec2 uv;
uniform mat4 mvp;
out vec3 n;
out vec2 vUv;
void main() { gl_Position = mvp * vec4(pos, 1.0); n = normal; vUv = uv; }
