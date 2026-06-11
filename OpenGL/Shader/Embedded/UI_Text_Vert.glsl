#version 460 core

// UI text vertex shader: positions a single glyph quad in PANEL pixel space (top-left origin) and
// passes the glyph's atlas UV to the fragment stage. One draw per glyph in v1 (per-glyph uRect/uUv
// uniforms); batching is a later optimization. uProj is the same ortho the rect pass uses.

layout(location = 0) in vec2 aCorner;   // unit quad corner [0,1]

uniform mat4 uProj;
uniform vec4 uRect;   // glyph quad: x, y, w, h (panel pixels)
uniform vec4 uUv;     // glyph atlas rect: u0, v0, u1, v1 (0..1)

out vec2 vUv;

void main() {
    vUv = mix(uUv.xy, uUv.zw, aCorner);
    vec2 panelPos = uRect.xy + aCorner * uRect.zw;
    gl_Position = uProj * vec4(panelPos, 0.0, 1.0);
}
