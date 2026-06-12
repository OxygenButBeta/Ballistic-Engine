#version 330 core

in vec2 uv;
in vec4 color;

out vec4 fragColor;

uniform sampler2D Particle;
uniform bool HasTexture;

void main() {
    vec4 tex;
    if (HasTexture) {
        tex = texture(Particle, uv);
    } else {
        // Soft round dot fallback: radial alpha falloff so an untextured emitter still looks like
        // a glow blob, not a hard square.
        float d = length(uv - vec2(0.5)) * 2.0;        // 0 at center, 1 at edge
        float a = smoothstep(1.0, 0.0, d);             // soft edge
        tex = vec4(1.0, 1.0, 1.0, a);
    }

    fragColor = tex * color;
    // Premultiply-friendly: additive blend (SrcAlpha, One) and alpha blend (SrcAlpha, 1-SrcAlpha)
    // both read fragColor.rgb scaled by fragColor.a via the GL blend func set per system.
    if (fragColor.a < 0.003)
        discard;
}
