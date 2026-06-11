#version 460 core

// UI quad fragment shader: a filled, optionally rounded rectangle with an optional border, and an
// optional texture (for Image elements). Rounded corners + border edge are antialiased with a
// signed-distance field evaluated in the rect's local pixel space, so a pill/chip looks clean at any
// size and the border is crisp. Colors arrive PREMULTIPLIED by the element's effective opacity (the
// render walker folds the tree opacity in), so the shader just blends straight alpha.

in vec2 vLocalPx;   // position within the rect, in pixels
in vec2 vUv;        // 0..1 across the rect

uniform vec2  uSize;          // rect size in pixels
uniform vec4  uFill;          // RGBA premultiplied
uniform vec4  uBorderColor;   // RGBA premultiplied
uniform float uBorderWidth;   // pixels (0 = none)
uniform vec4  uRadius;        // per-corner radius px: TL, TR, BR, BL
uniform int   uHasTexture;    // 1 = sample uTexture and multiply by uFill (used as a tint)
uniform sampler2D uTexture;

out vec4 FragColor;

// Signed distance from point p (centered rect coords) to a rounded box of half-size b with the
// four corner radii in r (x=TL ... selected per quadrant below). Negative inside.
float roundedBoxSDF(vec2 p, vec2 b, vec4 r) {
    // Pick the radius for the quadrant p is in: right side uses TR/BR, left uses TL/BL.
    r.xy = (p.x > 0.0) ? r.yz : r.xw;   // -> (top, bottom) for this side
    float rad = (p.y > 0.0) ? r.x : r.y;
    vec2 q = abs(p) - b + rad;
    return min(max(q.x, q.y), 0.0) + length(max(q, 0.0)) - rad;
}

void main() {
    vec2 halfSize = uSize * 0.5;
    vec2 p = vLocalPx - halfSize;          // center-origin coords

    float dist = roundedBoxSDF(p, halfSize, uRadius);

    // Antialias over ~1px using the SDF gradient.
    float aa = fwidth(dist);
    float coverage = 1.0 - smoothstep(-aa, aa, dist);
    if (coverage <= 0.0) discard;

    vec4 color = uFill;
    if (uHasTexture == 1)
        color = texture(uTexture, vUv) * uFill;

    // Border: blend toward the border color within uBorderWidth of the outer edge.
    if (uBorderWidth > 0.0 && uBorderColor.a > 0.0) {
        float borderEdge = dist + uBorderWidth;   // inner edge of the border ring
        float borderMix = smoothstep(-aa, aa, borderEdge);
        color = mix(color, uBorderColor, borderMix);
    }

    // Output PREMULTIPLIED alpha (rgb *= a) and fold the SDF coverage into alpha. Paired with a
    // premultiplied blend (One, OneMinusSrcAlpha) on the C# side, this antialiases the rounded edge
    // and border WITHOUT color fringing — straight-alpha blending leaks the border's RGB at partial
    // coverage (the pink halo bug). coverage multiplies the whole premultiplied color so a 50%-covered
    // edge contributes 50% color AND 50% alpha, which is exactly right.
    float a = color.a * coverage;
    FragColor = vec4(color.rgb * a, a);
}
