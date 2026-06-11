#version 460 core

// UI text fragment shader: samples the single-channel SDF glyph atlas and thresholds it for a crisp,
// antialiased edge at any scale. The atlas stores distance with the edge at ~0.5 (onedge_value 128 /
// 255). screen-space derivative gives the AA width so glyphs stay sharp whether scaled up on a 4K
// panel or down. Outputs PREMULTIPLIED alpha to match the rect pass's blend (One, OneMinusSrcAlpha).

in vec2 vUv;

uniform sampler2D uAtlas;
uniform vec4 uColor;    // text color, straight RGBA (opacity already folded by the walker)
uniform float uSpread;  // 0 = crisp glyph; >0 = expand coverage outward for a soft glow/shadow halo

out vec4 FragColor;

void main() {
    float dist = texture(uAtlas, vUv).r;   // 0..1, edge at ~0.5

    // The glyph edge is at 0.5; lowering the threshold by uSpread grows the shape outward (glow). The
    // AA width widens with the spread so a large glow stays soft rather than hard-edged.
    float edge = 0.5 - uSpread;
    float aa = fwidth(dist) + uSpread * 0.5;
    float alpha = smoothstep(edge - aa, edge + aa, dist);
    if (alpha <= 0.0) discard;

    float a = uColor.a * alpha;
    FragColor = vec4(uColor.rgb * a, a);   // premultiplied
}
