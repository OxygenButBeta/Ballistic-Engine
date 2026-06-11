#version 330 core

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D sourceTexture;
uniform vec2 sourceTexelSize;
uniform bool applyThreshold;
uniform float threshold;

vec3 SampleSrc(vec2 uv) {
    return max(texture(sourceTexture, uv).rgb, vec3(0.0));
}

// Down-weight very bright samples so single HDR fireflies do not flicker as huge blobs.
float KarisWeight(vec3 c) {
    return 1.0 / (1.0 + max(c.r, max(c.g, c.b)));
}

// Soft-knee threshold: keeps energy above 'threshold', fades smoothly below it.
vec3 Threshold(vec3 c) {
    float lum = max(c.r, max(c.g, c.b));
    float knee = threshold * 0.5;
    float soft = clamp(lum - threshold + knee, 0.0, 2.0 * knee);
    soft = soft * soft / (4.0 * knee + 1e-4);
    float contribution = max(soft, lum - threshold) / max(lum, 1e-4);
    return c * clamp(contribution, 0.0, 1.0);
}

void main() {
    vec2 uv = TexCoords;
    vec2 t = sourceTexelSize;

    // 13-tap downsample (Jimenez).
    vec3 a = SampleSrc(uv + t * vec2(-2.0,  2.0));
    vec3 b = SampleSrc(uv + t * vec2( 0.0,  2.0));
    vec3 c = SampleSrc(uv + t * vec2( 2.0,  2.0));
    vec3 d = SampleSrc(uv + t * vec2(-2.0,  0.0));
    vec3 e = SampleSrc(uv);
    vec3 f = SampleSrc(uv + t * vec2( 2.0,  0.0));
    vec3 g = SampleSrc(uv + t * vec2(-2.0, -2.0));
    vec3 h = SampleSrc(uv + t * vec2( 0.0, -2.0));
    vec3 i = SampleSrc(uv + t * vec2( 2.0, -2.0));
    vec3 j = SampleSrc(uv + t * vec2(-1.0,  1.0));
    vec3 k = SampleSrc(uv + t * vec2( 1.0,  1.0));
    vec3 l = SampleSrc(uv + t * vec2(-1.0, -1.0));
    vec3 m = SampleSrc(uv + t * vec2( 1.0, -1.0));

    vec3 color;
    if (applyThreshold) {
        // First (HDR) tap: average 2x2 groups with Karis weights to kill fireflies.
        vec3 g0 = (a + b + d + e) * 0.25;
        vec3 g1 = (b + c + e + f) * 0.25;
        vec3 g2 = (d + e + g + h) * 0.25;
        vec3 g3 = (e + f + h + i) * 0.25;
        vec3 g4 = (j + k + l + m) * 0.25;
        color = g0 * (0.125 * KarisWeight(g0))
              + g1 * (0.125 * KarisWeight(g1))
              + g2 * (0.125 * KarisWeight(g2))
              + g3 * (0.125 * KarisWeight(g3))
              + g4 * (0.5   * KarisWeight(g4));
        color = Threshold(color);
    }
    else {
        color = e * 0.125
              + (a + c + g + i) * 0.03125
              + (b + d + f + h) * 0.0625
              + (j + k + l + m) * 0.125;
    }

    FragColor = vec4(color, 1.0);
}
