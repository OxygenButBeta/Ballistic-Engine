#version 330 core

// Quad geometry (divisor 0): a unit quad, corners in [-0.5, 0.5], with uv.
layout(location = 0) in vec2 aCorner;
layout(location = 1) in vec2 aUv;

// Per-particle instance data (divisor 1). Locations 4-7 — NOT 2/3, which collide with the engine's
// reserved normal/tangent vertex attributes on this shared GL context.
layout(location = 4) in vec3 iPosition;  // world-space center
layout(location = 5) in float iSize;     // world-space billboard size
layout(location = 6) in vec4 iColor;     // RGBA (color/alpha pre-lerped over lifetime)
layout(location = 7) in float iRotation; // billboard roll, radians

out vec2 uv;
out vec4 color;

uniform mat4 view;
uniform mat4 projection;

void main() {
    uv = aUv;
    color = iColor;

    // Rotate the corner in the billboard plane (roll around the view direction).
    float s = sin(iRotation);
    float c = cos(iRotation);
    vec2 corner = vec2(aCorner.x * c - aCorner.y * s, aCorner.x * s + aCorner.y * c);

    // Billboard in VIEW space: the camera right/up axes ARE X/Y there, so offset the view-space
    // center by the corner directly — convention-independent, no camera-basis extraction needed.
    vec4 centerView = view * vec4(iPosition, 1.0);
    centerView.xy += corner * iSize;
    gl_Position = projection * centerView;
}
