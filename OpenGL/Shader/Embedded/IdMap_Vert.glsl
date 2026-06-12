#version 460 core

// Entity-ID pass: plain position transform with one combined matrix (mvp = model * view *
// projection built CPU-side in OpenTK row-vector order, so GLSL applies it as mvp * pos).
layout(location = 0) in vec3 aPosition;

uniform mat4 mvp;

void main() {
    gl_Position = mvp * vec4(aPosition, 1.0);
}
