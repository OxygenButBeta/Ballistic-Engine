#version 460 core

// UI quad vertex shader. Draws a single rectangle in PANEL pixel space (top-left origin, +Y down).
// The unit quad [0..1]x[0..1] is scaled/offset by uRect (x, y, w, h) and projected by uProj (an
// ortho matrix mapping panel pixels to clip space). uLocalPos carries the [0..1] corner to the
// fragment stage so it can do rounded-corner / border SDF math in local rect space.

layout(location = 0) in vec2 aCorner;   // unit quad corner in [0,1]

uniform mat4 uProj;
uniform vec4 uRect;   // x, y, width, height  (panel pixels)

out vec2 vLocalPx;    // position within the rect, in pixels (0..w, 0..h)
out vec2 vUv;         // 0..1 across the rect (for textures)

void main() {
    vUv = aCorner;
    vLocalPx = aCorner * uRect.zw;
    vec2 panelPos = uRect.xy + aCorner * uRect.zw;
    gl_Position = uProj * vec4(panelPos, 0.0, 1.0);
}
