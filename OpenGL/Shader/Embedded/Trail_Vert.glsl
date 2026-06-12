#version 330 core

// Ribbon vertices are pre-built camera-facing in world space by TrailRenderer.BuildRibbon, so the
// vertex stage is a plain world->clip transform. Locations 0-2 (no instancing, no reserved-attrib
// collision — this is a non-instanced strip with its own VAO).
layout(location = 0) in vec3 aPosition;
layout(location = 1) in vec2 aUv;
layout(location = 2) in vec4 aColor;

out vec2 uv;
out vec4 color;

uniform mat4 view;
uniform mat4 projection;

void main() {
    uv = aUv;
    color = aColor;
    gl_Position = projection * view * vec4(aPosition, 1.0);
}
