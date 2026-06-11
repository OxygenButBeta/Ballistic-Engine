#version 330 core

// Depth-of-field stage 2: bokeh gather at half-res. For each output pixel, sample a circular
// disk of taps; a neighbour contributes only when its own blur circle (CoC) is large enough to
// reach this pixel, which produces real bokeh discs around bright out-of-focus highlights
// instead of a uniform blur. Near (foreground) and far (background) fields are kept separate so
// a blurred foreground bleeds OVER the sharp midground but a blurred background never bleeds
// onto sharp foreground edges.

in vec2 TexCoords;
out vec4 FragColor;

uniform sampler2D cocColorTexture;  // half-res: rgb = color, a = signed CoC
uniform vec2  TexelSize;            // 1 / half-res dimensions
uniform float MaxCoc;              // same clamp as stage 1 (fraction of frame height)

const int   TAPS = 48;
const float GOLDEN = 2.39996323;   // golden-angle spiral for an even disk distribution

void main() {
    vec4 center = texture(cocColorTexture, TexCoords);
    float centerCoc = center.a;

    // Kernel radius in texels: CoC fraction-of-height -> pixels. The half-res buffer already
    // halves it; MaxCoc bounds the worst case so the loop cost is fixed.
    float maxRadiusPx = MaxCoc * (1.0 / TexelSize.y);

    vec3 farColor = center.rgb;
    float farWeight = 1.0;
    vec3 nearColor = vec3(0.0);
    float nearWeight = 0.0;

    // Seed the near field with the centre only if it is itself a foreground pixel.
    if (centerCoc < 0.0) {
        nearColor = center.rgb;
        nearWeight = 1.0;
    }

    for (int i = 0; i < TAPS; i++) {
        float t = (float(i) + 0.5) / float(TAPS);
        float r = sqrt(t);                       // uniform area distribution over the disk
        float a = float(i) * GOLDEN;
        vec2 dir = vec2(cos(a), sin(a)) * r;
        vec2 uv = TexCoords + dir * maxRadiusPx * TexelSize;

        vec4 s = texture(cocColorTexture, uv);
        float sampleCoc = abs(s.a);
        float tapDist = r * maxRadiusPx;          // how far this tap is, in pixels

        // A sample reaches the centre only if its blur circle is at least that wide.
        float reach = smoothstep(tapDist - 1.0, tapDist + 1.0, sampleCoc * (1.0 / TexelSize.y));

        if (s.a < 0.0) {
            // Foreground sample -> near field (it can spread over anything in front).
            nearColor += s.rgb * reach;
            nearWeight += reach;
        } else {
            // Background sample -> far field, but only blend toward samples at least as far
            // as the centre (so a sharp foreground edge does not pull in blurry background).
            float w = reach * step(centerCoc - 0.02, s.a);
            farColor += s.rgb * w;
            farWeight += w;
        }
    }

    farColor /= max(farWeight, 1e-4);
    nearColor /= max(nearWeight, 1e-4);

    // Foreground coverage: how strongly the near field occludes the far field at this pixel.
    float nearCoverage = clamp(nearWeight / float(TAPS) * 2.0, 0.0, 1.0);
    vec3 result = mix(farColor, nearColor, nearCoverage);

    // Pass the (unsigned, normalised) blur amount forward so the composite can cross-fade with
    // the sharp full-res image: max of the in-focus->out CoC and the foreground coverage.
    float blurAmount = max(clamp(abs(centerCoc) / MaxCoc, 0.0, 1.0), nearCoverage);
    FragColor = vec4(result, blurAmount);
}
