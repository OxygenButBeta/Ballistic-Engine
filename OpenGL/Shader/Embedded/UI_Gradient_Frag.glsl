#version 460 core

// UI gradient fragment shader: fills a rounded rect with a linear or radial gradient of up to 8 color
// stops, evaluated per-fragment. Reuses the same rounded-box SDF as the rect shader for crisp,
// antialiased corners. Stop colors arrive straight (opacity folded into the per-fragment output via
// uOpacity), and the result is premultiplied to match the One/OneMinusSrcAlpha blend.

in vec2 vLocalPx;   // position within the rect, in pixels
in vec2 vUv;        // 0..1 across the rect

uniform vec2  uSize;        // rect size in pixels
uniform vec4  uRadius;      // per-corner radius px: TL, TR, BR, BL
uniform float uOpacity;     // element effective opacity

uniform int   uKind;        // 0 = linear, 1 = radial
uniform float uAngle;       // linear: gradient direction in RADIANS (CSS 0deg=up, cw)
uniform vec2  uCenter;      // radial: center as 0..1 of the box
uniform vec2  uRadii;       // radial: x/y radii as 0..1 of half-box

uniform int   uStopCount;
uniform vec4  uStopColor[8];
uniform float uStopPos[8];

out vec4 FragColor;

float roundedBoxSDF(vec2 p, vec2 b, vec4 r) {
    r.xy = (p.x > 0.0) ? r.yz : r.xw;
    float rad = (p.y > 0.0) ? r.x : r.y;
    vec2 q = abs(p) - b + rad;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - rad;
}

// Sample the stop list at parameter t (0..1), linearly interpolating between adjacent stops.
vec4 sampleStops(float t) {
    if (uStopCount <= 1) return uStopColor[0];
    if (t <= uStopPos[0]) return uStopColor[0];
    for (int i = 0; i < uStopCount - 1; i++) {
        float a = uStopPos[i];
        float b = uStopPos[i + 1];
        if (t <= b) {
            float f = (b > a) ? (t - a) / (b - a) : 0.0;
            return mix(uStopColor[i], uStopColor[i + 1], clamp(f, 0.0, 1.0));
        }
    }
    return uStopColor[uStopCount - 1];
}

void main() {
    vec2 halfSize = uSize * 0.5;
    vec2 p = vLocalPx - halfSize;

    float dist = roundedBoxSDF(p, halfSize, uRadius);
    float aa = fwidth(dist);
    float coverage = 1.0 - smoothstep(-aa, aa, dist);
    if (coverage <= 0.0) discard;

    float t;
    if (uKind == 0) {
        // Linear: project the fragment onto the gradient axis. CSS 0deg points UP; the axis direction
        // is (sin, -cos) so 90deg points right. Map projection from [-0.5,0.5] of the box to [0,1].
        vec2 dir = vec2(sin(uAngle), -cos(uAngle));
        vec2 uvCentered = vUv - 0.5;
        // Scale by aspect so the projection is in normalized box space along the axis.
        float proj = dot(uvCentered, dir);
        // Axis half-length in this direction (so the gradient spans the box).
        float halfLen = abs(dir.x) * 0.5 + abs(dir.y) * 0.5;
        t = clamp(proj / (2.0 * halfLen) + 0.5, 0.0, 1.0);
    } else {
        // Radial: normalized elliptical distance from the center.
        vec2 d = (vUv - uCenter) / max(uRadii, vec2(1e-4));
        t = clamp(length(d), 0.0, 1.0);
    }

    vec4 color = sampleStops(t);
    float a = color.a * uOpacity * coverage;
    FragColor = vec4(color.rgb * a, a);
}
